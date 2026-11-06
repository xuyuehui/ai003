package room

import "testing"

func TestArenaRoomStepsAndAcks(t *testing.T) {
	a := NewArena(8)
	a.Enter("a", "A")
	a.Enter("b", "B")

	if !a.SubmitCmd("a", Cmd{Seq: 1, Forward: 1, Msec: 50}) {
		t.Fatal("cmd 1 rejected")
	}
	if !a.SubmitCmd("b", Cmd{Seq: 1, Side: 1, Msec: 50}) {
		t.Fatal("cmd 1 rejected")
	}

	snap := a.Step()
	if len(snap.Players) != 2 {
		t.Fatalf("players = %d, want 2", len(snap.Players))
	}
	if snap.Seq == 0 {
		t.Fatal("snapshot seq should advance")
	}
	if a.SnapshotFor("a").Ack != 1 {
		t.Fatalf("ack = %d, want 1", a.SnapshotFor("a").Ack)
	}
}

func TestArenaRoomDropsDuplicatesAndOrdersOutOfOrderCmds(t *testing.T) {
	a := NewArena(8)
	a.Enter("a", "A")

	if !a.SubmitCmd("a", Cmd{Seq: 5, Forward: 1}) {
		t.Fatal("seq 5 rejected")
	}
	if a.SubmitCmd("a", Cmd{Seq: 5, Forward: 1}) {
		t.Fatal("duplicate seq should be dropped")
	}
	if a.SubmitCmd("a", Cmd{Seq: 4, Forward: 1}) {
		t.Fatal("stale seq should be dropped")
	}
	if !a.SubmitCmd("a", Cmd{Seq: 7, Forward: 1}) {
		t.Fatal("seq 7 rejected")
	}
	if !a.SubmitCmd("a", Cmd{Seq: 6, Forward: 1}) {
		t.Fatal("out-of-order seq 6 rejected")
	}

	// Queue must be [5,6,7]; each Step consumes one in order.
	for want := uint32(5); want <= 7; want++ {
		a.Step()
		if got := a.SnapshotFor("a").Ack; got != want {
			t.Fatalf("ack = %d, want %d", got, want)
		}
	}
}

func TestArenaRoomDeltaAgainstAck(t *testing.T) {
	a := NewArena(8)
	a.Enter("a", "A")
	a.SubmitCmd("a", Cmd{Seq: 1, Forward: 1})
	first := a.Step()

	a.SubmitCmd("a", Cmd{Seq: 2, Forward: 1})
	second := a.Step()

	delta := a.DeltaFor("a", first.Seq)
	if delta.Base != first.Seq {
		t.Fatalf("base = %d, want %d", delta.Base, first.Seq)
	}
	if delta.Seq != second.Seq {
		t.Fatalf("seq = %d, want %d", delta.Seq, second.Seq)
	}
	if len(delta.Players) == 0 {
		t.Fatal("expected at least the moving player in the delta")
	}

	full := a.DeltaFor("a", 0)
	if full.Base != 0 {
		t.Fatalf("unknown base should produce a full delta, got base %d", full.Base)
	}
}

func TestArenaRoomRepeatsLastCmdOnPacketLoss(t *testing.T) {
	a := NewArena(8)
	a.Enter("a", "A")
	a.SubmitCmd("a", Cmd{Seq: 1, Forward: 1})
	first := a.Step()
	// No cmd this tick: the server repeats the last command (Quake behaviour).
	a.Step()
	moved := a.SnapshotFor("a").Players[0].Origin[2] != first.Players[0].Origin[2]
	if !moved {
		t.Fatalf("player should keep moving with the repeated cmd: %v -> %v",
			first.Players[0].Origin, a.SnapshotFor("a").Players[0].Origin)
	}
}
func TestArenaRoomCombatAndReconnect(t *testing.T) {
	a := NewArena(8)
	a.Enter("a", "A")
	a.Enter("b", "B")

	// Walk A towards B (slot 0 at 0,0; slot 1 at 4000,0) until in attack range.
	for i := 0; i < 6; i++ {
		a.SubmitCmd("a", Cmd{Seq: uint32(i + 1), Side: 1})
		a.Step()
	}
	a.SubmitCmd("a", Cmd{Seq: 7, Buttons: 1})
	snap := a.Step()

	damaged := false
	for _, p := range snap.Players {
		if p.ID == 2 && p.Health < 100 {
			damaged = true
		}
	}
	if !damaged {
		t.Fatalf("attack did not damage the opponent: %+v", snap.Players)
	}

	// Reconnect keeps the player in the room with its state.
	a.MarkOffline("a")
	if a.HasOnline("a") {
		t.Fatal("player should be offline")
	}
	a.Enter("a", "A")
	if !a.HasOnline("a") {
		t.Fatal("reconnect should bring the player back online")
	}
	if a.Count() != 2 {
		t.Fatalf("players = %d, want 2", a.Count())
	}
}
