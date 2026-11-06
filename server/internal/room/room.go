// Package room implements a deterministic, frame-synced combat room.
//
// The server drives a fixed 20 Hz tick. Clients send inputs, the room applies
// them in tick order and broadcasts the frame (inputs + resulting state) to
// every member. All arithmetic is integer millimetres / millimetres-per-tick so
// Go, Unity (C#) and Unreal (C++) produce exactly the same result.
package room

import "sync"

const (
	// TickRate is the fixed simulation rate in ticks per second.
	TickRate = 20
	// TickMillis is the wall-clock duration of one tick.
	TickMillis = 1000 / TickRate

	// ArenaHalfMM is half the square arena edge, in millimetres (±20 m).
	ArenaHalfMM int64 = 20000

	// SpeedMMPerTick is the walk speed per tick (5 m/s at 20 Hz).
	SpeedMMPerTick int64 = 250
	// DiagonalMMPerTick is 250/sqrt(2), rounded, so diagonals are not faster.
	DiagonalMMPerTick int64 = 177

	// AttackRangeMM is the melee reach.
	AttackRangeMM int64 = 3000
	// AttackDamage is applied on a successful hit.
	AttackDamage = 25
	// AttackCooldownTicks is 1 s at 20 Hz.
	AttackCooldownTicks = 20
	// MaxHP is the starting and respawn health.
	MaxHP = 100
	// RespawnTicks is 3 s at 20 Hz.
	RespawnTicks = 60
	// DisconnectGraceTicks keeps an offline player's state for 30 s.
	DisconnectGraceTicks = 30 * TickRate
	// EmptyRoomGraceTicks is how long an empty room is kept before it is removed.
	EmptyRoomGraceTicks = 30 * TickRate
)

// Input is one player's command for a tick.
type Input struct {
	DX      int8  `json:"dx"`
	DZ      int8  `json:"dz"`
	Buttons uint8 `json:"buttons"`
}

// ButtonAttack is the melee attack bit.
const ButtonAttack uint8 = 1 << 0

// Player is the authoritative room state of one member.
type Player struct {
	ID       string
	Name     string
	Slot     int
	X, Z     int64 // millimetres, room-local
	FacingX  int8
	FacingZ  int8
	HP       int
	Cooldown int
	Respawn  int // ticks until respawn; 0 means alive
	Input    Input
	// LastInput is applied when no ticked input is queued (packet loss/repeat).
	LastInput Input
	// Pending holds inputs keyed by the tick they belong to (client prediction).
	Pending map[uint64]Input
	// Offline is set when the socket drops: the state is kept for
	// DisconnectGraceTicks so a reconnect can resume, then the player is removed.
	Offline      bool
	OfflineTicks int
}

// State is the per-player snapshot carried by a frame.
type State struct {
	ID      string `json:"id"`
	Name    string `json:"name"`
	X       int64  `json:"x"`
	Z       int64  `json:"z"`
	FacingX int8   `json:"facingX"`
	FacingZ int8   `json:"facingZ"`
	HP      int    `json:"hp"`
	Alive   bool   `json:"alive"`
	Slot    int    `json:"slot"`
	Offline bool   `json:"offline,omitempty"`
}

// Event describes something that happened during a tick.
type Event struct {
	Type   string `json:"type"` // hit | death | respawn
	Source string `json:"source,omitempty"`
	Target string `json:"target,omitempty"`
	Damage int    `json:"damage,omitempty"`
}

// Frame is broadcast every tick to room members.
type Frame struct {
	Tick    uint64           `json:"tick"`
	Inputs  map[string]Input `json:"inputs"`
	Players []State          `json:"players"`
	Events  []Event          `json:"events,omitempty"`
}

