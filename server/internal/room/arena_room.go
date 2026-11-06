package room

import (
	"math"
	"sync"
)

// ArenaRoom runs the same gameplay as Room but with Quake III style netcode:
// clients submit sequenced usercmds, the server executes one per tick, and
// replies with sequenced snapshots that can be delta-compressed against the
// snapshot the client acknowledged.
type ArenaRoom struct {
	mu       sync.Mutex
	room     *Room
	cmds     map[string][]Cmd
	lastSeq  map[string]uint32
	ackSeq   map[string]uint32
	lastCmd  map[string]Cmd
	snapshot Snapshot
	history  map[uint32]Snapshot // full snapshots by seq (bounded)
	historyN int
}

const arenaHistorySize = 128

// NewArena creates an arena-sync room with the given capacity.
func NewArena(capacity int) *ArenaRoom {
	return &ArenaRoom{
		room:    New(capacity),
		cmds:    map[string][]Cmd{},
		lastSeq: map[string]uint32{},
		ackSeq:  map[string]uint32{},
		lastCmd: map[string]Cmd{},
		history: map[uint32]Snapshot{},
	}
}

// Enter adds or reconnects a player, mirroring Room.Enter semantics.
func (a *ArenaRoom) Enter(id, name string) State {
	a.mu.Lock()
	defer a.mu.Unlock()
	state := a.room.Enter(id, name)
	if _, ok := a.cmds[id]; !ok {
		a.cmds[id] = nil
	}
	return state
}

// MarkOffline / Leave / HasOnline / Count / Tick delegate to the gameplay room.
func (a *ArenaRoom) MarkOffline(id string) {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.room.MarkOffline(id)
}

func (a *ArenaRoom) Leave(id string) {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.room.Leave(id)
	delete(a.cmds, id)
	delete(a.lastSeq, id)
	delete(a.ackSeq, id)
	delete(a.lastCmd, id)
}

func (a *ArenaRoom) HasOnline(id string) bool { return a.room.HasOnline(id) }
func (a *ArenaRoom) Count() int               { return a.room.Count() }
func (a *ArenaRoom) Tick() uint64             { return a.room.Tick() }
func (a *ArenaRoom) EmptyFor() int            { return a.room.EmptyFor() }

// SubmitCmd queues a usercmd. Duplicate or stale seq numbers are dropped; a
// bounded look-ahead window tolerates packets arriving out of order.
func (a *ArenaRoom) SubmitCmd(id string, cmd Cmd) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	if !a.room.HasOnline(id) {
		return false
	}
	if cmd.Seq != 0 && cmd.Seq <= a.lastSeq[id] {
		return false
	}
	queue := a.cmds[id]
	if len(queue) > 0 && cmd.Seq != 0 && cmd.Seq <= queue[0].Seq {
		return false
	}
	if len(queue) > 0 && cmd.Seq != 0 && cmd.Seq <= queue[len(queue)-1].Seq {
		// Insert out-of-order command in seq order.
		for i, queued := range queue {
			if cmd.Seq == queued.Seq {
				return false
			}
			if cmd.Seq < queued.Seq {
				queue = append(queue[:i], append([]Cmd{cmd}, queue[i:]...)...)
				a.cmds[id] = queue
				return true
			}
		}
	}
	if len(queue) >= 64 {
		queue = queue[len(queue)-64:]
	}
	a.cmds[id] = append(queue, cmd)
	return true
}

// cmdToInput converts a yaw-relative usercmd into the room's world-axis input.
func cmdToInput(cmd Cmd) Input {
	yaw := float64(cmd.Yaw) * math.Pi / 32768.0
	fwd := float64(cmd.Forward)
	side := float64(cmd.Side)
	dx := side*math.Cos(yaw) + fwd*math.Sin(yaw)
	dz := -side*math.Sin(yaw) + fwd*math.Cos(yaw)
	return Input{DX: int8(math.Round(clampF(dx, -127, 127))), DZ: int8(math.Round(clampF(dz, -127, 127))), Buttons: cmd.Buttons}
}

func clampF(v, lo, hi float64) float64 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// Step executes one tick: each player's next usercmd (or the last one when the
// queue is empty, matching Quake's "repeat last cmd" behaviour) drives the
// shared gameplay, then a snapshot is produced and stored in the ack history.
func (a *ArenaRoom) Step() Snapshot {
	a.mu.Lock()
	defer a.mu.Unlock()

	tick := a.room.Tick() + 1
	for _, id := range a.room.Onlines() {
		if !a.room.HasOnline(id) {
			continue
		}
		var cmd Cmd
		if queue := a.cmds[id]; len(queue) > 0 {
			cmd = queue[0]
			a.cmds[id] = queue[1:]
			a.lastSeq[id] = cmd.Seq
			a.ackSeq[id] = cmd.Seq
			a.lastCmd[id] = cmd
		} else if last, ok := a.lastCmd[id]; ok {
			cmd = last
		}
		a.room.SetInput(id, tick, cmdToInput(cmd))
	}

	frame := a.room.Step()
	snap := a.snapshotFrom(frame)
	a.snapshot = snap
	a.history[snap.Seq] = snap
	a.historyN++
	if a.historyN > arenaHistorySize {
		for seq := range a.history {
			if seq+arenaHistorySize < snap.Seq {
				delete(a.history, seq)
			}
		}
		a.historyN = len(a.history)
	}
	return snap
}

// SnapshotFor returns the latest snapshot with the player's own cmd ack applied.
func (a *ArenaRoom) SnapshotFor(id string) Snapshot {
	a.mu.Lock()
	defer a.mu.Unlock()
	snap := a.snapshot
	snap.Ack = a.ackSeq[id]
	return snap
}

// DeltaFor returns a delta against the snapshot the client last acked. When the
// acked base is unknown (join, packet loss beyond the history window) a full
// snapshot delta is returned so the client can resync.
func (a *ArenaRoom) DeltaFor(id string, ackBase uint32) Delta {
	a.mu.Lock()
	defer a.mu.Unlock()
	snap := a.snapshot
	snap.Ack = a.ackSeq[id]
	base, ok := a.history[ackBase]
	if ackBase == 0 || !ok {
		return BuildDelta(nil, snap)
	}
	return BuildDelta(&base, snap)
}

func (a *ArenaRoom) snapshotFrom(frame Frame) Snapshot {
	out := Snapshot{Seq: uint32(frame.Tick), ServerTime: uint32(frame.Tick) * uint32(TickMillis)}
	for _, state := range frame.Players {
		p := ArenaPlayer{
			ID:     uint8(state.Slot + 1),
			Origin: [3]int32{int32(state.X), 0, int32(state.Z)},
			Health: uint16(maxInt(state.HP, 0)),
			Yaw:    yawOf(state.FacingX, state.FacingZ),
		}
		if state.Alive {
			p.Flags |= 1 << 0
		}
		if state.Offline {
			p.Flags |= 1 << 1
		}
		out.Players = append(out.Players, p)
	}
	return out
}

func yawOf(fx, fz int8) int16 {
	if fx == 0 && fz == 0 {
		return 0
	}
	// 16-bit angle where 65536 == 360 degrees.
	angle := math.Atan2(float64(fx), float64(fz))
	return int16(angle / (2 * math.Pi) * 65536)
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
