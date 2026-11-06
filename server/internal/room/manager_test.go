package room

import (
	"errors"
	"testing"
)

func TestManagerAutoMatchFillsThenCreates(t *testing.T) {
	m := NewManager(2)

	id1, _, err := m.Enter("", "p1", "A")
	if err != nil {
		t.Fatalf("first enter: %v", err)
	}
	id2, _, err := m.Enter("", "p2", "B")
	if err != nil {
		t.Fatalf("second enter: %v", err)
	}
	if id1 != id2 || id1 != "arena-1" {
		t.Fatalf("rooms = %q, %q, want both arena-1", id1, id2)
	}

	id3, _, err := m.Enter("", "p3", "C")
	if err != nil {
		t.Fatalf("third enter: %v", err)
	}
	if id3 != "arena-2" {
		t.Fatalf("third room = %q, want arena-2", id3)
	}

	if m.Count() != 2 {
		t.Fatalf("rooms = %d, want 2", m.Count())
	}

	list := m.List()
	if len(list) != 2 || list[0].Players != 2 || list[1].Players != 1 {
		t.Fatalf("list = %+v", list)
	}
	if list[0].Capacity != 2 {
		t.Fatalf("capacity = %d, want 2", list[0].Capacity)
	}
}

func TestManagerExplicitRoomFull(t *testing.T) {
	m := NewManager(1)
	if _, _, err := m.Enter("arena-9", "p1", "A"); err != nil {
		t.Fatalf("explicit create: %v", err)
	}
	if _, _, err := m.Enter("arena-9", "p2", "B"); !errors.Is(err, ErrRoomFull) {
		t.Fatalf("second enter error = %v, want ErrRoomFull", err)
	}
}

func TestManagerLeaveFreesSlot(t *testing.T) {
	m := NewManager(2)
	m.Enter("", "p1", "A")
	m.Enter("", "p2", "B")
	m.Leave("arena-1", "p1")

	id, _, err := m.Enter("", "p3", "C")
	if err != nil {
		t.Fatalf("enter after leave: %v", err)
	}
	if id != "arena-1" {
		t.Fatalf("matched room = %q, want arena-1", id)
	}
}

func TestManagerReconnectReturnsToSameRoom(t *testing.T) {
	m := NewManager(2)
	first, _, err := m.Enter("", "p1", "A")
	if err != nil {
		t.Fatalf("enter: %v", err)
	}
	m.Enter("", "p2", "B")
	m.MarkOffline(first, "p1")

	roomID, _, err := m.Enter("", "p1", "A")
	if err != nil {
		t.Fatalf("reconnect: %v", err)
	}
	if roomID != first {
		t.Fatalf("reconnect room = %q, want %q", roomID, first)
	}
}

func TestManagerStepOnlyKnownRoom(t *testing.T) {
	m := NewManager(2)
	if _, ok := m.Step("nope"); ok {
		t.Fatal("Step accepted an unknown room")
	}
	m.Enter("", "p1", "A")
	frame, ok := m.Step("arena-1")
	if !ok || len(frame.Players) != 1 {
		t.Fatalf("step = %+v ok=%v", frame, ok)
	}
}

func TestCleanupEmptyRoom(t *testing.T) {
	m := NewManager(DefaultCapacity)
	roomID, _, err := m.Enter("", "p1", "A")
	if err != nil {
		t.Fatalf("enter: %v", err)
	}
	m.Leave(roomID, "p1")

	for i := 0; i < EmptyRoomGraceTicks+1; i++ {
		m.Step(roomID)
	}

	if removed := m.CleanupEmpty(EmptyRoomGraceTicks); removed != 1 {
		t.Fatalf("cleanup removed %d rooms, want 1", removed)
	}
	if m.Count() != 0 {
		t.Fatalf("room count = %d, want 0", m.Count())
	}
}

func TestFindOnlineIgnoresOffline(t *testing.T) {
	m := NewManager(DefaultCapacity)
	roomID, _, err := m.Enter("", "p1", "A")
	if err != nil {
		t.Fatalf("enter: %v", err)
	}
	if got, ok := m.FindOnline("p1"); !ok || got != roomID {
		t.Fatalf("FindOnline = %q,%v want %q,true", got, ok, roomID)
	}
	m.MarkOffline(roomID, "p1")
	if _, ok := m.FindOnline("p1"); ok {
		t.Fatal("offline player should not be reported online")
	}
}
func TestExplicitArenaRoomIsListedAndSteppable(t *testing.T) {
	m := NewManager(8)
	id, _, err := m.EnterSync("duel-room", "a", "A", SyncArena)
	if err != nil || id != "duel-room" {
		t.Fatalf("EnterSync = %q, %v", id, err)
	}
	if m.Mode(id) != SyncArena {
		t.Fatalf("mode = %q", m.Mode(id))
	}
	found := false
	for _, rid := range m.RoomIDs() {
		if rid == id {
			found = true
		}
	}
	if !found {
		t.Fatal("explicit arena room missing from RoomIDs")
	}
	list := m.List()
	if len(list) != 1 || list[0].Sync != "arena" || list[0].ID != id {
		t.Fatalf("list = %+v", list)
	}
	if _, ok := m.ArenaStep(id); !ok {
		t.Fatal("ArenaStep failed for explicit room")
	}
}