// Spawn points in millimetres, assigned by slot so joining order is irrelevant
// for a given slot.
var spawnPoints = [][2]int64{
	{0, 0},
	{4000, 0},
	{-4000, 0},
	{0, 4000},
	{0, -4000},
	{4000, 4000},
	{-4000, 4000},
	{4000, -4000},
	{-4000, -4000},
}

// PlayerRadiusMM is the collision radius used to keep fighters apart.
const PlayerRadiusMM int64 = 400

// Room holds the deterministic arena state.
type Room struct {
	mu         sync.Mutex
	players    map[string]*Player
	order      []string
	tick       uint64
	capacity   int
	emptyTicks int
}

// New creates an empty room with the given capacity (<=0 uses the default).
func New(capacity int) *Room {
	if capacity <= 0 {
		capacity = DefaultCapacity
	}
	return &Room{players: make(map[string]*Player), capacity: capacity}
}

// Onlines returns the ids of the currently online players.
func (r *Room) Onlines() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]string, 0, len(r.order))
	for _, id := range r.order {
		if p, ok := r.players[id]; ok && !p.Offline {
			out = append(out, id)
		}
	}
	return out
}

// HasOnline reports whether the player exists and is not offline.
func (r *Room) HasOnline(id string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	p, ok := r.players[id]
	return ok && !p.Offline
}

// Count returns the number of players currently in the room.
func (r *Room) Count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.players)
}

// Tick returns the current tick number.
func (r *Room) Tick() uint64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.tick
}

// Enter adds a player and returns its state. An offline player (reconnect) is
// revived with its previous HP/position/slot; an online re-enter resets it.
func (r *Room) Enter(id, name string) State {
	r.mu.Lock()
	defer r.mu.Unlock()

	if p, ok := r.players[id]; ok {
		if p.Offline {
			p.Offline = false
			p.OfflineTicks = 0
			p.Input = Input{}
			if name != "" {
				p.Name = name
			}
			return stateOf(p)
		}
		r.reset(p)
		return stateOf(p)
	}

	used := make(map[int]bool, len(r.players))
	for _, p := range r.players {
		used[p.Slot] = true
	}
	slot := 0
	for slot < len(spawnPoints) && used[slot] {
		slot++
	}
	if slot == len(spawnPoints) {
		slot = len(r.players) % len(spawnPoints)
	}
	if name == "" {
		name = "player"
	}

	p := &Player{ID: id, Name: name, Slot: slot}
	r.players[id] = p
	r.order = append(r.order, id)
	r.reset(p)
	return stateOf(p)
}

// Has reports whether the player is in the room (online or offline).
func (r *Room) Has(id string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	_, ok := r.players[id]
	return ok
}

// MarkOffline keeps the player's state for DisconnectGraceTicks.
func (r *Room) MarkOffline(id string) {
	r.mu.Lock()
	defer r.mu.Unlock()

	p, ok := r.players[id]
	if !ok || p.Offline {
		return
	}
	p.Offline = true
	p.OfflineTicks = DisconnectGraceTicks
	p.Input = Input{}
}

// Leave removes a player immediately (explicit room.leave).
func (r *Room) Leave(id string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.removeLocked(id)
}

func (r *Room) removeLocked(id string) {
	if _, ok := r.players[id]; !ok {
		return
	}
	delete(r.players, id)
	for i, existing := range r.order {
		if existing == id {
			r.order = append(r.order[:i], r.order[i+1:]...)
			break
		}
	}
}

// SetInput stores a command. A zero tick applies from the next tick (legacy
// clients); a non-zero tick is applied exactly at that tick, which is what the
// clients' prediction/replay relies on.
func (r *Room) SetInput(id string, tick uint64, in Input) bool {
	in.DX = clampDir(in.DX)
	in.DZ = clampDir(in.DZ)

	r.mu.Lock()
	defer r.mu.Unlock()

	p, ok := r.players[id]
	if !ok {
		return false
	}
	if tick == 0 || tick <= r.tick {
		p.LastInput = in
		p.Input = in
		return true
	}
	if p.Pending == nil {
		p.Pending = make(map[uint64]Input)
	}
	p.Pending[tick] = in
	return true
}

