package gateway

import (
	"testing"

	"ai003/server/internal/room"
)

func TestMetricsCountRoomFrameEvents(t *testing.T) {
	m := newMetrics()

	frame := room.Frame{
		Tick: 3,
		Events: []room.Event{
			{Type: "attack", Source: "p1"},
			{Type: "hit", Source: "p1", Target: "p2", Damage: 25},
			{Type: "death", Source: "p1", Target: "p2"},
			{Type: "respawn", Source: "p2"},
			{Type: "attack", Source: "p2"},
			{Type: "unknown-event"},
		},
	}
	m.noteRoomFrame(frame)

	if got := m.roomFrames.Load(); got != 1 {
		t.Fatalf("roomFrames = %d, want 1", got)
	}
	if got := m.attackEvents.Load(); got != 2 {
		t.Fatalf("attackEvents = %d, want 2", got)
	}
	if got := m.hitEvents.Load(); got != 1 {
		t.Fatalf("hitEvents = %d, want 1", got)
	}
	if got := m.deathEvents.Load(); got != 1 {
		t.Fatalf("deathEvents = %d, want 1", got)
	}
	if got := m.respawnEvents.Load(); got != 1 {
		t.Fatalf("respawnEvents = %d, want 1", got)
	}

	// An empty frame still counts as a broadcast and adds no events.
	m.noteRoomFrame(room.Frame{Tick: 4})
	if got := m.roomFrames.Load(); got != 2 {
		t.Fatalf("roomFrames = %d, want 2", got)
	}
	if got := m.hitEvents.Load(); got != 1 {
		t.Fatalf("hitEvents drifted to %d after an eventless frame", got)
	}
}

func TestMetricsCountMessagesAndWorldSnapshots(t *testing.T) {
	m := newMetrics()
	first := []byte(`{"tick":1}`)
	second := []byte(`{"tick":2,"players":[]}`)
	m.noteMessage(Message{Type: "room.frame", Body: first})
	m.noteMessage(Message{Type: "room.broadcast", Body: second})
	m.noteWorldSnapshot()

	if got := m.messagesOut.Load(); got != 2 {
		t.Fatalf("messagesOut = %d, want 2", got)
	}
	if want := uint64(len(first) + len(second)); m.bytesOut.Load() != want {
		t.Fatalf("bytesOut = %d, want %d", m.bytesOut.Load(), want)
	}
	if got := m.worldSnaps.Load(); got != 1 {
		t.Fatalf("worldSnaps = %d, want 1", got)
	}
	if m.startedAt.IsZero() {
		t.Fatal("metrics start time was not recorded")
	}
}
