package gateway

import (
	"log/slog"
	"math"
	"sort"
	"sync"
	"time"
)

const (
	// worldHalfExtentMeters is half of the 2048 m world: positions are clamped
	// into it so a buggy or hostile client cannot claim a spot outside the map.
	worldHalfExtentMeters = 1024.0

	// Vertical clamp: the world spans roughly -76 m .. 396 m, with margin for
	// swimming and jumping.
	minHeightMeters = -200.0
	maxHeightMeters = 600.0

	// SnapshotRate is the server broadcast rate for player state.
	SnapshotRate = 100 * time.Millisecond
)

// Vec3 is a world position in metres, matching world/world.json.
type Vec3 struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

// MoveState is what a client reports for its own avatar, and what the server
// stores as the authoritative state for that player.
type MoveState struct {
	Pos      Vec3    `json:"pos"`
	Yaw      float64 `json:"yaw"`
	Speed    float64 `json:"speed"`
	Grounded bool    `json:"grounded"`
	Swimming bool    `json:"swimming"`
}

// Player is one connected avatar.
type Player struct {
	ID       string
	Name     string
	Client   string
	State    MoveState
	JoinedAt time.Time
	LastSeen time.Time
}

// SnapshotPlayer is the per-player payload broadcast to everyone.
type SnapshotPlayer struct {
	ID       string  `json:"id"`
	Name     string  `json:"name"`
	Client   string  `json:"client"`
	Pos      Vec3    `json:"pos"`
	Yaw      float64 `json:"yaw"`
	Speed    float64 `json:"speed"`
	Grounded bool    `json:"grounded"`
	Swimming bool    `json:"swimming"`
	// Anim is derived by the server so every client shows the same state.
	Anim string `json:"anim"`
}

// Snapshot is broadcast at SnapshotRate to every joined client.
type Snapshot struct {
	Tick       uint64           `json:"tick"`
	ServerTime int64            `json:"serverTime"`
	Players    []SnapshotPlayer `json:"players"`
	// World carries the server-authoritative environment events (tide, elder,
	// salt nodes) in the same packet, so one snapshot is enough for a demo.
	World *WorldState `json:"world,omitempty"`
}

// Hub owns the authoritative player table.
type Hub struct {
	log      *slog.Logger
	interval time.Duration
	metrics  *metricsState

	mu      sync.RWMutex
	players map[string]*Player
	tick    uint64
	subs    map[string]func(Message)

	stop chan struct{}
	once sync.Once

	sim *WorldSim
}

// NewHub builds a hub that broadcasts every interval.
func NewHub(logger *slog.Logger, interval time.Duration) *Hub {
	if interval <= 0 {
		interval = SnapshotRate
	}
	return &Hub{
		log:      logger,
		interval: interval,
		players:  make(map[string]*Player),
		subs:     make(map[string]func(Message)),
		stop:     make(chan struct{}),
		sim:      NewWorldSim(0, 0),
	}
}

// Start launches the broadcast loop.
func (h *Hub) Start() {
	go h.loop()
}

// Close stops the broadcast loop.
func (h *Hub) Close() {
	h.once.Do(func() { close(h.stop) })
}

func (h *Hub) loop() {
	ticker := time.NewTicker(h.interval)
	defer ticker.Stop()

	for {
		select {
		case <-h.stop:
			return
		case <-ticker.C:
			h.broadcast()
		}
	}
}

func (h *Hub) broadcast() {
	h.mu.Lock()
	h.tick++
	snapshot := h.snapshotLocked()
	subs := make([]func(Message), 0, len(h.subs))
	for _, send := range h.subs {
		subs = append(subs, send)
	}
	players := make([]*Player, 0, len(h.players))
	for _, p := range h.players {
		players = append(players, p)
	}
	h.mu.Unlock()

	if h.sim != nil {
		state := h.sim.Advance(time.Now(), func(x, z float64) string {
			bestID := ""
			bestDist := NodeCaptureRadius
			for _, p := range players {
				d := math.Hypot(p.State.Pos.X-x, p.State.Pos.Z-z)
				if d < bestDist {
					bestDist = d
					bestID = p.ID
				}
			}
			return bestID
		})
		snapshot.World = &state
	}

	if len(subs) == 0 {
		return
	}

	msg, err := NewMessage("snapshot", 0, snapshot)
	if err != nil {
		h.log.Error("encode snapshot", "error", err)
		return
	}
	if h.metrics != nil {
		h.metrics.noteWorldSnapshot()
	}
	for _, send := range subs {
		send(msg)
	}
}

