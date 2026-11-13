package gateway

import (
	"bytes"
	"encoding/json"
	"log/slog"
	"net"
	"strings"
	"testing"
	"time"

	"ai003/server/internal/room"
	"ai003/server/internal/rudp"
)

// udpCapture is a net.PacketConn that records writes instead of sending them.
type udpCapture struct {
	sent [][]byte
}

func (c *udpCapture) ReadFrom(p []byte) (int, net.Addr, error) { return 0, nil, nil }
func (c *udpCapture) WriteTo(p []byte, addr net.Addr) (int, error) {
	c.sent = append(c.sent, append([]byte(nil), p...))
	return len(p), nil
}
func (c *udpCapture) Close() error                     { return nil }
func (c *udpCapture) LocalAddr() net.Addr              { return testUDPAddr("capture") }
func (c *udpCapture) SetDeadline(time.Time) error      { return nil }
func (c *udpCapture) SetReadDeadline(time.Time) error  { return nil }
func (c *udpCapture) SetWriteDeadline(time.Time) error { return nil }

type testUDPAddr string

func (a testUDPAddr) Network() string { return "sim" }
func (a testUDPAddr) String() string  { return string(a) }

// newDispatchServer builds a Server without the background tick loops so UDP
// payload tests are deterministic: rooms only advance when the test steps them.
func newDispatchServer(t *testing.T) (*Server, *bytes.Buffer) {
	t.Helper()
	logBuf := &bytes.Buffer{}
	logger := slog.New(slog.NewTextHandler(logBuf, &slog.HandlerOptions{Level: slog.LevelDebug}))
	hub := NewHub(logger, SnapshotRate) // not started: no broadcast goroutine
	s := &Server{
		log:         logger,
		hub:         hub,
		rooms:       room.NewManager(room.DefaultCapacity),
		roomSubs:    map[string]roomSub{},
		sessions:    map[string]sessionInfo{},
		metrics:     newMetrics(),
		udpTokens:   map[string]string{},
		udpSessions: map[uint32]*udpSession{},
		stop:        make(chan struct{}),
	}
	t.Cleanup(s.Close)
	return s, logBuf
}

func mustMessage(t *testing.T, typ string, body any) []byte {
	t.Helper()
	msg, err := NewMessage(typ, 1, body)
	if err != nil {
		t.Fatalf("encode %s: %v", typ, err)
	}
	raw, err := json.Marshal(msg)
	if err != nil {
		t.Fatalf("marshal %s: %v", typ, err)
	}
	return raw
}

func TestUDPPayloadRoomInputMovesPlayer(t *testing.T) {
	s, _ := newDispatchServer(t)
	roomID, _, err := s.rooms.Enter("", "p1", "P")
	if err != nil {
		t.Fatalf("enter room: %v", err)
	}
	sess := &udpSession{sessionID: "ws-1", clientID: "p1", roomID: roomID}

	_, before, ok := s.rooms.Snapshot(roomID)
	if !ok || len(before) != 1 {
		t.Fatalf("snapshot before = %+v ok=%v", before, ok)
	}
	startX := before[0].X

	payload := mustMessage(t, "room.input", map[string]any{"tick": 0, "dx": 1, "dz": 0, "buttons": 0})
	s.handleUDPPayload(sess, payload)

	frame, ok := s.rooms.Step(roomID)
	if !ok || len(frame.Players) != 1 {
		t.Fatalf("step = %+v ok=%v", frame, ok)
	}
	if got := frame.Players[0].X; got <= startX {
		t.Fatalf("X after room.input = %d, want > %d", got, startX)
	}
	if got := frame.Players[0].FacingX; got != 1 {
		t.Fatalf("FacingX = %d, want 1 (input must reach the room)", got)
	}
}

