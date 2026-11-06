package room

// Quake III Arena style snapshot model.
//
// The arena room keeps the same gameplay as the frame-synced room but replaces
// the "broadcast inputs, everyone simulates" contract with usercmds, sequenced
// snapshots and delta compression:
//
//	client -> room.arena.cmd   {seq, msec, yaw, pitch, forward, side, up, buttons}
//	server -> room.arena.snap  {seq, serverTime, ack, players[...]}
//	server -> room.arena.delta {seq, base, mask, players[...]}   (changed fields only)
//
// Snapshots are authoritative states. Clients predict the local player by
// re-running unacked cmds and reconcile against the latest snapshot ack.

// Cmd is one client usercmd (Quake III `usercmd_t` shape, integer units).
type Cmd struct {
	Seq     uint32 `json:"seq"`
	Msec    uint16 `json:"msec"`
	Yaw     int16  `json:"yaw"`
	Pitch   int16  `json:"pitch"`
	Forward int8   `json:"forward"`
	Side    int8   `json:"side"`
	Up      int8   `json:"up"`
	Buttons uint8  `json:"buttons"`
}

// ArenaPlayer is one player's snapshot state (millimetres / mm-per-tick).
type ArenaPlayer struct {
	ID       uint8    `json:"id"`
	Origin   [3]int32 `json:"origin"`
	Velocity [3]int32 `json:"velocity"`
	Health   uint16   `json:"health"`
	Yaw      int16    `json:"yaw"`
	Flags    uint16   `json:"flags"`
}

// Snapshot is a full authoritative state at a server tick.
type Snapshot struct {
	Seq        uint32        `json:"seq"`
	ServerTime uint32        `json:"serverTime"`
	Ack        uint32        `json:"ack"`
	Players    []ArenaPlayer `json:"players"`
}

// Player field mask bits used by Delta.
const (
	FieldOrigin   uint16 = 1 << 0
	FieldVelocity uint16 = 1 << 1
	FieldHealth   uint16 = 1 << 2
	FieldYaw      uint16 = 1 << 3
	FieldFlags    uint16 = 1 << 4
)

// DeltaPlayer carries only the fields that changed compared to the base snapshot.
// Origin/Velocity/Health/Yaw/Flags are valid when the corresponding mask bit is set.
type DeltaPlayer struct {
	ID       uint8    `json:"id"`
	Mask     uint16   `json:"mask"`
	Origin   [3]int32 `json:"origin,omitempty"`
	Velocity [3]int32 `json:"velocity,omitempty"`
	Health   uint16   `json:"health,omitempty"`
	Yaw      int16    `json:"yaw,omitempty"`
	Flags    uint16   `json:"flags,omitempty"`
}

// Delta is an incremental snapshot against Base (the last snapshot the client acked).
type Delta struct {
	Seq        uint32        `json:"seq"`
	Base       uint32        `json:"base"`
	ServerTime uint32        `json:"serverTime"`
	Ack        uint32        `json:"ack"`
	Removed    []uint8       `json:"removed,omitempty"`
	Players    []DeltaPlayer `json:"players"`
}

// BuildDelta diffs cur against base. A nil base produces a delta carrying every field.
func BuildDelta(base *Snapshot, cur Snapshot) Delta {
	byID := map[uint8]ArenaPlayer{}
	if base != nil {
		for _, p := range base.Players {
			byID[p.ID] = p
		}
	}
	d := Delta{Seq: cur.Seq, ServerTime: cur.ServerTime, Ack: cur.Ack}
	if base != nil {
		d.Base = base.Seq
	}
	seen := map[uint8]bool{}
	for _, p := range cur.Players {
		seen[p.ID] = true
		old, had := byID[p.ID]
		if !had {
			d.Players = append(d.Players, DeltaPlayer{
				ID: p.ID, Mask: FieldOrigin | FieldVelocity | FieldHealth | FieldYaw | FieldFlags,
				Origin: p.Origin, Velocity: p.Velocity, Health: p.Health, Yaw: p.Yaw, Flags: p.Flags,
			})
			continue
		}
		dp := DeltaPlayer{ID: p.ID}
		if old.Origin != p.Origin {
			dp.Mask |= FieldOrigin
			dp.Origin = p.Origin
		}
		if old.Velocity != p.Velocity {
			dp.Mask |= FieldVelocity
			dp.Velocity = p.Velocity
		}
		if old.Health != p.Health {
			dp.Mask |= FieldHealth
			dp.Health = p.Health
		}
		if old.Yaw != p.Yaw {
			dp.Mask |= FieldYaw
			dp.Yaw = p.Yaw
		}
		if old.Flags != p.Flags {
			dp.Mask |= FieldFlags
			dp.Flags = p.Flags
		}
		if dp.Mask != 0 {
			d.Players = append(d.Players, dp)
		}
	}
	if base != nil {
		for _, p := range base.Players {
			if !seen[p.ID] {
				d.Removed = append(d.Removed, p.ID)
			}
		}
	}
	return d
}

// ApplyDelta reconstructs a full snapshot from base + delta.
func ApplyDelta(base *Snapshot, d Delta) Snapshot {
	out := Snapshot{Seq: d.Seq, ServerTime: d.ServerTime, Ack: d.Ack}
	byID := map[uint8]int{}
	if base != nil {
		for _, p := range base.Players {
			byID[p.ID] = len(out.Players)
			out.Players = append(out.Players, p)
		}
	}
	for _, dp := range d.Players {
		idx, ok := byID[dp.ID]
		if !ok {
			out.Players = append(out.Players, ArenaPlayer{ID: dp.ID})
			idx = len(out.Players) - 1
			byID[dp.ID] = idx
		}
		p := &out.Players[idx]
		if dp.Mask&FieldOrigin != 0 {
			p.Origin = dp.Origin
		}
		if dp.Mask&FieldVelocity != 0 {
			p.Velocity = dp.Velocity
		}
		if dp.Mask&FieldHealth != 0 {
			p.Health = dp.Health
		}
		if dp.Mask&FieldYaw != 0 {
			p.Yaw = dp.Yaw
		}
		if dp.Mask&FieldFlags != 0 {
			p.Flags = dp.Flags
		}
	}
	if len(d.Removed) > 0 {
		rm := map[uint8]bool{}
		for _, id := range d.Removed {
			rm[id] = true
		}
		kept := out.Players[:0]
		for _, p := range out.Players {
			if !rm[p.ID] {
				kept = append(kept, p)
			}
		}
		out.Players = kept
	}
	return out
}