// Snapshot returns the current tick and player states without advancing.
func (r *Room) Snapshot() (uint64, []State) {
	r.mu.Lock()
	defer r.mu.Unlock()

	states := make([]State, 0, len(r.players))
	for _, id := range r.order {
		states = append(states, stateOf(r.players[id]))
	}
	return r.tick, states
}

// inputFor returns the input for the current tick: a queued ticked input when
// present, otherwise the last one (packet loss repeats the previous command).
func (r *Room) inputFor(p *Player) Input {
	if p.Pending != nil {
		if in, ok := p.Pending[r.tick]; ok {
			delete(p.Pending, r.tick)
			p.LastInput = in
		}
		for tick := range p.Pending {
			if tick < r.tick {
				delete(p.Pending, tick)
			}
		}
	}
	return p.LastInput
}

// Step advances one tick and returns the frame to broadcast.
func (r *Room) Step() Frame {
	r.mu.Lock()
	defer r.mu.Unlock()

	r.tick++
	frame := Frame{
		Tick:    r.tick,
		Inputs:  make(map[string]Input, len(r.players)),
		Players: make([]State, 0, len(r.players)),
	}

	for _, id := range r.order {
		p := r.players[id]
		if p.Offline {
			// Grace timers keep running while offline: a connected player who is
			// still dead will respawn, and attack cooldowns expire.
			if p.Respawn > 0 {
				p.Respawn--
				if p.Respawn == 0 {
					r.reset(p)
				}
			}
			if p.Cooldown > 0 {
				p.Cooldown--
			}
			p.OfflineTicks--
			if p.OfflineTicks <= 0 {
				r.removeLocked(id)
			}
			continue
		}
		p.Input = r.inputFor(p)
		frame.Inputs[id] = p.Input
		r.stepPlayer(p, &frame.Events)
	}
	r.separate()
	if len(r.players) == 0 {
		r.emptyTicks++
	} else {
		r.emptyTicks = 0
	}
	for _, id := range r.order {
		frame.Players = append(frame.Players, stateOf(r.players[id]))
	}
	return frame
}

// EmptyFor returns how many consecutive ticks the room has had no players.
func (r *Room) EmptyFor() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.emptyTicks
}

// separate pushes overlapping fighters apart along the dominant axis. It is
// integer-only and deterministic, and runs after every movement/attack so all
// clients compute the same positions.
func (r *Room) separate() {
	const stepMM int64 = 20
	const minDistMM = 2 * PlayerRadiusMM

	for i := 0; i < len(r.order); i++ {
		a := r.players[r.order[i]]
		if a.Respawn > 0 || a.Offline {
			continue
		}
		for j := i + 1; j < len(r.order); j++ {
			b := r.players[r.order[j]]
			if b.Respawn > 0 || b.Offline {
				continue
			}

			dx := b.X - a.X
			dz := b.Z - a.Z
			if dx*dx+dz*dz >= minDistMM*minDistMM {
				continue
			}

			if dx == 0 && dz == 0 {
				dx = 1
			}
			if abs64(dx) >= abs64(dz) {
				dir := sign64(dx)
				a.X = clamp(a.X-dir*stepMM, -ArenaHalfMM, ArenaHalfMM)
				b.X = clamp(b.X+dir*stepMM, -ArenaHalfMM, ArenaHalfMM)
			} else {
				dir := sign64(dz)
				a.Z = clamp(a.Z-dir*stepMM, -ArenaHalfMM, ArenaHalfMM)
				b.Z = clamp(b.Z+dir*stepMM, -ArenaHalfMM, ArenaHalfMM)
			}
		}
	}
}

func abs64(v int64) int64 {
	if v < 0 {
		return -v
	}
	return v
}