func TestUDPPayloadArenaCmdQueuesAndIsAcked(t *testing.T) {
	s, _ := newDispatchServer(t)
	if _, _, err := s.rooms.EnterSync("duel", "a", "A", room.SyncArena); err != nil {
		t.Fatalf("enter arena: %v", err)
	}
	sess := &udpSession{sessionID: "ws-2", clientID: "a", roomID: "duel"}

	cmd := room.Cmd{Seq: 7, Msec: 16, Yaw: 0, Forward: 1}
	s.handleUDPPayload(sess, mustMessage(t, "room.arena.cmd", cmd))

	snap, ok := s.rooms.ArenaStep("duel")
	if !ok {
		t.Fatal("ArenaStep failed for a known arena room")
	}
	if len(snap.Players) != 1 || snap.Players[0].Origin[2] == 0 {
		t.Fatalf("snapshot player = %+v, want forward movement on Z", snap.Players)
	}
	// Step's own snapshot carries no per-player ack; the ack is applied when the
	// snapshot is addressed to a client (SnapshotFor / DeltaFor).
	acked, ok := s.rooms.ArenaSnapshotFor("duel", "a")
	if !ok {
		t.Fatal("ArenaSnapshotFor failed")
	}
	if acked.Ack != 7 {
		t.Fatalf("snapshot ack = %d, want 7 (cmd seq must be acked)", acked.Ack)
	}

	// The next tick repeats the last cmd (no new input): the player keeps
	// moving and the ack stays at 7.
	next, ok := s.rooms.ArenaStep("duel")
	if !ok {
		t.Fatal("second ArenaStep failed")
	}
	nextAcked, ok := s.rooms.ArenaSnapshotFor("duel", "a")
	if !ok {
		t.Fatal("ArenaSnapshotFor failed after second step")
	}
	if nextAcked.Ack != 7 {
		t.Fatalf("second snapshot ack = %d, want 7", nextAcked.Ack)
	}
	if next.Players[0].Origin[2] <= acked.Players[0].Origin[2] {
		t.Fatalf("repeated cmd did not move the player: %d -> %d",
			acked.Players[0].Origin[2], next.Players[0].Origin[2])
	}
}

func TestUDPPayloadStaleArenaCmdIsRejectedAndLogged(t *testing.T) {
	s, logBuf := newDispatchServer(t)
	if _, _, err := s.rooms.EnterSync("duel", "a", "A", room.SyncArena); err != nil {
		t.Fatalf("enter arena: %v", err)
	}
	sess := &udpSession{sessionID: "ws-2", clientID: "a", roomID: "duel"}
	cmd := room.Cmd{Seq: 3, Msec: 16, Forward: 1}

	s.handleUDPPayload(sess, mustMessage(t, "room.arena.cmd", cmd))
	if _, ok := s.rooms.ArenaStep("duel"); !ok {
		t.Fatal("ArenaStep failed")
	}

	// Replaying an already executed seq must be dropped and reported.
	s.handleUDPPayload(sess, mustMessage(t, "room.arena.cmd", cmd))
	if !strings.Contains(logBuf.String(), "udp arena cmd rejected") {
		t.Fatalf("rejected cmd was not logged; log = %q", logBuf.String())
	}
	if _, ok := s.rooms.ArenaStep("duel"); !ok {
		t.Fatal("ArenaStep failed")
	}
	snap, ok := s.rooms.ArenaSnapshotFor("duel", "a")
	if !ok {
		t.Fatal("ArenaSnapshotFor failed")
	}
	if snap.Ack != 3 {
		t.Fatalf("ack after rejected replay = %d, want 3", snap.Ack)
	}
}

func TestUDPPayloadArenaAckOnlyMovesForward(t *testing.T) {
	s, _ := newDispatchServer(t)
	s.roomSubs["ws-3"] = roomSub{roomID: "duel", playerID: "a", ack: 0}
	sess := &udpSession{sessionID: "ws-3", clientID: "a", roomID: "duel"}

	s.handleUDPPayload(sess, mustMessage(t, "room.arena.ack", map[string]uint32{"seq": 5}))
	if got := s.roomSubs["ws-3"].ack; got != 5 {
		t.Fatalf("ack = %d, want 5", got)
	}

	s.handleUDPPayload(sess, mustMessage(t, "room.arena.ack", map[string]uint32{"seq": 3}))
	if got := s.roomSubs["ws-3"].ack; got != 5 {
		t.Fatalf("ack rewound to %d, want 5 (acks only move forward)", got)
	}

	s.handleUDPPayload(sess, mustMessage(t, "room.arena.ack", map[string]uint32{"seq": 9}))
	if got := s.roomSubs["ws-3"].ack; got != 9 {
		t.Fatalf("ack = %d, want 9", got)
	}
}

