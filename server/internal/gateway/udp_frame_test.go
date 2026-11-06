package gateway

import (
	"encoding/json"
	"io"
	"log/slog"
	"net"
	"net/http/httptest"
	"testing"
	"time"

	"ai003/server/internal/rudp"
)

func TestFrameRoomUDPFramesContainBothPlayers(t *testing.T) {
	server := New(slog.New(slog.NewTextHandler(io.Discard, nil)))
	defer server.Close()
	udpAddr, err := server.StartUDP("127.0.0.1:0")
	if err != nil {
		t.Fatalf("StartUDP: %v", err)
	}
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	type client struct {
		ep    *rudp.Endpoint
		sock  net.PacketConn
		token string
	}
	clients := make([]client, 2)
	for i := 0; i < 2; i++ {
		conn := dial(t, httpServer.URL)
		readWelcome(t, conn)
		enter, _ := NewMessage("room.enter", 1, map[string]string{"name": "p", "room": "duel-frame"})
		if err := conn.WriteJSON(enter); err != nil {
			t.Fatalf("enter: %v", err)
		}
		for {
			var msg Message
			if err := conn.ReadJSON(&msg); err != nil {
				t.Fatalf("entered: %v", err)
			}
			if msg.Type != "room.entered" {
				continue
			}
			var body struct {
				UDP struct {
					Addr  string `json:"addr"`
					Token string `json:"token"`
				} `json:"udp"`
			}
			_ = json.Unmarshal(msg.Body, &body)
			clients[i].token = body.UDP.Token
			if body.UDP.Addr != "" {
				udpAddr = body.UDP.Addr
			}
			break
		}
	}
	for i := range clients {
		sock, err := net.ListenPacket("udp", "127.0.0.1:0")
		if err != nil {
			t.Fatalf("udp: %v", err)
		}
		clients[i].sock = sock
		remote, _ := net.ResolveUDPAddr("udp", udpAddr)
		ep := rudp.NewEndpoint(sock, remote, uint32(0x100+i), rudp.Config{RTO: 40 * time.Millisecond})
		hello, _ := json.Marshal(map[string]any{"token": clients[i].token})
		if err := ep.SendHello(hello); err != nil {
			t.Fatalf("hello: %v", err)
		}
		clients[i].ep = ep
	}

	deadline := time.Now().Add(3 * time.Second)
	buf := make([]byte, 2048)
	for time.Now().Before(deadline) {
		for i := range clients {
			_ = clients[i].sock.SetReadDeadline(time.Now().Add(50 * time.Millisecond))
			for {
				n, _, err := clients[i].sock.ReadFrom(buf)
				if err != nil {
					break
				}
				for _, payload := range clients[i].ep.Handle(append([]byte(nil), buf[:n]...)) {
					var msg Message
					if json.Unmarshal(payload, &msg) != nil || msg.Type != "room.frame" {
						continue
					}
					var frame struct {
						Players []struct {
							ID string `json:"id"`
						} `json:"players"`
					}
					if json.Unmarshal(msg.Body, &frame) != nil {
						continue
					}
					if len(frame.Players) != 2 {
						t.Fatalf("client %d frame players = %d, want 2", i, len(frame.Players))
					}
					return
				}
			}
			clients[i].ep.Tick()
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("no two-player frame observed over UDP")
}
