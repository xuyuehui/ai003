// Command democlient runs a headless protocol-v3 room scenario.
//
// It joins the arena, sends 20 Hz inputs (moving in a square and swinging on a
// fixed cadence) and prints a JSON summary of what it observed. Pair it with
// GET /demo on the server for a reproducible technical demonstration that does
// not need Unity or Unreal running.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"
)

type envelope struct {
	Type string          `json:"type"`
	Seq  int64           `json:"seq,omitempty"`
	Body json.RawMessage `json:"body,omitempty"`
}

type roomEntered struct {
	RoomID   string `json:"roomId"`
	PlayerID string `json:"playerId"`
	Tick     uint64 `json:"tick"`
	TickRate int    `json:"tickRate"`
	MaxHP    int    `json:"maxHP"`
}

type framePlayer struct {
	ID      string `json:"id"`
	HP      int    `json:"hp"`
	Offline bool   `json:"offline"`
	X       int64  `json:"x"`
	Z       int64  `json:"z"`
}

type frameEvent struct {
	Type   string `json:"type"`
	Source string `json:"source"`
	Target string `json:"target"`
}

type frameBody struct {
	Tick    uint64        `json:"tick"`
	Players []framePlayer `json:"players"`
	Events  []frameEvent  `json:"events"`
}

type summary struct {
	RoomID       string         `json:"roomId"`
	PlayerID     string         `json:"playerId"`
	Frames       int            `json:"frames"`
	FirstTick    uint64         `json:"firstTick"`
	LastTick     uint64         `json:"lastTick"`
	MaxTickGap   uint64         `json:"maxTickGap"`
	FinalHP      int            `json:"finalHP"`
	PlayersSeen  int            `json:"playersSeen"`
	Events       map[string]int `json:"events"`
	DurationSecs float64        `json:"durationSecs"`
}

// sharedPositions lets the input goroutine aim at the nearest player without
// assuming spawn slots (join order is racy when both clients start together).
type sharedPositions struct {
	mu  sync.Mutex
	pos map[string][2]int64
}

func newSharedPositions() *sharedPositions {
	return &sharedPositions{pos: make(map[string][2]int64)}
}

func (s *sharedPositions) set(id string, x, z int64) {
	s.mu.Lock()
	s.pos[id] = [2]int64{x, z}
	s.mu.Unlock()
}

// sparCommand walks toward the nearest other player and swings once in range.
func (s *sharedPositions) sparCommand(me string) (int, int, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()

	self, ok := s.pos[me]
	if !ok {
		return 1, 0, false
	}

	var best int64 = 1 << 62
	var tx, tz int64
	found := false
	for id, p := range s.pos {
		if id == me {
			continue
		}
		dx, dz := p[0]-self[0], p[1]-self[1]
		if d := dx*dx + dz*dz; d < best {
			best = d
			tx, tz = p[0], p[1]
			found = true
		}
	}
	if !found {
		return 0, 0, false
	}

	dx, dz := tx-self[0], tz-self[1]
	if dx*dx+dz*dz <= 2500*2500 {
		return 0, 0, true
	}
	return sign(dx), sign(dz), false
}

func sign(v int64) int {
	switch {
	case v > 0:
		return 1
	case v < 0:
		return -1
	default:
		return 0
	}
}

func main() {
	url := flag.String("url", envOr("AI003_WS_URL", "ws://127.0.0.1:8080/ws"),
		"gateway websocket url (priority: -url flag > AI003_WS_URL > ws://127.0.0.1:8080/ws)")
	name := flag.String("name", "demo", "player name")
	room := flag.String("room", "", "explicit room id (empty = auto match)")
	clientID := flag.String("clientId", "", "persistent client id (empty = server default)")
	seconds := flag.Int("seconds", 8, "scenario duration")
	still := flag.Bool("still", false, "stand still and only swing")
	spar := flag.Bool("spar", false, "walk two metres forward, then keep swinging (hits the still partner)")
	flag.Parse()

	if err := run(*url, *name, *room, *clientID, *seconds, *still, *spar); err != nil {
		fmt.Fprintf(os.Stderr, "democlient: %v\n", err)
		os.Exit(1)
	}
}

