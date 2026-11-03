package gateway

import (
	"encoding/json"
	"math"
	"testing"
	"time"
)

func TestSanitizeClampsAndNormalizes(t *testing.T) {
	in := MoveState{
		Pos:   Vec3{X: 99999, Y: -9999, Z: -99999},
		Yaw:   -90,
		Speed: 1e9,
	}
	out := sanitize(in)

	if out.Pos.X != worldHalfExtentMeters {
		t.Fatalf("X = %v, want %v", out.Pos.X, worldHalfExtentMeters)
	}
	if out.Pos.Z != -worldHalfExtentMeters {
		t.Fatalf("Z = %v, want %v", out.Pos.Z, -worldHalfExtentMeters)
	}
	if out.Pos.Y != minHeightMeters {
		t.Fatalf("Y = %v, want %v", out.Pos.Y, minHeightMeters)
	}
	if out.Yaw != 270 {
		t.Fatalf("Yaw = %v, want 270", out.Yaw)
	}
	if out.Speed != 100 {
		t.Fatalf("Speed = %v, want 100", out.Speed)
	}

	nan := sanitize(MoveState{Pos: Vec3{X: math.NaN(), Y: math.Inf(1), Z: math.NaN()}})
	if nan.Pos.X != 0 || nan.Pos.Z != 0 || nan.Pos.Y != 0 {
		t.Fatalf("NaN/Inf not normalised: %+v", nan.Pos)
	}
}

func TestDeriveAnim(t *testing.T) {
	cases := []struct {
		name  string
		state MoveState
		want  string
	}{
		{"idle", MoveState{Grounded: true}, "idle"},
		{"walk", MoveState{Grounded: true, Speed: 3}, "walk"},
		{"run", MoveState{Grounded: true, Speed: 8}, "run"},
		{"jump", MoveState{Grounded: false, Speed: 4}, "jump"},
		{"swim", MoveState{Grounded: false, Swimming: true}, "swim"},
	}
	for _, tc := range cases {
		if got := deriveAnim(tc.state); got != tc.want {
			t.Errorf("%s: deriveAnim = %q, want %q", tc.name, got, tc.want)
		}
	}
}

func TestHubJoinMoveSnapshot(t *testing.T) {
	hub := NewHub(testLogger(), time.Hour)
	defer hub.Close()

	player := hub.Join("p1", "Alice", "unity")
	if player.Name != "Alice" || player.Client != "unity" {
		t.Fatalf("join = %+v", player)
	}

	state := MoveState{Pos: Vec3{X: 10, Y: 40, Z: -8}, Yaw: 90, Speed: 0, Grounded: true}
	if _, ok := hub.ApplyMove("p1", state); !ok {
		t.Fatal("ApplyMove rejected a joined player")
	}

	snapshot := hub.Snapshot()
	if len(snapshot.Players) != 1 {
		t.Fatalf("players = %d, want 1", len(snapshot.Players))
	}
	got := snapshot.Players[0]
	if got.ID != "p1" || got.Pos.X != 10 || got.Anim != "idle" {
		t.Fatalf("snapshot player = %+v", got)
	}

	// Moving without joining is refused.
	if _, ok := hub.ApplyMove("ghost", state); ok {
		t.Fatal("ApplyMove accepted an unknown player")
	}

	hub.Leave("p1")
	if players := hub.Snapshot().Players; len(players) != 0 {
		t.Fatalf("players after leave = %+v, want empty", players)
	}
}

func joinClient(t *testing.T, conn messageReader, client, name string) string {
	t.Helper()
	join, err := NewMessage("join", 1, map[string]string{"client": client, "name": name})
	if err != nil {
		t.Fatalf("build join: %v", err)
	}
	if err := conn.WriteJSON(join); err != nil {
		t.Fatalf("write join: %v", err)
	}

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		var msg Message
		if err := conn.SetReadDeadline(deadline); err != nil {
			t.Fatalf("deadline: %v", err)
		}
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read joined: %v", err)
		}
		if msg.Type != "joined" {
			continue
		}
		var body struct {
			PlayerID string `json:"playerId"`
		}
		if err := json.Unmarshal(msg.Body, &body); err != nil || body.PlayerID == "" {
			t.Fatalf("joined body = %s (%v)", msg.Body, err)
		}
		return body.PlayerID
	}
	t.Fatal("no joined message")
	return ""
}

// waitSnapshot reads snapshots until pred is satisfied.
func waitSnapshot(t *testing.T, conn messageReader, timeout time.Duration, pred func(Snapshot) bool) Snapshot {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		snapshot := readSnapshot(t, conn, deadline)
		if pred(snapshot) {
			return snapshot
		}
	}
	t.Fatal("snapshot condition not met before deadline")
	return Snapshot{}
}

type messageReader interface {
	WriteJSON(any) error
	ReadJSON(any) error
	SetReadDeadline(time.Time) error
}

func readSnapshot(t *testing.T, conn messageReader, deadline time.Time) Snapshot {
	t.Helper()
	for time.Now().Before(deadline) {
		var msg Message
		if err := conn.SetReadDeadline(deadline); err != nil {
			t.Fatalf("set deadline: %v", err)
		}
		if err := conn.ReadJSON(&msg); err != nil {
			t.Fatalf("read snapshot: %v", err)
		}
		if msg.Type != "snapshot" {
			continue
		}
		var snapshot Snapshot
		if err := json.Unmarshal(msg.Body, &snapshot); err != nil {
			t.Fatalf("decode snapshot: %v", err)
		}
		return snapshot
	}
	t.Fatal("no snapshot within deadline")
	return Snapshot{}
}
