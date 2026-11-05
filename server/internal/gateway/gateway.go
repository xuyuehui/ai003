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

	"ai003/server/internal/room"

	"github.com/gorilla/websocket"
)

const (
	// Version is the server build version, reported by /healthz and welcome.
	Version = "0.1.0"

	// ProtocolVersion is bumped whenever the envelope schema changes.
	// v2: join / move / snapshot (server-authoritative player state).
	// v3: room.enter / room.entered / room.input / room.frame / room.leave (frame sync).
	ProtocolVersion = 3

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

	rooms    *room.Manager
	roomMu   sync.Mutex
	roomSubs map[string]roomSub
	sessions map[string]sessionInfo // session id -> identity + room
	metrics  *metricsState

	world   *WorldInfo
	worldMu sync.RWMutex
	started time.Time

	stop     chan struct{}
	stopOnce sync.Once
}

// sessionInfo ties a websocket session to the client's persistent id and room.
type sessionInfo struct {
	clientID string
	roomID   string
}

// roomSub is one connection's subscription to a specific room.
type roomSub struct {
	roomID string
	send   func(Message)
}

// New builds a Server that logs through logger (which may be nil).
func New(logger *slog.Logger) *Server {
	return NewWithWorld(logger, "")
}

// NewWithWorld is New plus optional world metadata (world.json directory).
func NewWithWorld(logger *slog.Logger, worldDir string) *Server {
	if logger == nil {
		logger = slog.Default()
	}
	s := &Server{log: logger, mux: http.NewServeMux(), metrics: newMetrics(), started: time.Now()}
	if worldDir != "" {
		info, err := LoadWorldInfo(worldDir)
		if err != nil {
			logger.Warn("world metadata not loaded", "dir", worldDir, "error", err)
		} else {
			s.world = info
			logger.Info("world loaded", "name", info.Name, "sizeMeters", info.SizeMeters,
				"resolution", info.Resolution, "chunks", info.ChunksPerAxis*info.ChunksPerAxis)
		}
	}
	s.up = websocket.Upgrader{
		ReadBufferSize:  4096,
		WriteBufferSize: 4096,
		// Development server: both clients are local or on the LAN.
		CheckOrigin: func(*http.Request) bool { return true },
	}
	s.hub = NewHub(logger, SnapshotRate)
	s.hub.metrics = s.metrics
	s.hub.Start()
	s.rooms = room.NewManager(room.DefaultCapacity)
	s.roomSubs = make(map[string]roomSub)
	s.sessions = make(map[string]sessionInfo)
	s.stop = make(chan struct{})
	go s.roomLoop()
	s.mux.HandleFunc("GET /healthz", s.handleHealth)
	s.mux.HandleFunc("GET /demo", s.handleDemo)
	s.mux.HandleFunc("GET /world", s.handleWorld)
	s.mux.HandleFunc("GET /dashboard", s.handleDashboard)
	s.mux.HandleFunc("GET /ws", s.handleWS)
	return s
}

// Close stops background work (snapshot hub and room tick loop).
func (s *Server) Close() {
	s.stopOnce.Do(func() { close(s.stop) })
	s.hub.Close()
}

// roomLoop advances every deterministic combat room at a fixed 20 Hz.
func (s *Server) roomLoop() {
	ticker := time.NewTicker(time.Duration(room.TickMillis) * time.Millisecond)
	defer ticker.Stop()

	for {
		select {
		case <-s.stop:
			return
		case <-ticker.C:
			s.rooms.CleanupEmpty(room.EmptyRoomGraceTicks)
			for _, roomID := range s.rooms.RoomIDs() {
				frame, ok := s.rooms.Step(roomID)
				if !ok || len(frame.Players) == 0 {
					continue
				}
				s.metrics.noteRoomFrame(frame)

				s.roomMu.Lock()
				subs := make([]func(Message), 0, len(s.roomSubs))
				for _, sub := range s.roomSubs {
					if sub.roomID == roomID {
						subs = append(subs, sub.send)
					}
				}
				s.roomMu.Unlock()
				if len(subs) == 0 {
					continue
				}

				msg, err := NewMessage("room.frame", 0, frame)
				if err != nil {
					s.log.Error("encode room frame", "error", err)
					continue
				}
				for _, send := range subs {
					send(msg)
				}
			}
		}
	}
}