func TestUDPPayloadIgnoresGarbage(t *testing.T) {
	s, _ := newDispatchServer(t)
	roomID, _, err := s.rooms.Enter("", "p1", "P")
	if err != nil {
		t.Fatalf("enter room: %v", err)
	}
	sess := &udpSession{sessionID: "ws-1", clientID: "p1", roomID: roomID}

	tests := []struct {
		name    string
		payload []byte
	}{
		{"not json", []byte("not json at all")},
		{"empty", nil},
		{"empty object", []byte(`{}`)},
		{"unknown type", []byte(`{"type":"room.teleport","body":{"x":1}}`)},
		{"input body is a string", []byte(`{"type":"room.input","body":"nope"}`)},
		{"arena cmd body is an array", []byte(`{"type":"room.arena.cmd","body":[1,2,3]}`)},
		{"arena ack body is a string", []byte(`{"type":"room.arena.ack","body":"nope"}`)},
	}

	_, before, ok := s.rooms.Snapshot(roomID)
	if !ok || len(before) != 1 {
		t.Fatalf("snapshot before = %+v ok=%v", before, ok)
	}
	startX, startZ := before[0].X, before[0].Z

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			s.handleUDPPayload(sess, tc.payload)
		})
	}

	frame, ok := s.rooms.Step(roomID)
	if !ok || len(frame.Players) != 1 {
		t.Fatalf("step = %+v ok=%v", frame, ok)
	}
	if frame.Players[0].X != startX || frame.Players[0].Z != startZ {
		t.Fatalf("garbage payload moved the player: (%d,%d) -> (%d,%d)",
			startX, startZ, frame.Players[0].X, frame.Players[0].Z)
	}
	if _, ok := s.roomSubs["ws-1"]; ok {
		t.Fatal("garbage payload created a room subscription")
	}
}

func TestPayloadOfStripsTheRudpHeader(t *testing.T) {
	const headerSize = 28
	body := []byte(`{"token":"abc"}`)
	buf := make([]byte, headerSize+len(body))
	copy(buf[headerSize:], body)

	tests := []struct {
		name string
		pkt  []byte
		want []byte
	}{
		{"exact header only", make([]byte, headerSize), []byte{}},
		{"header plus body", buf, body},
		{"one byte short", make([]byte, headerSize-1), nil},
		{"nil", nil, nil},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := payloadOf(tc.pkt)
			if !bytes.Equal(got, tc.want) {
				t.Fatalf("payloadOf = %q, want %q", got, tc.want)
			}
		})
	}

	// Cross-package contract: the strip offset must match rudp's real header,
	// which is the whole reason the hello handshake works.
	conn := &udpCapture{}
	ep := rudp.NewEndpoint(conn, testUDPAddr("gateway"), 1, rudp.Config{})
	if err := ep.SendHello(body); err != nil {
		t.Fatalf("SendHello: %v", err)
	}
	if len(conn.sent) != 1 {
		t.Fatalf("SendHello wrote %d datagrams", len(conn.sent))
	}
	if got := payloadOf(conn.sent[0]); !bytes.Equal(got, body) {
		t.Fatalf("payloadOf(rudp hello) = %q, want %q", got, body)
	}
	if session, typ, ok := rudp.SessionOf(conn.sent[0]); !ok || session != 1 || typ != rudp.TypeHello() {
		t.Fatalf("SessionOf(hello) = (%d,%d,%v)", session, typ, ok)
	}
}
