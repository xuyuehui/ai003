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
}

// Manager owns the room set and assigns players to rooms.
//
// An empty room id means "match me": the manager picks the first room with a
// free slot (fewest players first) and creates a new one when all are full.
// Explicit room ids are created on demand and may fail with ErrRoomFull.
type Manager struct {
	mu       sync.Mutex
	rooms    map[string]*Room
	order    []string
	capacity int
	next     int
}

// NewManager builds a manager with the given per-room capacity (<=0 uses the default).
func NewManager(capacity int) *Manager {
	if capacity <= 0 {
		capacity = DefaultCapacity
	}
	return &Manager{rooms: make(map[string]*Room), capacity: capacity}
}

// Enter places a player into a room. It returns the room id and initial state.
func (m *Manager) Enter(roomID, playerID, name string) (string, State, error) {
	if playerID == "" {
		return "", State{}, errors.New("empty player id")
	}

	m.mu.Lock()
	defer m.mu.Unlock()

	if roomID == "" {
		roomID = m.pickRoomLocked()
	} else if _, ok := m.rooms[roomID]; !ok {
		m.createLocked(roomID)
	}

	r := m.rooms[roomID]
	if r.Count() >= m.capacity {
		return "", State{}, fmt.Errorf("%s: %w", roomID, ErrRoomFull)
	}
	state := r.Enter(playerID, name)
	return roomID, state, nil
}

// Leave removes the player from a room; empty rooms are kept for reuse.
func (m *Manager) Leave(roomID, playerID string) {
	m.mu.Lock()
	defer m.mu.Unlock()

	if r, ok := m.rooms[roomID]; ok {
		r.Leave(playerID)
	}
}

// SetInput stores a player's input for the next tick of that room.
func (m *Manager) SetInput(roomID, playerID string, in Input) bool {
	m.mu.Lock()
	r, ok := m.rooms[roomID]
	m.mu.Unlock()
	if !ok {
		return false
	}
	return r.SetInput(playerID, in)
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
		r := m.rooms[id]
		tick, states := r.Snapshot()
		list = append(list, Info{ID: id, Players: len(states), Capacity: m.capacity, Tick: tick})
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
