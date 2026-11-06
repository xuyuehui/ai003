// Command arenaclient exercises the Quake III style arena room headlessly.
//
// It joins with sync=arena, sends 20 Hz usercmds, acknowledges snapshots and
// prints a JSON summary (snapshot count, delta count, acked seq, final origin),
// so the arena path can be demonstrated without Unity or Unreal.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/gorilla/websocket"
)

type envelope struct {
	Type string          `json:"type"`
	Seq  int64           `json:"seq,omitempty"`
	Body json.RawMessage `json:"body,omitempty"`
}

type deltaPlayer struct {
	ID     uint8    `json:"id"`
	Mask   uint16   `json:"mask"`
	Origin [3]int32 `json:"origin"`
}

type delta struct {
	Seq     uint32        `json:"seq"`
	Base    uint32        `json:"base"`
	Ack     uint32        `json:"ack"`
	Players []deltaPlayer `json:"players"`
}

func main() {
	url := flag.String("url", "ws://127.0.0.1:8080/ws", "gateway websocket url")
	seconds := flag.Float64("seconds", 4, "how long to drive the room")
	room := flag.String("room", "", "explicit room id (empty = match)")
	flag.Parse()

	conn, _, err := websocket.DefaultDialer.Dial(*url, nil)
	if err != nil {
		fail("dial: %v", err)
	}
	defer conn.Close()

	seq := int64(0)
	send := func(typ string, body any) error {
		raw, err := json.Marshal(body)
		if err != nil {
			return err
		}
		seq++
		return conn.WriteJSON(envelope{Type: typ, Seq: seq, Body: raw})
	}

	// Drain the welcome envelope (optional, but matches the documented handshake).
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	_, _, _ = conn.ReadMessage()

	if err := send("room.enter", map[string]string{"name": "ArenaBot", "sync": "arena", "room": *room}); err != nil {
		fail("enter: %v", err)
	}

	roomID := ""
	ack := uint32(0)
	snaps, deltas := 0, 0
	var origin [3]int32
	var lastSeq uint32

	stop := time.Now().Add(time.Duration(*seconds * float64(time.Second)))
	cmdSeq := uint32(0)
	nextCmd := time.Now()
	for time.Now().Before(stop) {
		if time.Now().After(nextCmd) {
			cmdSeq++
			_ = send("room.arena.cmd", map[string]any{
				"seq": cmdSeq, "msec": 50, "yaw": 0, "pitch": 0,
				"forward": 1, "side": 0, "up": 0, "buttons": 0,
			})
			nextCmd = nextCmd.Add(50 * time.Millisecond)
		}

		_ = conn.SetReadDeadline(time.Now().Add(200 * time.Millisecond))
		_, raw, err := conn.ReadMessage()
		if err != nil {
			if ne, ok := err.(interface{ Timeout() bool }); ok && ne.Timeout() {
				continue
			}
			break
		}
		var msg envelope
		if json.Unmarshal(raw, &msg) != nil {
			continue
		}
		if msg.Type != "room.arena.snap" && msg.Type != "room.arena.delta" && msg.Type != "room.entered" {
			fmt.Fprintf(os.Stderr, "msg type=%s body=%s\n", msg.Type, string(msg.Body))
		}
		switch msg.Type {
		case "room.entered":
			var body struct {
				RoomID string `json:"roomId"`
				Sync   string `json:"sync"`
			}
			_ = json.Unmarshal(msg.Body, &body)
			roomID = body.RoomID
			if body.Sync != "arena" {
				fail("entered non-arena room %q (sync=%q)", body.RoomID, body.Sync)
			}
		case "room.arena.snap":
			snaps++
			var d delta
			if json.Unmarshal(msg.Body, &d) == nil {
				lastSeq = d.Seq
				for _, p := range d.Players {
					if p.Mask&1 != 0 {
						origin = p.Origin
					}
				}
				ack = d.Seq
				_ = send("room.arena.ack", map[string]any{"seq": d.Seq})
			}
		case "room.arena.delta":
			deltas++
			var d delta
			if json.Unmarshal(msg.Body, &d) == nil {
				lastSeq = d.Seq
				for _, p := range d.Players {
					if p.Mask&1 != 0 {
						origin = p.Origin
					}
				}
				ack = d.Seq
				_ = send("room.arena.ack", map[string]any{"seq": d.Seq})
			}
		}
		if snaps > 0 && deltas > 0 && origin[2] != 0 {
			break
		}
	}

	summary := map[string]any{
		"roomId":    roomID,
		"snapshots": snaps,
		"deltas":    deltas,
		"cmds":      cmdSeq,
		"ack":       ack,
		"lastSeq":   lastSeq,
		"origin":    origin,
		"moved":     origin[2] != 0,
	}
	out, _ := json.MarshalIndent(summary, "", "  ")
	fmt.Println(string(out))
	if roomID == "" || snaps == 0 || deltas == 0 || origin[2] == 0 {
		os.Exit(1)
	}
}

func fail(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	os.Exit(1)
}
