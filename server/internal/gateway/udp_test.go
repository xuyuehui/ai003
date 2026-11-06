package gateway

import (
	"encoding/json"
	"log/slog"
	"net"
	"net/http/httptest"
	"os"
	"testing"
	"time"

	"ai003/server/internal/rudp"
)

func TestRoomUDPHandshakeAndArenaSnapshots(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	udpAddr, err := server.StartUDP("127.0.0.1:0")
	if err != nil {
		t.Fatalf("StartUDP: %v", err)
	}
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	conn := dial(t, httpServer.URL)
	readWelcome(t, conn)
	enter, _ := NewMessage("room.enter", 1, map[string]string{"name": "udp", "sync": "arena"})
	if err := conn.WriteJSON(enter); err != nil {
		t.Fatalf("enter: %v", err)
	}
	var token, roomID string
	for {
		var msg Message
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("entered: %v", err)
		}
		if msg.Type != "room.entered" {
			continue
		}
		var body struct {
			RoomID string `json:"roomId"`
			UDP    struct {
				Addr  string `json:"addr"`
				Token string `json:"token"`
			} `json:"udp"`
		}
		if err := json.Unmarshal(msg.Body, &body); err != nil {
			t.Fatalf("decode entered: %v", err)
		}
		roomID, token = body.RoomID, body.UDP.Token
		if body.UDP.Addr != "" {
			udpAddr = body.UDP.Addr
		}
		break
	}
	if roomID == "" || token == "" {
		t.Fatalf("missing room/udp token: %q %q", roomID, token)
	}

	local, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("udp listen: %v", err)
	}
	defer local.Close()
	remote, _ := net.ResolveUDPAddr("udp", udpAddr)
	ep := rudp.NewEndpoint(local, remote, 0xA003, rudp.Config{RTO: 40 * time.Millisecond})
	hello, _ := json.Marshal(map[string]any{"token": token})
	if err := ep.SendHello(hello); err != nil {
		t.Fatalf("hello: %v", err)
	}

	welcomeSeen := false
	var lastSnap roomDelta
	deadline := time.Now().Add(4 * time.Second)
	buf := make([]byte, 2048)
	for time.Now().Before(deadline) {
		_ = local.SetReadDeadline(time.Now().Add(100 * time.Millisecond))
		n, _, err := local.ReadFrom(buf)
		if err == nil {
			for _, payload := range ep.Handle(append([]byte(nil), buf[:n]...)) {
				var msg Message
				if json.Unmarshal(payload, &msg) != nil {
					continue
				}
				switch msg.Type {
				case "room.udp.welcome":
					welcomeSeen = true
				case "room.arena.snap", "room.arena.delta":
					var d roomDelta
					if json.Unmarshal(msg.Body, &d) == nil {
						lastSnap = d
					}
				}
			}
		}
		ep.Tick()
		if welcomeSeen && len(lastSnap.Players) > 0 {
			break
		}
	}
	if !welcomeSeen {
		t.Fatal("no room.udp.welcome over UDP")
	}
	if len(lastSnap.Players) == 0 {
		t.Fatal("no arena snapshot over UDP")
	}
	// Send a usercmd over UDP and confirm the ack advances.
	cmd, _ := json.Marshal(map[string]any{"type": "room.arena.cmd", "seq": 2,
		"body": map[string]any{"seq": 1, "msec": 50, "forward": 1}})
	if err := ep.Send(cmd); err != nil {
		t.Fatalf("send cmd: %v", err)
	}
	acked := false
	deadline = time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		_ = local.SetReadDeadline(time.Now().Add(100 * time.Millisecond))
		n, _, err := local.ReadFrom(buf)
		if err == nil {
			for _, payload := range ep.Handle(append([]byte(nil), buf[:n]...)) {
				var msg Message
				if json.Unmarshal(payload, &msg) != nil {
					continue
				}
				if msg.Type != "room.arena.delta" && msg.Type != "room.arena.snap" {
					continue
				}
				var d roomDelta
				if json.Unmarshal(msg.Body, &d) == nil && d.Ack > 0 {
					acked = true
				}
			}
		}
		ep.Tick()
		if acked {
			break
		}
	}
	if !acked {
		t.Fatal("ack did not advance after a usercmd over UDP")
	}
}

type roomDelta struct {
	Seq     uint32 `json:"seq"`
	Base    uint32 `json:"base"`
	Ack     uint32 `json:"ack"`
	Players []struct {
		ID     uint8    `json:"id"`
		Mask   uint16   `json:"mask"`
		Origin [3]int32 `json:"origin"`
	} `json:"players"`
}
