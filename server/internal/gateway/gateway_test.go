package gateway

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func testLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(io.Discard, nil))
}

func dial(t *testing.T, serverURL string) *websocket.Conn {
	t.Helper()
	wsURL := "ws" + strings.TrimPrefix(serverURL, "http") + "/ws"
	conn, _, err := websocket.DefaultDialer.Dial(wsURL, nil)
	if err != nil {
		t.Fatalf("dial %s: %v", wsURL, err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	return conn
}

func TestHealthz(t *testing.T) {
	srv := httptest.NewServer(New(testLogger()).Handler())
	defer srv.Close()

	res, err := srv.Client().Get(srv.URL + "/healthz")
	if err != nil {
		t.Fatalf("GET /healthz: %v", err)
	}
	defer res.Body.Close()

	if res.StatusCode != 200 {
		t.Fatalf("status = %d, want 200", res.StatusCode)
	}
	var body struct {
		Status          string `json:"status"`
		ProtocolVersion int    `json:"protocolVersion"`
	}
	if err := json.NewDecoder(res.Body).Decode(&body); err != nil {
		t.Fatalf("decode healthz: %v", err)
	}
	if body.Status != "ok" || body.ProtocolVersion != ProtocolVersion {
		t.Fatalf("unexpected health payload: %+v", body)
	}
}

func TestWelcomeThenPong(t *testing.T) {
	srv := httptest.NewServer(New(testLogger()).Handler())
	defer srv.Close()

	conn := dial(t, srv.URL)
	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))

	var welcome Message
	if err := conn.ReadJSON(&welcome); err != nil {
		t.Fatalf("read welcome: %v", err)
	}
	if welcome.Type != "welcome" {
		t.Fatalf("type = %q, want welcome", welcome.Type)
	}
	var welcomeBody struct {
		SessionID string `json:"sessionId"`
	}
	if err := json.Unmarshal(welcome.Body, &welcomeBody); err != nil || welcomeBody.SessionID == "" {
		t.Fatalf("welcome body missing sessionId: %s (%v)", welcome.Body, err)
	}

	ping, err := NewMessage("ping", 42, map[string]string{"client": "test"})
	if err != nil {
		t.Fatalf("build ping: %v", err)
	}
	if err := conn.WriteJSON(ping); err != nil {
		t.Fatalf("write ping: %v", err)
	}

	var pong Message
	if err := conn.ReadJSON(&pong); err != nil {
		t.Fatalf("read pong: %v", err)
	}
	if pong.Type != "pong" || pong.Seq != 42 {
		t.Fatalf("reply = %+v, want pong seq 42", pong)
	}
	var pongBody struct {
		ServerTime int64 `json:"serverTime"`
	}
	if err := json.Unmarshal(pong.Body, &pongBody); err != nil || pongBody.ServerTime == 0 {
		t.Fatalf("pong body missing serverTime: %s (%v)", pong.Body, err)
	}
}

func TestUnknownTypeReturnsError(t *testing.T) {
	srv := httptest.NewServer(New(testLogger()).Handler())
	defer srv.Close()

	conn := dial(t, srv.URL)
	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))

	var welcome Message
	if err := conn.ReadJSON(&welcome); err != nil {
		t.Fatalf("read welcome: %v", err)
	}
	if err := conn.WriteJSON(Message{Type: "nope", Seq: 9}); err != nil {
		t.Fatalf("write: %v", err)
	}
	var reply Message
	if err := conn.ReadJSON(&reply); err != nil {
		t.Fatalf("read error reply: %v", err)
	}
	if reply.Type != "error" || reply.Seq != 9 {
		t.Fatalf("reply = %+v, want error seq 9", reply)
	}
}

// readWelcome consumes the welcome frame so the caller can start protocol v2.
func readWelcome(t *testing.T, conn messageReader) {
	t.Helper()
	if err := conn.SetReadDeadline(time.Now().Add(3 * time.Second)); err != nil {
		t.Fatalf("deadline: %v", err)
	}
	var welcome Message
	if err := conn.ReadJSON(&welcome); err != nil {
		t.Fatalf("read welcome: %v", err)
	}
	if welcome.Type != "welcome" {
		t.Fatalf("type = %q, want welcome", welcome.Type)
	}
}