func run(url, name, roomID, clientID string, seconds int, still, spar bool) error {
	conn, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		return fmt.Errorf("dial %s: %w", url, err)
	}
	defer conn.Close()

	var seq int64
	send := func(typ string, body any) error {
		seq++
		msg := envelope{Type: typ, Seq: seq}
		if body != nil {
			raw, err := json.Marshal(body)
			if err != nil {
				return err
			}
			msg.Body = raw
		}
		return conn.WriteJSON(msg)
	}

	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	var welcome envelope
	if err := conn.ReadJSON(&welcome); err != nil {
		return fmt.Errorf("read welcome: %w", err)
	}

	enter := map[string]any{"name": name}
	if roomID != "" {
		enter["room"] = roomID
	}
	if clientID != "" {
		enter["clientId"] = clientID
	}
	if err := send("room.enter", enter); err != nil {
		return fmt.Errorf("send room.enter: %w", err)
	}

	entered := roomEntered{TickRate: 20, MaxHP: 100}
	for {
		_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
		var msg envelope
		if err := conn.ReadJSON(&msg); err != nil {
			return fmt.Errorf("read room.entered: %w", err)
		}
		if msg.Type == "error" {
			return fmt.Errorf("gateway error: %s", msg.Body)
		}
		if msg.Type != "room.entered" {
			continue
		}
		if err := json.Unmarshal(msg.Body, &entered); err != nil {
			return fmt.Errorf("decode room.entered: %w", err)
		}
		break
	}

	var currentTick atomic.Uint64
	currentTick.Store(entered.Tick)
	shared := newSharedPositions()

	deadline := time.Now().Add(time.Duration(seconds) * time.Second)
	ticker := time.NewTicker(time.Second / time.Duration(max(1, entered.TickRate)))
	defer ticker.Stop()

	var sendErr error
	var sendErrOnce sync.Once
	go func() {
		step := 0
		for now := range ticker.C {
			if now.After(deadline) {
				return
			}
			step++
			tick := currentTick.Load() + 2
			dx, dz := direction(step, entered.TickRate)
			buttons := 0
			if step%30 < 6 {
				buttons = 1
			}
			if still {
				dx, dz = 0, 0
				buttons = 1
			}
			if spar {
				var attack bool
				dx, dz, attack = shared.sparCommand(entered.PlayerID)
				if attack {
					buttons = 1
				} else {
					buttons = 0
				}
			}
			if err := send("room.input", map[string]any{
				"tick": tick, "dx": dx, "dz": dz, "buttons": buttons,
			}); err != nil {
				sendErrOnce.Do(func() { sendErr = err })
				return
			}
		}
	}()

	result := summary{
		RoomID:      entered.RoomID,
		PlayerID:    entered.PlayerID,
		FirstTick:   entered.Tick,
		LastTick:    entered.Tick,
		FinalHP:     entered.MaxHP,
		Events:      map[string]int{},
		PlayersSeen: 1,
	}

	start := time.Now()
	for time.Now().Before(deadline) {
		_ = conn.SetReadDeadline(deadline)
		var msg envelope
		if err := conn.ReadJSON(&msg); err != nil {
			break
		}
		if msg.Type != "room.frame" {
			continue
		}

		var frame frameBody
		if err := json.Unmarshal(msg.Body, &frame); err != nil {
			continue
		}

		result.Frames++
		if frame.Tick > currentTick.Load() {
			currentTick.Store(frame.Tick)
		}
		if frame.Tick > result.LastTick {
			if gap := frame.Tick - result.LastTick; gap > result.MaxTickGap {
				result.MaxTickGap = gap
			}
			result.LastTick = frame.Tick
		}
		if len(frame.Players) > result.PlayersSeen {
			result.PlayersSeen = len(frame.Players)
		}
		for _, player := range frame.Players {
			shared.set(player.ID, player.X, player.Z)
			if player.ID == entered.PlayerID {
				result.FinalHP = player.HP
			}
		}
		for _, event := range frame.Events {
			result.Events[event.Type]++
		}
	}

	result.DurationSecs = time.Since(start).Seconds()
	if sendErr != nil {
		return fmt.Errorf("send room.input: %w", sendErr)
	}

	encoded, err := json.MarshalIndent(result, "", "  ")
	if err != nil {
		return err
	}
	fmt.Println(string(encoded))
	return nil
}

// direction walks a square: two seconds per side at 20 Hz.
func direction(step, tickRate int) (int, int) {
	side := max(1, tickRate*2)
	switch (step / side) % 4 {
	case 0:
		return 1, 0
	case 1:
		return 0, 1
	case 2:
		return -1, 0
	default:
		return 0, -1
	}
}

func max(a, b int) int {
	if a > b {
		return a
	}
	return b
}

// envOr mirrors the helper in cmd/server and cmd/pingclient so all three
// binaries agree on how a gateway address is overridden via the environment.
func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
