// Command pingclient is a headless smoke-test client for the AI003 gateway.
//
//	go run ./cmd/pingclient -url ws://127.0.0.1:8080/ws -n 3
//
// It exits 0 only if every ping was answered by a pong with a matching seq.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/gorilla/websocket"
)

type message struct {
	Type string          `json:"type"`
	Seq  int64           `json:"seq,omitempty"`
	Body json.RawMessage `json:"body,omitempty"`
}

func main() {
	url := flag.String("url", envOr("AI003_WS_URL", "ws://127.0.0.1:8080/ws"), "WebSocket URL (priority: -url flag > AI003_WS_URL > ws://127.0.0.1:8080/ws)")
	count := flag.Int("n", 3, "number of pings to send")
	timeout := flag.Duration("timeout", 5*time.Second, "timeout per ping")
	flag.Parse()

	if err := run(*url, *count, *timeout); err != nil {
		fmt.Fprintf(os.Stderr, "SMOKE FAILED: %v\n", err)
		os.Exit(1)
	}
	fmt.Println("SMOKE OK")
}

func run(url string, count int, timeout time.Duration) error {
	conn, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		return fmt.Errorf("dial %s: %w", url, err)
	}
	defer conn.Close()

	// The gateway greets every connection with a welcome frame.
	if err := conn.SetReadDeadline(time.Now().Add(timeout)); err != nil {
		return err
	}
	welcome := message{}
	if err := conn.ReadJSON(&welcome); err != nil {
		return fmt.Errorf("read welcome: %w", err)
	}
	if welcome.Type != "welcome" {
		return fmt.Errorf("expected welcome, got %q", welcome.Type)
	}
	fmt.Printf("welcome: session=%s body=%s\n", sessionID(welcome.Body), welcome.Body)

	for i := 1; i <= count; i++ {
		body, _ := json.Marshal(map[string]string{"client": "go-pingclient"})
		ping := message{Type: "ping", Seq: int64(i), Body: body}
		start := time.Now()
		if err := conn.WriteJSON(ping); err != nil {
			return fmt.Errorf("write ping %d: %w", i, err)
		}
		if err := conn.SetReadDeadline(time.Now().Add(timeout)); err != nil {
			return err
		}
		for {
			reply := message{}
			if err := conn.ReadJSON(&reply); err != nil {
				return fmt.Errorf("read pong %d: %w", i, err)
			}
			if reply.Type == "pong" && reply.Seq == int64(i) {
				fmt.Printf("pong seq=%d rtt=%s body=%s\n", reply.Seq, time.Since(start).Round(time.Microsecond), reply.Body)
				break
			}
			fmt.Printf("skip %q seq=%d\n", reply.Type, reply.Seq)
		}
	}
	_ = conn.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(websocket.CloseNormalClosure, ""), time.Now().Add(time.Second))
	return nil
}

func sessionID(body json.RawMessage) string {
	var v struct {
		SessionID string `json:"sessionId"`
	}
	_ = json.Unmarshal(body, &v)
	return v.SessionID
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
