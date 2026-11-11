package gateway

import (
	"math"
	"time"
)

// World-event timings. They are deliberately generous: the demo needs the tide
// and the elder to show up inside a short session.
const (
	TidePeriodSeconds  = 240.0
	TideSpeed          = 12.0 // metres per second, +X sweep
	TideHalfWidth      = 1100.0
	ElderPeriodSeconds = 300.0
	ElderDuration      = 70.0
	ElderSpeed         = 26.0 // metres per second, -X sweep
	NodeCaptureRadius  = 8.0
	NodeCaptureSeconds = 2.0
)

// TideState is the sandstorm wall. Its position is authoritative: clients only
// interpolate towards it.
type TideState struct {
	Active bool    `json:"active"`
	X      float64 `json:"x"`
	Height float64 `json:"height"`
	Speed  float64 `json:"speed"`
	ETA    float64 `json:"eta"`
}

// ElderState is the singing beast crossing the basin. Clients draw a wake, not
// a creature: it is a path, not an AI.
type ElderState struct {
	Active   bool    `json:"active"`
	X        float64 `json:"x"`
	Z        float64 `json:"z"`
	Progress float64 `json:"progress"`
}

// NodeState is one vena-salt outcrop and who is holding it.
type NodeState struct {
	ID       int     `json:"id"`
	X        float64 `json:"x"`
	Z        float64 `json:"z"`
	Owner    string  `json:"owner"`
	Progress float64 `json:"progress"`
}

// WorldState rides along with every snapshot.
type WorldState struct {
	Elapsed float64    `json:"elapsed"`
	Tide    TideState  `json:"tide"`
	Elder   ElderState `json:"elder"`
	Nodes   []NodeState `json:"nodes"`
}

// WorldSim advances the world events. It is deterministic in elapsed time, so a
// reconnecting client lands on exactly the same tide position as everyone else.
type WorldSim struct {
	nodes   []NodeState
	elapsed float64
	last    time.Time
}

// NewWorldSim places the salt nodes in a ring around the spawn.
func NewWorldSim(centerX, centerZ float64) *WorldSim {
	sim := &WorldSim{}
	const count = 6
	const radius = 260.0
	for i := 0; i < count; i++ {
		a := float64(i) / count * 2 * math.Pi
		sim.nodes = append(sim.nodes, NodeState{
			ID: i,
			X:  centerX + math.Cos(a)*radius,
			Z:  centerZ + math.Sin(a)*radius,
		})
	}
	return sim
}

// Advance moves the simulation to now and returns the broadcast payload.
// claim reports the player id standing on a node ("" when nobody is).
func (w *WorldSim) Advance(now time.Time, claim func(x, z float64) string) WorldState {
	dt := 0.0
	if !w.last.IsZero() {
		dt = now.Sub(w.last).Seconds()
		if dt < 0 || dt > 0.5 {
			dt = 0
		}
	}
	w.last = now
	w.elapsed += dt

	for i := range w.nodes {
		claimer := ""
		if claim != nil {
			claimer = claim(w.nodes[i].X, w.nodes[i].Z)
		}
		switch {
		case claimer != "":
			w.nodes[i].Progress += dt / NodeCaptureSeconds
			if w.nodes[i].Progress >= 1 {
				w.nodes[i].Progress = 1
				w.nodes[i].Owner = claimer
			}
		case w.nodes[i].Progress > 0:
			w.nodes[i].Progress -= dt / (NodeCaptureSeconds * 2)
			if w.nodes[i].Progress < 0 {
				w.nodes[i].Progress = 0
			}
		}
	}

	return WorldState{
		Elapsed: w.elapsed,
		Tide:    tideAt(w.elapsed),
		Elder:   elderAt(w.elapsed),
		Nodes:   append([]NodeState(nil), w.nodes...),
	}
}

func tideAt(elapsed float64) TideState {
	phase := math.Mod(elapsed, TidePeriodSeconds)
	cross := (TideHalfWidth * 2) / TideSpeed
	if phase >= cross {
		return TideState{Active: false, X: TideHalfWidth, Speed: TideSpeed}
	}
	x := -TideHalfWidth + phase*TideSpeed
	return TideState{
		Active: true,
		X:      x,
		Height: 320 + 40*math.Sin(phase*0.05),
		Speed:  TideSpeed,
		ETA:    (TideHalfWidth - x) / TideSpeed,
	}
}

func elderAt(elapsed float64) ElderState {
	// Offset so a crossing is already under way when the demo starts.
	phase := math.Mod(elapsed+20, ElderPeriodSeconds)
	if phase >= ElderDuration {
		return ElderState{Active: false}
	}
	progress := phase / ElderDuration
	return ElderState{
		Active:   true,
		X:        900 - progress*(ElderSpeed*ElderDuration),
		Z:        120,
		Progress: progress,
	}
}

// SnapshotWorldState returns the current state without advancing (tests, /world).
func (w *WorldSim) SnapshotWorldState() WorldState {
	return WorldState{
		Elapsed: w.elapsed,
		Tide:    tideAt(w.elapsed),
		Elder:   elderAt(w.elapsed),
		Nodes:   append([]NodeState(nil), w.nodes...),
	}
}