// leaveRoom detaches a session. A permanent leave removes the player; a plain
// disconnect only marks it offline so a reconnect can resume within the grace.
func (s *Server) leaveRoom(id string, permanent bool) {
	s.roomMu.Lock()
	info := s.sessions[id]
	delete(s.sessions, id)
	delete(s.roomSubs, id)
	s.roomMu.Unlock()

	if info.roomID == "" {
		return
	}
	if permanent {
		s.rooms.Leave(info.roomID, info.clientID)
	} else {
		s.rooms.MarkOffline(info.roomID, info.clientID)
	}
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
	clientID := sessionID
	log := s.log.With("session", sessionID, "remote", r.RemoteAddr)
	log.Info("client connected")
	defer func() {
		s.leaveRoom(sessionID, false)
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
		if err := conn.WriteJSON(m); err != nil {
			return err
		}
		s.metrics.noteMessage(m)
		return nil
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
				Client   string `json:"client"`
				Name     string `json:"name"`
				ClientID string `json:"clientId"`
			}
			if len(msg.Body) > 0 {
				if err := json.Unmarshal(msg.Body, &body); err != nil {
					writeError(msg.Seq, msg.Type, "invalid join body: "+err.Error())
					continue
				}
			}
			if body.ClientID != "" {
				if len(body.ClientID) > 64 {
					writeError(msg.Seq, msg.Type, "clientId too long")
					continue
				}
				clientID = body.ClientID
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

		case "room.enter":
			var body struct {
				Name     string `json:"name"`
				Room     string `json:"room"`
				ClientID string `json:"clientId"`
			}
			if len(msg.Body) > 0 {
				if err := json.Unmarshal(msg.Body, &body); err != nil {
					writeError(msg.Seq, msg.Type, "invalid room.enter body: "+err.Error())
					continue
				}
			}
			if body.ClientID != "" {
				if len(body.ClientID) > 64 {
					writeError(msg.Seq, msg.Type, "clientId too long")
					continue
				}
				clientID = body.ClientID
			}

			// One persistent client id may only have one online fighter. A second
			// session with the same id is rejected instead of silently taking over.
			if existing, online := s.rooms.FindOnline(clientID); online {
				s.roomMu.Lock()
				info, sameSession := s.sessions[sessionID]
				s.roomMu.Unlock()
				if !sameSession || info.roomID != existing {
					writeError(msg.Seq, msg.Type, "client already online")
					continue
				}
			}

			roomID, state, err := s.rooms.Enter(body.Room, clientID, body.Name)
			if err != nil {
				writeError(msg.Seq, msg.Type, err.Error())
				continue
			}
			tick, players, _ := s.rooms.Snapshot(roomID)

			s.roomMu.Lock()
			s.sessions[sessionID] = sessionInfo{clientID: clientID, roomID: roomID}
			s.roomSubs[sessionID] = roomSub{roomID: roomID, send: func(m Message) { _ = write(m) }}
			s.roomMu.Unlock()

			entered, err := NewMessage("room.entered", msg.Seq, map[string]any{
				"roomId":              roomID,
				"playerId":            clientID,
				"tick":                tick,
				"state":               state,
				"players":             players,
				"tickRate":            room.TickRate,
				"tickMillis":          room.TickMillis,
				"arenaHalfMM":         room.ArenaHalfMM,
				"speedMMPerTick":      room.SpeedMMPerTick,
				"diagonalMMPerTick":   room.DiagonalMMPerTick,
				"attackRangeMM":       room.AttackRangeMM,
				"attackDamage":        room.AttackDamage,
				"attackCooldownTicks": room.AttackCooldownTicks,
				"maxHP":               room.MaxHP,
				"respawnTicks":        room.RespawnTicks,
				"buttonAttack":        room.ButtonAttack,
			})
			if err != nil {
				log.Error("encode room.entered", "error", err)
				return
			}
			if err := write(entered); err != nil {
				log.Warn("write room.entered", "error", err)
				return
			}
			log.Info("room entered", "player", clientID, "session", sessionID, "room", roomID, "tick", tick)

		case "room.input":
			var payload struct {
				Tick    uint64 `json:"tick"`
				DX      int8   `json:"dx"`
				DZ      int8   `json:"dz"`
				Buttons uint8  `json:"buttons"`
			}
			if err := json.Unmarshal(msg.Body, &payload); err != nil {
				writeError(msg.Seq, msg.Type, "invalid room.input body: "+err.Error())
				continue
			}
			s.roomMu.Lock()
			info := s.sessions[sessionID]
			s.roomMu.Unlock()
			if info.roomID == "" {
				writeError(msg.Seq, msg.Type, "room.enter before room.input")
				continue
			}
			in := room.Input{DX: payload.DX, DZ: payload.DZ, Buttons: payload.Buttons}
			if !s.rooms.SetInput(info.roomID, info.clientID, payload.Tick, in) {
				writeError(msg.Seq, msg.Type, "room.input for unknown player")
				continue
			}

		case "room.list":
			list, err := NewMessage("room.list", msg.Seq, map[string]any{"rooms": s.rooms.List()})
			if err != nil {
				log.Error("encode room.list", "error", err)
				return
			}
			if err := write(list); err != nil {
				log.Warn("write room.list", "error", err)
				return
			}

		case "room.leave":
			s.roomMu.Lock()
			info := s.sessions[sessionID]
			s.roomMu.Unlock()
			s.leaveRoom(sessionID, true)
			left, err := NewMessage("room.left", msg.Seq, map[string]any{"roomId": info.roomID})
			if err == nil {
				_ = write(left)
			}
			log.Info("room left", "player", info.clientID, "room", info.roomID)

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
