// Package gateway implements the single network entry point that both
// clients 鈥?Unity 6 and Unreal Engine 5 鈥?connect to.
//
// Transport: WebSocket at /ws, one JSON envelope per text frame:
//
//	{"type":"ping","seq":1,"body":{"client":"unity"}}
//
// The server answers every ping with a pong carrying the same seq. This is
// deliberately tiny: it exists to prove the toolchain and the three runtimes
// can talk to each other, not to model gameplay yet.
package gateway

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"log/slog"
	"net/http"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const (
	// Version is the server build version, reported by /healthz and welcome.
	Version = "0.1.0"

	// ProtocolVersion is bumped whenever the envelope schema changes.
	// v2: join / move / snapshot (server-authoritative player state).
	ProtocolVersion = 2

	readLimit   = 64 << 10
	writeWait   = 10 * time.Second
	idleTimeout = 90 * time.Second
)

// Message is the JSON envelope shared by all three parts.
type Message struct {
	Type string          `json:"type"`
	Seq  int64           `json:"seq,omitempty"`
	Body json.RawMessage `json:"body,omitempty"`
}

// NewMessage marshals v into the body of an envelope.
func NewMessage(typ string, seq int64, v any) (Message, error) {
	m := Message{Type: typ, Seq: seq}
	if v == nil {
		return m, nil
	}
	body, err := json.Marshal(v)
	if err != nil {
		return Message{}, err
	}
	m.Body = body
	return m, nil
}

// Server owns the HTTP routes.
type Server struct {
	log *slog.Logger
	mux *http.ServeMux
	up  websocket.Upgrader
	hub *Hub
}

// New builds a Server that logs through logger (which may be nil).
func New(logger *slog.Logger) *Server {
	if logger == nil {
		logger = slog.Default()
	}
	s := &Server{log: logger, mux: http.NewServeMux()}
	s.up = websocket.Upgrader{
		ReadBufferSize:  4096,
		WriteBufferSize: 4096,
		// Development server: both clients are local or on the LAN.
		CheckOrigin: func(*http.Request) bool { return true },
	}
	s.hub = NewHub(logger, SnapshotRate)
	s.hub.Start()
	s.mux.HandleFunc("GET /healthz", s.handleHealth)
	s.mux.HandleFunc("GET /ws", s.handleWS)
	return s
}

// Close stops background work (the snapshot broadcast loop).
func (s *Server) Close() { s.hub.Close() }

// Handler returns the root HTTP handler.
func (s *Server) Handler() http.Handler { return s.mux }

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"status":          "ok",
		"service":         "ai003-server",
		"version":         Version,
		"protocolVersion": ProtocolVersion,
		"serverTime":      time.Now().UTC().Format(time.RFC3339Nano),
	})
}

func (s *Server) handleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := s.up.Upgrade(w, r, nil)
	if err != nil {
		s.log.Warn("websocket upgrade failed", "error", err, "remote", r.RemoteAddr)
		return
	}

	sessionID := newSessionID()
	log := s.log.With("session", sessionID, "remote", r.RemoteAddr)
	log.Info("client connected")
	defer func() {
		s.hub.Leave(sessionID)
		_ = conn.Close()
		log.Info("client disconnected")
	}()

	conn.SetReadLimit(readLimit)
	_ = conn.SetReadDeadline(time.Now().Add(idleTimeout))
	conn.SetPongHandler(func(string) error {
		return conn.SetReadDeadline(time.Now().Add(idleTimeout))
	})

	var writeMu sync.Mutex
	write := func(m Message) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		if err := conn.SetWriteDeadline(time.Now().Add(writeWait)); err != nil {
			return err
		}
		return conn.WriteJSON(m)
	}

	welcome, err := NewMessage("welcome", 0, map[string]any{
		"sessionId":       sessionID,
		"serverVersion":   Version,
		"protocolVersion": ProtocolVersion,
		"snapshotRateMs":  SnapshotRate.Milliseconds(),
		"serverTime":      time.Now().UnixMilli(),
	})
	if err != nil {
		log.Error("encode welcome", "error", err)
		return
	}
	if err := write(welcome); err != nil {
		log.Warn("write welcome", "error", err)
		return
	}

	writeError := func(seq int64, typ, text string) {
		reply, err := NewMessage("error", seq, map[string]any{"message": text})
		if err == nil {
			_ = write(reply)
		}
		log.Warn("rejected message", "type", typ, "error", text)
	}

	for {
		var msg Message
		if err := conn.ReadJSON(&msg); err != nil {
			if websocket.IsUnexpectedCloseError(err, websocket.CloseNormalClosure, websocket.CloseGoingAway) {
				log.Warn("read", "error", err)
			}
			return
		}
		_ = conn.SetReadDeadline(time.Now().Add(idleTimeout))

		switch msg.Type {
		case "ping":
			pong, err := NewMessage("pong", msg.Seq, map[string]any{
				"serverTime": time.Now().UnixMilli(),
			})
			if err != nil {
				log.Error("encode pong", "error", err)
				return
			}
			if err := write(pong); err != nil {
				log.Warn("write pong", "error", err)
				return
			}
			log.Debug("ping", "seq", msg.Seq)

		case "join":
			var body struct {
				Client string `json:"client"`
				Name   string `json:"name"`
			}
			if len(msg.Body) > 0 {
				if err := json.Unmarshal(msg.Body, &body); err != nil {
					writeError(msg.Seq, msg.Type, "invalid join body: "+err.Error())
					continue
				}
			}
			if body.Client == "" {
				body.Client = "unknown"
			}
			player := s.hub.Join(sessionID, body.Name, body.Client)
			s.hub.Subscribe(sessionID, func(m Message) { _ = write(m) })

			joined, err := NewMessage("joined", msg.Seq, map[string]any{
				"playerId":        player.ID,
				"name":            player.Name,
				"protocolVersion": ProtocolVersion,
				"snapshotRateMs":  SnapshotRate.Milliseconds(),
			})
			if err != nil {
				log.Error("encode joined", "error", err)
				return
			}
			if err := write(joined); err != nil {
				log.Warn("write joined", "error", err)
				return
			}
			log.Info("player joined", "player", player.ID, "client", player.Client, "name", player.Name)

		case "move":
			var state MoveState
			if err := json.Unmarshal(msg.Body, &state); err != nil {
				writeError(msg.Seq, msg.Type, "invalid move body: "+err.Error())
				continue
			}
			if _, ok := s.hub.ApplyMove(sessionID, state); !ok {
				writeError(msg.Seq, msg.Type, "join before move")
				continue
			}

		default:
			writeError(msg.Seq, msg.Type, "unknown message type: "+msg.Type)
		}
	}
}

func newSessionID() string {
	var b [8]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "session"
	}
	return hex.EncodeToString(b[:])
}

