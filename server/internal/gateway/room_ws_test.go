package gateway

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http/httptest"
	"testing"
	"time"

	"ai003/server/internal/room"
)

func TestRoomEnterAndInputFrames(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	first := dial(t, httpServer.URL)
	readWelcome(t, first)
	firstID := enterRoom(t, first, 10)

	second := dial(t, httpServer.URL)
	readWelcome(t, second)
	secondID := enterRoom(t, second, 20)

	// Both clients see both players in the same tick frame.
	frame := waitRoomFrame(t, first, 3*time.Second, func(f room.Frame) bool {
		return len(f.Players) == 2 && f.Tick > 0
	})
	seen := make(map[string]room.State, len(frame.Players))
	for _, p := range frame.Players {
		seen[p.ID] = p
	}
	if _, ok := seen[firstID]; !ok {
		t.Fatalf("frame missing first player %s: %+v", firstID, frame.Players)
	}
	if _, ok := seen[secondID]; !ok {
		t.Fatalf("frame missing second player %s: %+v", secondID, frame.Players)
	}

	// Move east; the authoritative frame must advance the same player.
	move, err := NewMessage("room.input", 11, room.Input{DX: 1})
	if err != nil {
		t.Fatalf("build room.input: %v", err)
	}
	if err := first.WriteJSON(move); err != nil {
		t.Fatalf("write room.input: %v", err)
	}

	frame = waitRoomFrame(t, second, 3*time.Second, func(f room.Frame) bool {
		for _, p := range f.Players {
			if p.ID == firstID && p.X > 0 {
				return true
			}
		}
		return false
	})
	for _, p := range frame.Players {
		if p.ID == firstID && p.X != room.SpeedMMPerTick {
			t.Fatalf("moved player x = %d, want %d", p.X, room.SpeedMMPerTick)
		}
	}
}

func TestRoomLeaveRemovesPlayer(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	leaving := dial(t, httpServer.URL)
	readWelcome(t, leaving)
	leavingID := enterRoom(t, leaving, 10)

	observer := dial(t, httpServer.URL)
	readWelcome(t, observer)
	enterRoom(t, observer, 20)

	waitRoomFrame(t, observer, 3*time.Second, func(f room.Frame) bool {
		return len(f.Players) == 2
	})

	leave, err := NewMessage("room.leave", 11, nil)
	if err != nil {
		t.Fatalf("build room.leave: %v", err)
	}
	if err := leaving.WriteJSON(leave); err != nil {
		t.Fatalf("write room.leave: %v", err)
	}

	waitRoomFrame(t, observer, 3*time.Second, func(f room.Frame) bool {
		if len(f.Players) != 1 {
			return false
		}
		return f.Players[0].ID != leavingID
	})
}

func TestRoomInputBeforeEnterIsRejected(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	conn := dial(t, httpServer.URL)
	readWelcome(t, conn)

	input, err := NewMessage("room.input", 7, room.Input{DX: 1})
	if err != nil {
		t.Fatalf("build room.input: %v", err)
	}
	if err := conn.WriteJSON(input); err != nil {
		t.Fatalf("write room.input: %v", err)
	}

	_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	for {
		var reply Message
		if err := conn.ReadJSON(&reply); err != nil {
			t.Fatalf("read reply: %v", err)
		}
		if reply.Type == "error" {
			if reply.Seq != 7 {
				t.Fatalf("error seq = %d, want 7", reply.Seq)
			}
			return
		}
	}
}