// Count returns how many players are currently joined to the world.
func (h *Hub) Count() int {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return len(h.players)
}

// Join registers a player. An empty name falls back to the client kind.
func (h *Hub) Join(id, name, client string) *Player {
	if name == "" {
		name = client
	}
	now := time.Now()

	h.mu.Lock()
	defer h.mu.Unlock()

	player, ok := h.players[id]
	if !ok {
		player = &Player{ID: id, JoinedAt: now}
		h.players[id] = player
	}
	player.Name = name
	player.Client = client
	player.LastSeen = now
	return player
}

// Leave removes a player and its subscriber.
func (h *Hub) Leave(id string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	delete(h.players, id)
	delete(h.subs, id)
}

// Subscribe registers the callback that receives snapshots for a player.
func (h *Hub) Subscribe(id string, send func(Message)) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.subs[id] = send
}

// ApplyMove stores a validated, clamped state for the player.
func (h *Hub) ApplyMove(id string, state MoveState) (MoveState, bool) {
	state = sanitize(state)

	h.mu.Lock()
	defer h.mu.Unlock()

	player, ok := h.players[id]
	if !ok {
		return MoveState{}, false
	}
	player.State = state
	player.LastSeen = time.Now()
	return state, true
}

// Snapshot returns the current authoritative view.
func (h *Hub) Snapshot() Snapshot {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return h.snapshotLocked()
}

func (h *Hub) snapshotLocked() Snapshot {
	players := make([]SnapshotPlayer, 0, len(h.players))
	for _, p := range h.players {
		players = append(players, SnapshotPlayer{
			ID:       p.ID,
			Name:     p.Name,
			Client:   p.Client,
			Pos:      p.State.Pos,
			Yaw:      p.State.Yaw,
			Speed:    p.State.Speed,
			Grounded: p.State.Grounded,
			Swimming: p.State.Swimming,
			Anim:     deriveAnim(p.State),
		})
	}
	sort.Slice(players, func(i, j int) bool { return players[i].ID < players[j].ID })

	return Snapshot{
		Tick:       h.tick,
		ServerTime: time.Now().UnixMilli(),
		Players:    players,
	}
}

// deriveAnim maps authoritative movement state to the shared animation name.
func deriveAnim(state MoveState) string {
	switch {
	case state.Swimming:
		return "swim"
	case !state.Grounded:
		return "jump"
	case state.Speed > 6.5:
		return "run"
	case state.Speed > 0.2:
		return "walk"
	default:
		return "idle"
	}
}

// sanitize clamps positions into the world and normalises yaw/speed.
func sanitize(state MoveState) MoveState {
	// Non-finite input is treated as 0 rather than clamped to a boundary: a
	// corrupted packet should not teleport the avatar to the edge of the map.
	state.Pos = Vec3{
		X: finiteOrZero(state.Pos.X),
		Y: finiteOrZero(state.Pos.Y),
		Z: finiteOrZero(state.Pos.Z),
	}
	state.Pos.X = clamp(state.Pos.X, -worldHalfExtentMeters, worldHalfExtentMeters)
	state.Pos.Z = clamp(state.Pos.Z, -worldHalfExtentMeters, worldHalfExtentMeters)
	state.Pos.Y = clamp(state.Pos.Y, minHeightMeters, maxHeightMeters)
	state.Yaw = normalizeYaw(state.Yaw)
	state.Speed = clamp(state.Speed, 0, 100)
	return state
}

func finiteOrZero(v float64) float64 {
	if math.IsNaN(v) || math.IsInf(v, 0) {
		return 0
	}
	return v
}

func clamp(v, lo, hi float64) float64 {
	if math.IsNaN(v) || math.IsInf(v, 0) {
		return lo
	}
	return math.Min(math.Max(v, lo), hi)
}

func normalizeYaw(yaw float64) float64 {
	if math.IsNaN(yaw) || math.IsInf(yaw, 0) {
		return 0
	}
	yaw = math.Mod(yaw, 360)
	if yaw < 0 {
		yaw += 360
	}
	return yaw
}