func sign64(v int64) int64 {
	if v < 0 {
		return -1
	}
	return 1
}

func (r *Room) stepPlayer(p *Player, events *[]Event) {
	if p.Respawn > 0 {
		p.Respawn--
		if p.Respawn == 0 {
			r.reset(p)
			*events = append(*events, Event{Type: "respawn", Source: p.ID})
		}
		return
	}

	if p.Cooldown > 0 {
		p.Cooldown--
	}

	if p.Input.DX != 0 || p.Input.DZ != 0 {
		dx := int64(p.Input.DX)
		dz := int64(p.Input.DZ)
		step := SpeedMMPerTick
		if dx != 0 && dz != 0 {
			step = DiagonalMMPerTick
		}
		p.X = clamp(p.X+dx*step, -ArenaHalfMM, ArenaHalfMM)
		p.Z = clamp(p.Z+dz*step, -ArenaHalfMM, ArenaHalfMM)
		p.FacingX = p.Input.DX
		p.FacingZ = p.Input.DZ
	}

	if p.Input.Buttons&ButtonAttack == 0 || p.Cooldown > 0 {
		return
	}

	// Broadcast the swing even when it misses, so clients can play the animation.
	*events = append(*events, Event{Type: "attack", Source: p.ID})
	if target := r.pickTarget(p); target != nil {
		target.HP -= AttackDamage
		*events = append(*events, Event{Type: "hit", Source: p.ID, Target: target.ID, Damage: AttackDamage})
		if target.HP <= 0 {
			target.HP = 0
			target.Respawn = RespawnTicks
			*events = append(*events, Event{Type: "death", Source: p.ID, Target: target.ID})
		}
	}
	p.Cooldown = AttackCooldownTicks
}

// pickTarget returns the nearest living enemy inside range and the facing cone.
func (r *Room) pickTarget(p *Player) *Player {
	var best *Player
	var bestDist int64
	for _, other := range r.players {
		if other.ID == p.ID || other.Respawn > 0 || other.Offline {
			continue
		}
		dx := other.X - p.X
		dz := other.Z - p.Z
		dist2 := dx*dx + dz*dz
		if dist2 > AttackRangeMM*AttackRangeMM {
			continue
		}
		if !inCone(p.FacingX, p.FacingZ, dx, dz) {
			continue
		}
		if best == nil || dist2 < bestDist {
			best = other
			bestDist = dist2
		}
	}
	return best
}

// inCone accepts targets within ±60° of the facing direction. The check avoids
// floating point: dot² * 4 >= dist² means cos(angle) >= 0.5.
func inCone(fx, fz int8, dx, dz int64) bool {
	if fx == 0 && fz == 0 {
		return true // never moved yet: attack anything in range
	}
	dot := int64(fx)*dx + int64(fz)*dz
	if dot <= 0 {
		return false
	}
	dist2 := dx*dx + dz*dz
	return 4*dot*dot >= dist2
}

func (r *Room) reset(p *Player) {
	spawn := spawnPoints[p.Slot%len(spawnPoints)]
	p.X, p.Z = spawn[0], spawn[1]
	p.FacingX, p.FacingZ = 0, 1
	p.HP = MaxHP
	p.Cooldown = 0
	p.Respawn = 0
	p.Input = Input{}
	p.LastInput = Input{}
	p.Pending = nil
}

func stateOf(p *Player) State {
	return State{
		ID:      p.ID,
		Name:    p.Name,
		X:       p.X,
		Z:       p.Z,
		FacingX: p.FacingX,
		FacingZ: p.FacingZ,
		HP:      p.HP,
		Alive:   p.Respawn == 0,
		Slot:    p.Slot,
		Offline: p.Offline,
	}
}

func clampDir(v int8) int8 {
	if v > 1 {
		return 1
	}
	if v < -1 {
		return -1
	}
	return v
}

func clamp(v, lo, hi int64) int64 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}
