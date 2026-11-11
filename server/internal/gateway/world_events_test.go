package gateway

import (
	"testing"
	"time"
)

func TestTideSweepsAcrossTheBasin(t *testing.T) {
	sim := NewWorldSim(0, 0)
	start := time.Now()

	first := sim.Advance(start, nil)
	if !first.Tide.Active {
		t.Fatalf("tide should be active at t=0, got %+v", first.Tide)
	}
	startX := first.Tide.X

	now := start
	for i := 0; i < 300; i++ { // 60 s in 200 ms ticks
		now = now.Add(200 * time.Millisecond)
		sim.Advance(now, nil)
	}

	state := sim.SnapshotWorldState()
	if state.Tide.X <= startX {
		t.Fatalf("tide did not advance: %f -> %f", startX, state.Tide.X)
	}
	want := TideSpeed * 60
	if got := state.Tide.X - startX; got < want*0.95 || got > want*1.05 {
		t.Fatalf("tide travelled %.0f m in 60 s, want ~%.0f", got, want)
	}
	if state.Tide.ETA <= 0 {
		t.Fatalf("tide ETA should stay positive during the sweep")
	}
}

func TestNodeIsCapturedByStandingOnIt(t *testing.T) {
	sim := NewWorldSim(0, 0)
	now := time.Now()

	// Nobody near: progress must stay at zero.
	for i := 0; i < 20; i++ {
		now = now.Add(200 * time.Millisecond)
		sim.Advance(now, func(x, z float64) string { return "" })
	}
	if state := sim.SnapshotWorldState(); state.Nodes[0].Progress != 0 {
		t.Fatalf("node progressed with nobody standing on it: %+v", state.Nodes[0])
	}

	// Two seconds of presence captures it.
	for i := 0; i < 12; i++ {
		now = now.Add(200 * time.Millisecond)
		sim.Advance(now, func(x, z float64) string { return "player-1" })
	}
	state := sim.SnapshotWorldState()
	if state.Nodes[0].Owner != "player-1" {
		t.Fatalf("node not captured: %+v", state.Nodes[0])
	}
}

func TestElderCrossesPeriodically(t *testing.T) {
	sim := NewWorldSim(0, 0)
	now := time.Now()
	seen := false
	for i := 0; i < 400; i++ { // 80 s
		now = now.Add(200 * time.Millisecond)
		if sim.Advance(now, nil).Elder.Active {
			seen = true
			break
		}
	}
	if !seen {
		t.Fatalf("elder never became active in the first 80 s")
	}
}
