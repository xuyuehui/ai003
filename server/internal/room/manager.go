package room

import (
	"errors"
	"fmt"
	"sync"
)

// DefaultCapacity is the number of fighters per room.
const DefaultCapacity = 8

// ErrRoomFull is returned when an explicit room has no free slot.
var ErrRoomFull = errors.New("room full")

// Info is the public view of a room for matchmaking / lobby lists.
type Info struct {
	ID       string `json:"id"`
	Players  int    `json:"players"`
	Capacity int    `json:"capacity"`
	Tick     uint64 `json:"tick"`
	Sync     string `json:"sync"`
}

// Manager owns the room set and assigns players to rooms.
//
// An empty room id means "match me": the manager picks the first room with a
// free slot (fewest players first) and creates a new one when all are full.
// Explicit room ids are created on demand and may fail with ErrRoomFull.
type Manager struct {
	mu       sync.Mutex
	rooms    map[string]*Room
	arenas   map[string]*ArenaRoom
	order    []string
	capacity int
	next     int
}

// NewManager builds a manager with the given per-room capacity (<=0 uses the default).
func NewManager(capacity int) *Manager {
	if capacity <= 0 {
		capacity = DefaultCapacity
	}
	return &Manager{rooms: make(map[string]*Room), arenas: make(map[string]*ArenaRoom), capacity: capacity}
}

// Enter places a player into a room. It returns the room id and initial state.
func (m *Manager) Enter(roomID, playerID, name string) (string, State, error) {
	if playerID == "" {
		return "", State{}, errors.New("empty player id")
	}

	m.mu.Lock()
	defer m.mu.Unlock()

	if roomID == "" {
		if existing, ok := m.findPlayerLocked(playerID); ok {
			roomID = existing
		} else {
			roomID = m.pickRoomLocked()
		}
	} else if _, ok := m.rooms[roomID]; !ok {
		m.createLocked(roomID)
	}

	r := m.rooms[roomID]
	if !r.Has(playerID) && r.Count() >= m.capacity {
		return "", State{}, fmt.Errorf("%s: %w", roomID, ErrRoomFull)
	}
	state := r.Enter(playerID, name)
	return roomID, state, nil
}

// MarkOffline keeps the player in its room for DisconnectGraceTicks.
func (m *Manager) MarkOffline(roomID, playerID string) {
	m.mu.Lock()
	r, ok := m.rooms[roomID]
	a, arena := m.arenas[roomID]
	m.mu.Unlock()
	if arena {
		a.MarkOffline(playerID)
		return
	}
	if ok {
		r.MarkOffline(playerID)
	}
}

// findPlayerLocked returns the room holding the player (online or offline).
func (m *Manager) findPlayerLocked(playerID string) (string, bool) {
	for _, id := range m.order {
		r, ok := m.rooms[id]
		if !ok {
			continue // arena rooms live in m.arenas
		}
		if r.Has(playerID) {
			return id, true
		}
	}
	return "", false
}

// Leave removes the player from a room; empty rooms are kept for reuse.
func (m *Manager) Leave(roomID, playerID string) {
	m.mu.Lock()
	defer m.mu.Unlock()

	if a, ok := m.arenas[roomID]; ok {
		a.Leave(playerID)
		return
	}
	if r, ok := m.rooms[roomID]; ok {
		r.Leave(playerID)
	}
}

// SetInput stores a player's command for a specific tick (0 = next tick).
func (m *Manager) SetInput(roomID, playerID string, tick uint64, in Input) bool {
	m.mu.Lock()
	r, ok := m.rooms[roomID]
	m.mu.Unlock()
	if !ok {
		return false
	}
	return r.SetInput(playerID, tick, in)
}

// Snapshot returns the current tick and players of a room.
func (m *Manager) Snapshot(roomID string) (uint64, []State, bool) {
	m.mu.Lock()
	r, ok := m.rooms[roomID]
	m.mu.Unlock()
	if !ok {
		return 0, nil, false
	}
	tick, states := r.Snapshot()
	return tick, states, true
}

// Step advances one room. The second result is false for an unknown room.
func (m *Manager) Step(roomID string) (Frame, bool) {
	m.mu.Lock()
	r, ok := m.rooms[roomID]
	m.mu.Unlock()
	if !ok {
		return Frame{}, false
	}
	return r.Step(), true
}

// FindOnline returns the room holding an online player with that id.
func (m *Manager) FindOnline(playerID string) (string, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()

	roomID, ok := m.findPlayerLocked(playerID)
	if ok {
		room := m.rooms[roomID]
		if room == nil || !room.HasOnline(playerID) {
			ok = false
		}
	}
	if !ok {
		if arenaID, arenaOK := m.findArenaPlayerLocked(playerID); arenaOK {
			if a, exists := m.arenas[arenaID]; exists && a.HasOnline(playerID) {
				return arenaID, true
			}
		}
		return "", false
	}
	return roomID, true
}

// CleanupEmpty removes rooms that have had no players for at least graceTicks.
func (m *Manager) CleanupEmpty(graceTicks int) int {
	if graceTicks <= 0 {
		return 0
	}

	m.mu.Lock()
	defer m.mu.Unlock()

	removed := 0
	for _, id := range append([]string(nil), m.order...) {
		if a, ok := m.arenas[id]; ok {
			if a.EmptyFor() >= graceTicks {
				delete(m.arenas, id)
				delete(m.rooms, id)
				removed++
			}
			continue
		}
		room := m.rooms[id]
		if room == nil || room.EmptyFor() < graceTicks {
			continue
		}
		delete(m.rooms, id)
		removed++
	}
	if removed == 0 {
		return 0
	}

	kept := m.order[:0]
	for _, id := range m.order {
		if _, ok := m.rooms[id]; ok {
			kept = append(kept, id)
		}
	}
	m.order = kept
	return removed
}

// RoomIDs returns a stable copy of the room ids.
func (m *Manager) RoomIDs() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	ids := make([]string, len(m.order))
	copy(ids, m.order)
	return ids
}

// List returns room info sorted by creation order.
func (m *Manager) List() []Info {
	m.mu.Lock()
	defer m.mu.Unlock()

	list := make([]Info, 0, len(m.order))
	for _, id := range m.order {
		if a, ok := m.arenas[id]; ok {
			list = append(list, Info{ID: id, Players: a.Count(), Capacity: m.capacity, Tick: a.Tick(), Sync: string(SyncArena)})
			continue
		}
		r := m.rooms[id]
		tick, states := r.Snapshot()
		list = append(list, Info{ID: id, Players: len(states), Capacity: m.capacity, Tick: tick, Sync: string(SyncFrame)})
	}
	return list
}

// Count returns how many rooms exist.
func (m *Manager) Count() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.rooms)
}

// pickRoomLocked returns the room with the most free slots, creating one if needed.
func (m *Manager) pickRoomLocked() string {
	best := ""
	bestPlayers := m.capacity
	for _, id := range m.order {
		players := m.rooms[id].Count()
		if players < bestPlayers {
			best = id
			bestPlayers = players
		}
	}
	if best != "" {
		return best
	}

	m.next++
	id := fmt.Sprintf("arena-%d", m.next)
	m.createLocked(id)
	return id
}

func (m *Manager) createLocked(id string) {
	m.rooms[id] = New(m.capacity)
	m.order = append(m.order, id)
}
