// Package gateway implements the single network entry point that both
// clients — Unity 6 and Unreal Engine 5 — connect to.
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
	ProtocolVersion = 1

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
	s.mux.HandleFunc("GET /healthz", s.handleHealth)
	s.mux.HandleFunc("GET /ws", s.handleWS)
	return s
}

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
		default:
			reply, err := NewMessage("error", msg.Seq, map[string]any{
				"message": "unknown message type: " + msg.Type,
			})
			if err == nil {
				_ = write(reply)
			}
			log.Warn("unknown message type", "type", msg.Type, "seq", msg.Seq)
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
