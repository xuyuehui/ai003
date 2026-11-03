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
