package gateway

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"net"
	"time"

	"ai003/server/internal/room"
	"ai003/server/internal/rudp"
)

// udpSession is one client's reliable-UDP endpoint bound to a websocket session.
type udpSession struct {
	endpoint  *rudp.Endpoint
	sessionID string
	clientID  string
	roomID    string
}

// StartUDP opens the reliable-UDP room channel. It returns the bound address.
func (s *Server) StartUDP(addr string) (string, error) {
	conn, err := net.ListenPacket("udp", addr)
	if err != nil {
		return "", err
	}
	s.udpMu.Lock()
	s.udpConn = conn
	s.udpAddr = conn.LocalAddr().String()
	if s.udpTokens == nil {
		s.udpTokens = map[string]string{}
	}
	if s.udpSessions == nil {
		s.udpSessions = map[uint32]*udpSession{}
	}
	s.udpMu.Unlock()

	go s.udpLoop(conn)
	go s.udpTicker()
	s.log.Info("reliable udp room channel listening", "addr", s.udpAddr)
	return s.udpAddr, nil
}

// UDPAddr returns the advertised reliable-UDP address (empty when disabled).
func (s *Server) UDPAddr() string {
	s.udpMu.Lock()
	defer s.udpMu.Unlock()
	return s.udpAddr
}

// newUDPToken mints a one-time token that binds a UDP endpoint to a session.
func (s *Server) newUDPToken(sessionID string) string {
	var b [16]byte
	_, _ = rand.Read(b[:])
	token := hex.EncodeToString(b[:])
	s.udpMu.Lock()
	if s.udpTokens == nil {
		s.udpTokens = map[string]string{}
	}
	s.udpTokens[token] = sessionID
	s.udpMu.Unlock()
	return token
}

func (s *Server) udpLoop(conn net.PacketConn) {
	buf := make([]byte, 2048)
	for {
		n, remote, err := conn.ReadFrom(buf)
		if err != nil {
			select {
			case <-s.stop:
				return
			default:
				continue
			}
		}
		pkt := append([]byte(nil), buf[:n]...)
		session, typ, ok := rudp.SessionOf(pkt)
		if !ok {
			continue
		}
		if typ == rudp.TypeHello() {
			s.handleUDPHello(conn, remote, session, pkt)
			continue
		}
		s.udpMu.Lock()
		sess := s.udpSessions[session]
		s.udpMu.Unlock()
		if sess == nil {
			continue
		}
		for _, payload := range sess.endpoint.Handle(pkt) {
			s.handleUDPPayload(sess, payload)
		}
	}
}

func (s *Server) handleUDPHello(conn net.PacketConn, remote net.Addr, session uint32, pkt []byte) {
	var hello struct {
		Token string `json:"token"`
	}
	// Endpoint payload is the datagram after the header; the hello body is the
	// full JSON message kept in the payload field.
	body := payloadOf(pkt)
	if err := json.Unmarshal(body, &hello); err != nil {
		return
	}
	s.udpMu.Lock()
	sessionID, ok := s.udpTokens[hello.Token]
	if ok {
		delete(s.udpTokens, hello.Token)
	}
	s.udpMu.Unlock()
	if !ok {
		return
	}

	s.roomMu.Lock()
	info, exists := s.sessions[sessionID]
	s.roomMu.Unlock()
	if !exists {
		return
	}

	ep := rudp.NewEndpoint(conn, remote, session, rudp.Config{})
	ep.Handle(pkt) // seed receive sequence with the hello packet
	sess := &udpSession{endpoint: ep, sessionID: sessionID, clientID: info.clientID, roomID: info.roomID}

	s.udpMu.Lock()
	s.udpSessions[session] = sess
	s.udpMu.Unlock()

	// Switch the room subscription from websocket to reliable UDP.
	s.roomMu.Lock()
	if sub, ok := s.roomSubs[sessionID]; ok {
		sub.send = func(m Message) {
			if raw, err := json.Marshal(m); err == nil {
				_ = ep.Send(raw)
			}
		}
		s.roomSubs[sessionID] = sub
	}
	s.roomMu.Unlock()

	welcome, _ := json.Marshal(map[string]any{
		"type": "room.udp.welcome",
		"body": map[string]any{"sessionId": session, "roomId": info.roomID},
	})
	_ = ep.Send(welcome)
	s.log.Info("udp session bound", "session", sessionID, "remote", remote.String())
}

// handleUDPPayload dispatches one JSON envelope received over reliable UDP.
func (s *Server) handleUDPPayload(sess *udpSession, payload []byte) {
	var msg Message
	if err := json.Unmarshal(payload, &msg); err != nil {
		return
	}
	switch msg.Type {
	case "room.input":
		var p struct {
			Tick uint64 `json:"tick"`
			DX   int8   `json:"dx"`
			DZ   int8   `json:"dz"`
			But  uint8  `json:"buttons"`
		}
		if json.Unmarshal(msg.Body, &p) == nil {
			s.rooms.SetInput(sess.roomID, sess.clientID, p.Tick, room.Input{DX: p.DX, DZ: p.DZ, Buttons: p.But})
		}
	case "room.arena.cmd":
		var cmd room.Cmd
		if json.Unmarshal(msg.Body, &cmd) == nil {
			if !s.rooms.SubmitCmd(sess.roomID, sess.clientID, cmd) {
				s.log.Warn("udp arena cmd rejected", "room", sess.roomID, "player", sess.clientID, "seq", cmd.Seq)
			}
		}
	case "room.arena.ack":
		var p struct {
			Seq uint32 `json:"seq"`
		}
		if json.Unmarshal(msg.Body, &p) == nil {
			s.roomMu.Lock()
			sub := s.roomSubs[sess.sessionID]
			if p.Seq > sub.ack {
				sub.ack = p.Seq
				s.roomSubs[sess.sessionID] = sub
			}
			s.roomMu.Unlock()
		}
	}
}

func (s *Server) udpTicker() {
	ticker := time.NewTicker(10 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-s.stop:
			return
		case <-ticker.C:
			s.udpMu.Lock()
			sessions := make([]*udpSession, 0, len(s.udpSessions))
			for _, sess := range s.udpSessions {
				sessions = append(sessions, sess)
			}
			s.udpMu.Unlock()
			for _, sess := range sessions {
				for _, payload := range sess.endpoint.Tick() {
					s.handleUDPPayload(sess, payload)
				}
			}
		}
	}
}

// payloadOf strips the fixed RUDP header and returns the payload bytes.
func payloadOf(pkt []byte) []byte {
	const headerSize = 28
	if len(pkt) < headerSize {
		return nil
	}
	return pkt[headerSize:]
}
