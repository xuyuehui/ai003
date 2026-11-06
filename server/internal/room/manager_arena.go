package room

import (
	"errors"
	"fmt"
)

// SyncMode selects the room network synchronisation algorithm.
type SyncMode string

const (
	// SyncFrame is the deterministic input-broadcast room.
	SyncFrame SyncMode = "frame"
	// SyncArena is the Quake III style usercmd/snapshot room.
	SyncArena SyncMode = "arena"
)

func (m *Manager) arenaLockedInit() {
	if m.arenas == nil {
		m.arenas = map[string]*ArenaRoom{}
	}
}

// EnterSync is Enter with an explicit sync mode. Arena rooms live in a
// separate pool so frame and arena matchmaking never mix.
func (m *Manager) EnterSync(roomID, playerID, name string, mode SyncMode) (string, State, error) {
	if mode != SyncArena {
		return m.Enter(roomID, playerID, name)
	}
	if playerID == "" {
		return "", State{}, errors.New("empty player id")
	}

	m.mu.Lock()
	defer m.mu.Unlock()
	m.arenaLockedInit()

	if roomID == "" {
		if existing, ok := m.findArenaPlayerLocked(playerID); ok {
			roomID = existing
		} else {
			roomID = m.pickArenaRoomLocked()
		}
	} else if _, ok := m.arenas[roomID]; !ok {
		m.arenas[roomID] = NewArena(m.capacity)
	}

	a := m.arenas[roomID]
	if !a.Has(playerID) && a.Count() >= m.capacity {
		return "", State{}, fmt.Errorf("%s: %w", roomID, ErrRoomFull)
	}
	return roomID, a.Enter(playerID, name), nil
}

func (m *Manager) findArenaPlayerLocked(playerID string) (string, bool) {
	for _, id := range m.order {
		if a, ok := m.arenas[id]; ok && a.Has(playerID) {
			return id, true
		}
	}
	return "", false
}

func (m *Manager) pickArenaRoomLocked() string {
	best := ""
	bestPlayers := m.capacity
	for _, id := range m.order {
		a, ok := m.arenas[id]
		if !ok {
			continue
		}
		players := a.Count()
		if players < bestPlayers {
			best, bestPlayers = id, players
		}
	}
	if best != "" {
		return best
	}
	m.next++
	id := fmt.Sprintf("arena3-%d", m.next)
	m.arenas[id] = NewArena(m.capacity)
	m.order = append(m.order, id)
	return id
}

// Mode reports the sync algorithm of a room (frame by default).
func (m *Manager) Mode(roomID string) SyncMode {
	m.mu.Lock()
	defer m.mu.Unlock()
	if _, ok := m.arenas[roomID]; ok {
		return SyncArena
	}
	return SyncFrame
}

// SubmitCmd queues a usercmd in an arena room.
func (m *Manager) SubmitCmd(roomID, playerID string, cmd Cmd) bool {
	m.mu.Lock()
	a, ok := m.arenas[roomID]
	m.mu.Unlock()
	if !ok {
		return false
	}
	return a.SubmitCmd(playerID, cmd)
}

// ArenaDelta returns the delta for one client based on its acked snapshot.
func (m *Manager) ArenaDelta(roomID, playerID string, ack uint32) (Delta, bool) {
	m.mu.Lock()
	a, ok := m.arenas[roomID]
	m.mu.Unlock()
	if !ok {
		return Delta{}, false
	}
	return a.DeltaFor(playerID, ack), true
}

// ArenaSnapshotFor returns the latest snapshot with the player ack applied.
func (m *Manager) ArenaSnapshotFor(roomID, playerID string) (Snapshot, bool) {
	m.mu.Lock()
	a, ok := m.arenas[roomID]
	m.mu.Unlock()
	if !ok {
		return Snapshot{}, false
	}
	return a.SnapshotFor(playerID), true
}

// ArenaStep advances an arena room by one tick.
func (m *Manager) ArenaStep(roomID string) (Snapshot, bool) {
	m.mu.Lock()
	a, ok := m.arenas[roomID]
	m.mu.Unlock()
	if !ok {
		return Snapshot{}, false
	}
	return a.Step(), true
}

// ArenaIDs returns arena room ids in creation order.
func (m *Manager) ArenaIDs() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	ids := make([]string, 0, len(m.arenas))
	for _, id := range m.order {
		if _, ok := m.arenas[id]; ok {
			ids = append(ids, id)
		}
	}
	return ids
}

// TickOf returns the current tick for a frame or arena room.
func (m *Manager) TickOf(roomID string) uint64 {
	m.mu.Lock()
	defer m.mu.Unlock()
	if a, ok := m.arenas[roomID]; ok {
		return a.Tick()
	}
	if r, ok := m.rooms[roomID]; ok {
		return r.Tick()
	}
	return 0
}
