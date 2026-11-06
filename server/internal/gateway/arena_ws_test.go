package gateway

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"ai003/server/internal/room"

	"github.com/gorilla/websocket"
)

func TestArenaRoomEnterCmdSnapshotAndAck(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	conn := dial(t, httpServer.URL)
	readWelcome(t, conn)

	enter, err := NewMessage("room.enter", 1, map[string]string{"name": "duelist", "sync": "arena"})
	if err != nil {
		t.Fatalf("build room.enter: %v", err)
	}
	if err := conn.WriteJSON(enter); err != nil {
		t.Fatalf("write room.enter: %v", err)
	}

	var roomID string
	for {
		var msg Message
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read room.entered: %v", err)
		}
		if msg.Type != "room.entered" {
			continue
		}
		var body struct {
			RoomID string `json:"roomId"`
			Sync   string `json:"sync"`
		}
		if err := json.Unmarshal(msg.Body, &body); err != nil {
			t.Fatalf("decode room.entered: %v", err)
		}
		if body.Sync != "arena" {
			t.Fatalf("sync = %q, want arena", body.Sync)
		}
		roomID = body.RoomID
		break
	}
	if roomID == "" {
		t.Fatal("missing room id")
	}

	cmd, err := NewMessage("room.arena.cmd", 2, room.Cmd{Seq: 1, Msec: 50, Forward: 1})
	if err != nil {
		t.Fatalf("build cmd: %v", err)
	}
	if err := conn.WriteJSON(cmd); err != nil {
		t.Fatalf("write cmd: %v", err)
	}

	first, ok := waitArenaSnapshot(t, conn, 3*time.Second)
	if !ok {
		t.Fatal("no arena snapshot received")
	}
	if first.Base != 0 {
		t.Fatalf("first snapshot should be a full delta, base = %d", first.Base)
	}
	if first.Ack != 1 {
		t.Fatalf("ack = %d, want 1", first.Ack)
	}
	if len(first.Players) == 0 {
		t.Fatal("snapshot has no players")
	}

	// Ack the snapshot and make sure the next update is a delta against it.
	ack, err := NewMessage("room.arena.ack", 3, map[string]uint32{"seq": first.Seq})
	if err != nil {
		t.Fatalf("build ack: %v", err)
	}
	if err := conn.WriteJSON(ack); err != nil {
		t.Fatalf("write ack: %v", err)
	}

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		next, ok := waitArenaSnapshot(t, conn, 3*time.Second)
		if !ok {
			t.Fatal("no arena delta received")
		}
		if next.Seq <= first.Seq {
			continue
		}
		if next.Base != first.Seq {
			t.Fatalf("delta base = %d, want %d", next.Base, first.Seq)
		}
		return
	}
	t.Fatal("timed out waiting for a delta against the acked snapshot")
}

// waitArenaSnapshot reads messages until a room.arena.snap/delta arrives.
func waitArenaSnapshot(t *testing.T, conn messageReader, timeout time.Duration) (room.Delta, bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if err := conn.SetReadDeadline(deadline); err != nil {
			t.Fatalf("deadline: %v", err)
		}
		var msg Message
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read arena snapshot: %v", err)
		}
		if msg.Type != "room.arena.snap" && msg.Type != "room.arena.delta" {
			continue
		}
		var delta room.Delta
		if err := json.Unmarshal(msg.Body, &delta); err != nil {
			t.Fatalf("decode arena delta: %v", err)
		}
		return delta, true
	}
	return room.Delta{}, false
}
func TestArenaRoomCmdMovesPlayer(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	conn := dial(t, httpServer.URL)
	readWelcome(t, conn)
	enter, _ := NewMessage("room.enter", 1, map[string]string{"name": "mover", "sync": "arena"})
	if err := conn.WriteJSON(enter); err != nil {
		t.Fatalf("enter: %v", err)
	}
	for {
		var msg Message
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read: %v", err)
		}
		if msg.Type == "room.entered" {
			break
		}
	}

	cmd, _ := NewMessage("room.arena.cmd", 2, room.Cmd{Seq: 1, Msec: 50, Forward: 1})
	if err := conn.WriteJSON(cmd); err != nil {
		t.Fatalf("cmd: %v", err)
	}

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		var msg Message
		if err := conn.SetReadDeadline(deadline); err != nil {
			t.Fatalf("deadline: %v", err)
		}
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read snap: %v", err)
		}
		if msg.Type != "room.arena.snap" && msg.Type != "room.arena.delta" {
			continue
		}
		var delta room.Delta
		if err := json.Unmarshal(msg.Body, &delta); err != nil {
			t.Fatalf("decode: %v", err)
		}
		for _, p := range delta.Players {
			if p.Mask&room.FieldOrigin != 0 && p.Origin[2] > 0 {
				return
			}
		}
	}
	t.Fatal("player never moved; arena cmd path is broken")
}
func TestArenaExplicitRoomTwoClients(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	ids := make([]string, 2)
	conns := make([]*websocket.Conn, 2)
	for i := 0; i < 2; i++ {
		conns[i] = dial(t, httpServer.URL)
		readWelcome(t, conns[i])
		enter, _ := NewMessage("room.enter", 1, map[string]string{"name": "p", "room": "duel", "sync": "arena"})
		if err := conns[i].WriteJSON(enter); err != nil {
			t.Fatalf("enter %d: %v", i, err)
		}
		for {
			var msg Message
			if err := conns[i].ReadJSON(&msg); err != nil {
				t.Fatalf("read %d: %v", i, err)
			}
			if msg.Type != "room.entered" {
				continue
			}
			var body struct {
				RoomID string `json:"roomId"`
			}
			_ = json.Unmarshal(msg.Body, &body)
			ids[i] = body.RoomID
			break
		}
	}
	if ids[0] != "duel" || ids[1] != "duel" {
		t.Fatalf("room ids = %v", ids)
	}
	resp, err := http.Get(httpServer.URL + "/demo")
	if err != nil {
		t.Fatalf("demo: %v", err)
	}
	defer resp.Body.Close()
	var demo struct {
		Rooms []struct {
			ID      string `json:"id"`
			Players int    `json:"players"`
			Sync    string `json:"sync"`
		} `json:"rooms"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&demo); err != nil {
		t.Fatalf("decode demo: %v", err)
	}
	if len(demo.Rooms) != 1 || demo.Rooms[0].Players != 2 || demo.Rooms[0].Sync != "arena" {
		t.Fatalf("demo rooms = %+v", demo.Rooms)
	}
}