func TestRoomReconnectKeepsPlayer(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	first := dial(t, httpServer.URL)
	readWelcome(t, first)
	roomID, playerID := enterRoomAs(t, first, 10, "client-1")
	if playerID != "client-1" {
		t.Fatalf("playerId = %q, want client-1", playerID)
	}
	if err := first.Close(); err != nil {
		t.Fatalf("close first: %v", err)
	}

	// Give the server a moment to process the disconnect (marks offline).
	time.Sleep(150 * time.Millisecond)

	second := dial(t, httpServer.URL)
	readWelcome(t, second)
	reconnectedRoom, reconnectedID := enterRoomAs(t, second, 20, "client-1")
	if reconnectedRoom != roomID || reconnectedID != playerID {
		t.Fatalf("reconnect = (%q,%q), want (%q,%q)", reconnectedRoom, reconnectedID, roomID, playerID)
	}

	frame := waitRoomFrame(t, second, 3*time.Second, func(f room.Frame) bool {
		for _, p := range f.Players {
			if p.ID == playerID && !p.Offline {
				return true
			}
		}
		return false
	})
	if len(frame.Players) != 1 {
		t.Fatalf("players after reconnect = %+v", frame.Players)
	}
}

func TestRoomListReportsRooms(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	conn := dial(t, httpServer.URL)
	readWelcome(t, conn)
	enterRoom(t, conn, 10)

	list, err := NewMessage("room.list", 11, nil)
	if err != nil {
		t.Fatalf("build room.list: %v", err)
	}
	if err := conn.WriteJSON(list); err != nil {
		t.Fatalf("write room.list: %v", err)
	}

	_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	for {
		var reply Message
		if err := conn.ReadJSON(&reply); err != nil {
			t.Fatalf("read room.list: %v", err)
		}
		if reply.Type != "room.list" {
			continue
		}
		var body struct {
			Rooms []room.Info `json:"rooms"`
		}
		if err := json.Unmarshal(reply.Body, &body); err != nil {
			t.Fatalf("decode room.list: %v", err)
		}
		if len(body.Rooms) != 1 || body.Rooms[0].ID != "arena-1" || body.Rooms[0].Players != 1 {
			t.Fatalf("rooms = %+v", body.Rooms)
		}
		if body.Rooms[0].Capacity != room.DefaultCapacity {
			t.Fatalf("capacity = %d, want %d", body.Rooms[0].Capacity, room.DefaultCapacity)
		}
		return
	}
}

func enterRoom(t *testing.T, conn messageReader, seq int64) string {
	t.Helper()
	_, playerID := enterRoomAs(t, conn, seq, "")
	return playerID
}

// enterRoomAs joins with an explicit persistent client id ("" = session id) and
// returns the room id and player id.
func enterRoomAs(t *testing.T, conn messageReader, seq int64, clientID string) (string, string) {
	t.Helper()

	body := map[string]string{"name": "tester"}
	if clientID != "" {
		body["clientId"] = clientID
	}
	enter, err := NewMessage("room.enter", seq, body)
	if err != nil {
		t.Fatalf("build room.enter: %v", err)
	}
	if err := conn.WriteJSON(enter); err != nil {
		t.Fatalf("write room.enter: %v", err)
	}

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		var msg Message
		if err := conn.SetReadDeadline(deadline); err != nil {
			t.Fatalf("deadline: %v", err)
		}
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read room.entered: %v", err)
		}
		if msg.Type != "room.entered" {
			continue
		}
		var body struct {
			PlayerID string `json:"playerId"`
			RoomID   string `json:"roomId"`
		}
		if err := json.Unmarshal(msg.Body, &body); err != nil || body.PlayerID == "" {
			t.Fatalf("room.entered body = %s (%v)", msg.Body, err)
		}
		return body.RoomID, body.PlayerID
	}
	t.Fatal("no room.entered message")
	return "", ""
}

func waitRoomFrame(t *testing.T, conn messageReader, timeout time.Duration, pred func(room.Frame) bool) room.Frame {
	t.Helper()

	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		var msg Message
		if err := conn.SetReadDeadline(deadline); err != nil {
			t.Fatalf("deadline: %v", err)
		}
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read room.frame: %v", err)
		}
		if msg.Type != "room.frame" {
			continue
		}
		var frame room.Frame
		if err := json.Unmarshal(msg.Body, &frame); err != nil {
			t.Fatalf("decode room.frame: %v", err)
		}
		if pred(frame) {
			return frame
		}
	}
	t.Fatal("room frame condition not met")
	return room.Frame{}
}
