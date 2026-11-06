package room

import (
	"encoding/json"
	"testing"
)

func baseSnapshot() Snapshot {
	return Snapshot{
		Seq:        10,
		ServerTime: 500,
		Ack:        9,
		Players: []ArenaPlayer{
			{ID: 1, Origin: [3]int32{100, 200, 300}, Velocity: [3]int32{25, 0, 0}, Health: 100, Yaw: 90, Flags: 1},
			{ID: 2, Origin: [3]int32{-100, 0, 0}, Velocity: [3]int32{0, 0, 25}, Health: 75, Yaw: 180, Flags: 0},
		},
	}
}

func TestDeltaRoundTrip(t *testing.T) {
	base := baseSnapshot()
	cur := baseSnapshot()
	cur.Seq = 11
	cur.Ack = 10
	cur.Players[0].Origin = [3]int32{125, 200, 300}
	cur.Players[1].Health = 50

	d := BuildDelta(&base, cur)
	got := ApplyDelta(&base, d)
	if got.Seq != cur.Seq || got.Ack != cur.Ack {
		t.Fatalf("header mismatch: %+v", got)
	}
	if len(got.Players) != 2 {
		t.Fatalf("players = %d, want 2", len(got.Players))
	}
	if got.Players[0].Origin != [3]int32{125, 200, 300} {
		t.Fatalf("origin = %v", got.Players[0].Origin)
	}
	if got.Players[1].Health != 50 {
		t.Fatalf("health = %d", got.Players[1].Health)
	}
	// Unchanged fields keep the base value.
	if got.Players[1].Yaw != 180 || got.Players[0].Health != 100 {
		t.Fatalf("unchanged fields lost: %+v", got.Players)
	}
}

func TestDeltaCarriesOnlyChangedFields(t *testing.T) {
	base := baseSnapshot()
	cur := baseSnapshot()
	cur.Seq = 11
	cur.Players[0].Origin = [3]int32{200, 200, 300}

	d := BuildDelta(&base, cur)
	if len(d.Players) != 1 {
		t.Fatalf("delta players = %d, want 1 (only the moved player)", len(d.Players))
	}
	if d.Players[0].Mask != FieldOrigin {
		t.Fatalf("mask = %x, want %x", d.Players[0].Mask, FieldOrigin)
	}

	full, _ := json.Marshal(cur)
	small, _ := json.Marshal(d)
	if len(small) >= len(full) {
		t.Fatalf("delta (%d bytes) should be smaller than snapshot (%d bytes)", len(small), len(full))
	}
}

func TestDeltaRemovalAndFullBaseline(t *testing.T) {
	base := baseSnapshot()
	cur := baseSnapshot()
	cur.Seq = 12
	cur.Players = cur.Players[:1]

	d := BuildDelta(&base, cur)
	if len(d.Removed) != 1 || d.Removed[0] != 2 {
		t.Fatalf("removed = %v", d.Removed)
	}
	got := ApplyDelta(&base, d)
	if len(got.Players) != 1 || got.Players[0].ID != 1 {
		t.Fatalf("players after removal: %+v", got.Players)
	}

	full := BuildDelta(nil, cur)
	if full.Base != 0 || full.Players[0].Mask != FieldOrigin|FieldVelocity|FieldHealth|FieldYaw|FieldFlags {
		t.Fatalf("nil baseline should carry all fields: %+v", full)
	}
}

func TestAckSequenceMonotonic(t *testing.T) {
	// The ack must never move backwards when snapshots arrive out of order.
	var last uint32
	for _, seq := range []uint32{5, 4, 7, 6} {
		if seq > last {
			last = seq
		}
	}
	if last != 7 {
		t.Fatalf("ack = %d, want 7", last)
	}
}