func TestSnapshotCarriesBothPlayers(t *testing.T) {
	srv := httptest.NewServer(New(testLogger()).Handler())
	defer srv.Close()

	first := dial(t, srv.URL)
	readWelcome(t, first)
	firstID := joinClient(t, first, "unity", "Alice")

	second := dial(t, srv.URL)
	readWelcome(t, second)
	secondID := joinClient(t, second, "ue5", "Bob")

	// Alice moves; both clients must see the authoritative position.
	move, err := NewMessage("move", 2, MoveState{
		Pos: Vec3{X: 168, Y: 39.7, Z: -8}, Yaw: 90, Speed: 6, Grounded: true,
	})
	if err != nil {
		t.Fatalf("build move: %v", err)
	}
	if err := first.WriteJSON(move); err != nil {
		t.Fatalf("write move: %v", err)
	}

	for _, conn := range []messageReader{first, second} {
		snapshot := waitSnapshot(t, conn, 3*time.Second, func(s Snapshot) bool {
			if len(s.Players) != 2 {
				return false
			}
			for _, p := range s.Players {
				if p.ID == firstID && p.Pos.X == 168 && p.Anim == "walk" {
					return true
				}
			}
			return false
		})
		seen := make(map[string]SnapshotPlayer, len(snapshot.Players))
		for _, p := range snapshot.Players {
			seen[p.ID] = p
		}

		if len(seen) != 2 {
			t.Fatalf("players = %d, want 2 (%+v)", len(seen), snapshot.Players)
		}
		alice, ok := seen[firstID]
		if !ok {
			t.Fatalf("snapshot missing %s: %+v", firstID, snapshot.Players)
		}
		if alice.Pos.X != 168 || alice.Anim != "walk" {
			t.Fatalf("authoritative player = %+v, want x=168 anim=walk", alice)
		}
		if _, ok := seen[secondID]; !ok {
			t.Fatalf("snapshot missing %s: %+v", secondID, snapshot.Players)
		}
	}
}

func TestDisconnectRemovesPlayer(t *testing.T) {
	srv := httptest.NewServer(New(testLogger()).Handler())
	defer srv.Close()

	leaving := dial(t, srv.URL)
	readWelcome(t, leaving)
	leavingID := joinClient(t, leaving, "unity", "Leaving")

	observer := dial(t, srv.URL)
	readWelcome(t, observer)
	joinClient(t, observer, "ue5", "Observer")

	snapshot := waitSnapshot(t, observer, 3*time.Second, func(s Snapshot) bool {
		return len(s.Players) == 2
	})
	if len(snapshot.Players) != 2 {
		t.Fatalf("players before disconnect = %d, want 2", len(snapshot.Players))
	}

	if err := leaving.Close(); err != nil {
		t.Fatalf("close leaving client: %v", err)
	}

	waitSnapshot(t, observer, 3*time.Second, func(s Snapshot) bool {
		if len(s.Players) != 1 {
			return false
		}
		for _, p := range s.Players {
			if p.ID == leavingID {
				return false
			}
		}
		return true
	})
}

func TestMoveBeforeJoinIsRejected(t *testing.T) {
	srv := httptest.NewServer(New(testLogger()).Handler())
	defer srv.Close()

	conn := dial(t, srv.URL)
	readWelcome(t, conn)

	move, err := NewMessage("move", 3, MoveState{Pos: Vec3{X: 1, Y: 2, Z: 3}})
	if err != nil {
		t.Fatalf("build move: %v", err)
	}
	if err := conn.WriteJSON(move); err != nil {
		t.Fatalf("write move: %v", err)
	}

	_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	for {
		var reply Message
		if err := conn.ReadJSON(&reply); err != nil {
			t.Fatalf("read error reply: %v", err)
		}
		if reply.Type == "error" {
			if reply.Seq != 3 {
				t.Fatalf("error seq = %d, want 3", reply.Seq)
			}
			return
		}
	}
}

