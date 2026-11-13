package room

import (
	"errors"
	"reflect"
	"strings"
	"testing"
)

// --- arena matchmaking -----------------------------------------------------

func TestArenaMatchmakingUsesItsOwnRoomPool(t *testing.T) {
	m := NewManager(2)

	// A frame room first, so this test exercises pooling and not the
	// mix-mode dereference guarded by TestFrameMatchmakingAfterAnArenaRoom.
	frameRoom, _, err := m.Enter("", "f1", "F")
	if err != nil {
		t.Fatalf("frame enter: %v", err)
	}

	first, _, err := m.EnterSync("", "a1", "A", SyncArena)
	if err != nil {
		t.Fatalf("first arena enter: %v", err)
	}
	// Arena ids share the manager's counter with frame rooms, so only the
	// prefix and the ordering are contractual.
	if !strings.HasPrefix(first, "arena3-") {
		t.Fatalf("first arena room = %q, want an arena3-* id", first)
	}
	if first == frameRoom {
		t.Fatalf("arena matchmaking reused the frame room %q", frameRoom)
	}
	if second, _, err := m.EnterSync("", "a2", "B", SyncArena); err != nil || second != first {
		t.Fatalf("second arena enter = %q, %v; want %q", second, err, first)
	}
	third, _, err := m.EnterSync("", "a3", "C", SyncArena)
	if err != nil {
		t.Fatalf("third arena enter: %v", err)
	}
	if third == first {
		t.Fatalf("third arena enter reused the full room %q", first)
	}
	if got := m.Mode(third); got != SyncArena {
		t.Fatalf("Mode(%q) = %q, want %q", third, got, SyncArena)
	}

	if m.Mode(frameRoom) != SyncFrame {
		t.Fatalf("frame room %q reports mode %q", frameRoom, m.Mode(frameRoom))
	}
	if frameRoom == first || frameRoom == third {
		t.Fatalf("frame matchmaking joined arena room %q", frameRoom)
	}

	ids := m.ArenaIDs()
	if len(ids) != 2 || ids[0] != first || ids[1] != third {
		t.Fatalf("ArenaIDs = %v, want [%s %s]", ids, first, third)
	}

	sawArena, sawFrame := false, false
	for _, info := range m.List() {
		switch info.Sync {
		case string(SyncArena):
			sawArena = true
		case string(SyncFrame):
			sawFrame = true
		default:
			t.Fatalf("unexpected sync mode %q in List", info.Sync)
		}
	}
	if !sawArena || !sawFrame {
		t.Fatalf("List did not report both pools: %+v", m.List())
	}
}

func TestArenaReconnectReturnsSameRoom(t *testing.T) {
	m := NewManager(4)
	id, _, err := m.EnterSync("", "a1", "A", SyncArena)
	if err != nil {
		t.Fatalf("enter: %v", err)
	}
	m.MarkOffline(id, "a1")
	if got, ok := m.FindOnline("a1"); ok || got != "" {
		t.Fatalf("offline arena player reported online in %q", got)
	}

	again, _, err := m.EnterSync("", "a1", "A", SyncArena)
	if err != nil {
		t.Fatalf("reconnect: %v", err)
	}
	if again != id {
		t.Fatalf("reconnect room = %q, want %q", again, id)
	}
	if got, ok := m.FindOnline("a1"); !ok || got != id {
		t.Fatalf("reconnected player FindOnline = %q,%v, want %q,true", got, ok, id)
	}
}

func TestArenaExplicitRoomIsCreatedOnDemandAndRespectsCapacity(t *testing.T) {
	m := NewManager(1)
	id, _, err := m.EnterSync("duel", "a1", "A", SyncArena)
	if err != nil || id != "duel" {
		t.Fatalf("explicit arena enter = %q, %v", id, err)
	}
	if _, _, err := m.EnterSync("duel", "a2", "B", SyncArena); !errors.Is(err, ErrRoomFull) {
		t.Fatalf("second explicit enter error = %v, want ErrRoomFull", err)
	}
	if _, _, err := m.EnterSync("duel", "a1", "A", SyncArena); err != nil {
		t.Fatalf("re-entering the same player must be allowed: %v", err)
	}
	if _, _, err := m.EnterSync("", "", "X", SyncArena); err == nil {
		t.Fatal("empty player id was accepted in arena mode")
	}
	if m.Count() != 0 {
		t.Fatalf("arena rooms leaked into the frame pool: Count = %d", m.Count())
	}
}

// TestFrameMatchmakingAfterAnArenaRoom guards against the manager dereferencing
// a nil frame room: arena ids share m.order, so a "" frame Enter must skip them.
func TestFrameMatchmakingAfterAnArenaRoom(t *testing.T) {
	m := NewManager(4)
	if _, _, err := m.EnterSync("duel", "a1", "A", SyncArena); err != nil {
		t.Fatalf("arena enter: %v", err)
	}

	defer func() {
		if r := recover(); r != nil {
			t.Fatalf("frame matchmaking panicked while an arena room existed: %v", r)
		}
	}()

	roomID, state, err := m.Enter("", "f1", "F")
	if err != nil {
		t.Fatalf("frame enter: %v", err)
	}
	if m.Mode(roomID) != SyncFrame {
		t.Fatalf("player landed in %q (mode %q), want a frame room", roomID, m.Mode(roomID))
	}
	if state.ID != "f1" {
		t.Fatalf("state.ID = %q, want f1", state.ID)
	}
}

// --- arena command queue ---------------------------------------------------

func TestArenaSubmitCmdRejectsUnknownPlayerAndStaleSeq(t *testing.T) {
	m := NewManager(4)
	if _, _, err := m.EnterSync("duel", "a1", "A", SyncArena); err != nil {
		t.Fatalf("enter: %v", err)
	}

	if m.SubmitCmd("nope", "a1", Cmd{Seq: 1}) {
		t.Fatal("SubmitCmd accepted an unknown room")
	}
	if m.SubmitCmd("duel", "stranger", Cmd{Seq: 1}) {
		t.Fatal("SubmitCmd accepted an offline player")
	}
	if !m.SubmitCmd("duel", "a1", Cmd{Seq: 1, Forward: 1}) {
		t.Fatal("SubmitCmd rejected the first command")
	}
	if _, ok := m.ArenaStep("duel"); !ok {
		t.Fatal("ArenaStep failed")
	}
	if m.SubmitCmd("duel", "a1", Cmd{Seq: 1, Forward: 1}) {
		t.Fatal("SubmitCmd accepted a replayed sequence")
	}
	if !m.SubmitCmd("duel", "a1", Cmd{Seq: 2, Forward: 1}) {
		t.Fatal("SubmitCmd rejected a newer sequence")
	}
}

func TestArenaSubmitCmdHonoursTheReorderWindow(t *testing.T) {
	a := NewArena(4)
	a.Enter("p1", "P")

	// Arrival order 5, 7, 6: the middle command is inserted in sequence order
	// (the documented "bounded look-ahead window").
	for _, seq := range []uint32{5, 7} {
		if !a.SubmitCmd("p1", Cmd{Seq: seq, Forward: 1}) {
			t.Fatalf("SubmitCmd rejected seq %d", seq)
		}
	}
	if !a.SubmitCmd("p1", Cmd{Seq: 6, Forward: 1}) {
		t.Fatal("SubmitCmd rejected seq 6 arriving between 5 and 7")
	}
	for _, want := range []uint32{5, 6, 7} {
		a.Step()
		if got := a.SnapshotFor("p1").Ack; got != want {
			t.Fatalf("ack after step = %d, want %d (commands must execute in seq order)", got, want)
		}
	}

	// Replays and stale sequences are dropped.
	if a.SubmitCmd("p1", Cmd{Seq: 7, Forward: 1}) {
		t.Fatal("SubmitCmd accepted a replayed sequence")
	}
	if a.SubmitCmd("p1", Cmd{Seq: 4, Forward: 1}) {
		t.Fatal("SubmitCmd accepted a stale sequence below the executed one")
	}
	if !a.SubmitCmd("p1", Cmd{Seq: 8, Forward: 1}) {
		t.Fatal("SubmitCmd rejected the next fresh sequence")
	}
}

func TestArenaCommandQueueIsBounded(t *testing.T) {
	a := NewArena(4)
	a.Enter("p1", "P")
	for seq := uint32(1); seq <= 1000; seq++ {
		if !a.SubmitCmd("p1", Cmd{Seq: seq, Forward: 1}) {
			t.Fatalf("SubmitCmd rejected seq %d", seq)
		}
	}

	a.mu.Lock()
	queue := append([]Cmd(nil), a.cmds["p1"]...)
	a.mu.Unlock()

	if len(queue) > 128 {
		t.Fatalf("command queue grew to %d entries; it must stay bounded", len(queue))
	}
	if len(queue) == 0 {
		t.Fatal("command queue dropped every command")
	}
	if last := queue[len(queue)-1]; last.Seq != 1000 {
		t.Fatalf("newest retained seq = %d, want 1000", last.Seq)
	}
	if first := queue[0]; first.Seq == 1 {
		t.Fatal("queue never trimmed: oldest seq is still 1 after 1000 submits")
	}

	// The backlog still drains: the newest command is executed eventually.
	for i := 0; i <= len(queue); i++ {
		a.Step()
	}
	if got := a.SnapshotFor("p1").Ack; got != 1000 {
		t.Fatalf("final ack = %d, want 1000", got)
	}
}

func TestArenaLeaveDropsQueuedCommands(t *testing.T) {
	a := NewArena(4)
	a.Enter("p1", "P")
	if !a.SubmitCmd("p1", Cmd{Seq: 5, Forward: 1}) {
		t.Fatal("SubmitCmd rejected the first command")
	}

	a.Leave("p1")
	a.mu.Lock()
	_, hasQueue := a.cmds["p1"]
	_, hasLast := a.lastCmd["p1"]
	_, hasAck := a.ackSeq["p1"]
	a.mu.Unlock()
	if hasQueue || hasLast || hasAck {
		t.Fatalf("Leave left per-player state behind: queue=%v last=%v ack=%v", hasQueue, hasLast, hasAck)
	}

	a.Enter("p1", "P")
	snap := a.Step()
	if snap.Ack != 0 {
		t.Fatalf("stale cmd survived a leave: ack = %d", snap.Ack)
	}
}

// --- arena snapshots / delta compression -----------------------------------

func TestArenaDeltaCompressesAgainstTheAckedBase(t *testing.T) {
	a := NewArena(4)
	a.Enter("p1", "P")
	a.Enter("p2", "Q")

	// Two ticks of movement so the player is away from the spawn point. p2
	// never submits, so it stays still and must not show up in the delta.
	for seq := uint32(1); seq <= 2; seq++ {
		a.SubmitCmd("p1", Cmd{Seq: seq, Forward: 1})
		a.Step()
	}
	base := a.SnapshotFor("p1")
	if base.Seq == 0 || len(base.Players) != 2 {
		t.Fatalf("base snapshot = %+v", base)
	}

	// No new input: the delta against this base must be empty.
	if d := a.DeltaFor("p1", base.Seq); len(d.Players) != 0 || len(d.Removed) != 0 {
		t.Fatalf("idle delta = %+v, want no player changes", d)
	}

	// Move only toward +Z again so Origin changes but health does not.
	a.SubmitCmd("p1", Cmd{Seq: 3, Forward: 1})
	a.Step()
	cur := a.SnapshotFor("p1")

	d := a.DeltaFor("p1", base.Seq)
	if d.Base != base.Seq {
		t.Fatalf("delta base = %d, want %d", d.Base, base.Seq)
	}
	if len(d.Players) != 1 {
		t.Fatalf("delta carries %d players, want 1 (only p1 changed)", len(d.Players))
	}
	if d.Players[0].Mask != FieldOrigin {
		t.Fatalf("changed-player mask = %#x, want FieldOrigin only (%#x)", d.Players[0].Mask, FieldOrigin)
	}

	restored := ApplyDelta(&base, d)
	if !reflect.DeepEqual(restored.Players, cur.Players) {
		t.Fatalf("ApplyDelta round-trip mismatch:\n got %+v\nwant %+v", restored.Players, cur.Players)
	}
	if restored.Seq != cur.Seq {
		t.Fatalf("restored seq = %d, want %d", restored.Seq, cur.Seq)
	}
}

func TestArenaDeltaFallsBackToFullSnapshotForUnknownBase(t *testing.T) {
	a := NewArena(4)
	a.Enter("p1", "P")
	a.Step()

	for _, base := range []uint32{0, 999999} {
		d := a.DeltaFor("p1", base)
		if d.Base != 0 {
			t.Fatalf("delta for base %d claims base %d, want 0", base, d.Base)
		}
		if len(d.Players) != 1 {
			t.Fatalf("full delta carries %d players, want 1", len(d.Players))
		}
		want := FieldOrigin | FieldVelocity | FieldHealth | FieldYaw | FieldFlags
		if d.Players[0].Mask != want {
			t.Fatalf("full delta mask = %#x, want %#x", d.Players[0].Mask, want)
		}
	}
}

func TestApplyDeltaDropsRemovedPlayers(t *testing.T) {
	base := Snapshot{Seq: 5, Players: []ArenaPlayer{
		{ID: 1, Origin: [3]int32{1, 0, 1}, Health: 100},
		{ID: 2, Origin: [3]int32{2, 0, 2}, Health: 100},
	}}
	d := Delta{Seq: 6, Base: 5, Removed: []uint8{2}}

	got := ApplyDelta(&base, d)
	if len(got.Players) != 1 || got.Players[0].ID != 1 {
		t.Fatalf("ApplyDelta with removal = %+v", got.Players)
	}

	// BuildDelta must report the same removal from the other direction.
	cur := Snapshot{Seq: 6, Players: []ArenaPlayer{{ID: 1, Origin: [3]int32{1, 0, 1}, Health: 100}}}
	if bd := BuildDelta(&base, cur); len(bd.Removed) != 1 || bd.Removed[0] != 2 {
		t.Fatalf("BuildDelta removed = %v, want [2]", bd.Removed)
	}
}

func TestArenaTickAndRoomIdReports(t *testing.T) {
	m := NewManager(4)
	// Frame room first: see TestFrameMatchmakingAfterAnArenaRoom for the
	// reverse order, which currently panics in pickRoomLocked.
	frameID, _, err := m.Enter("", "f1", "F")
	if err != nil {
		t.Fatalf("frame enter: %v", err)
	}
	arenaID, _, err := m.EnterSync("duel", "a1", "A", SyncArena)
	if err != nil {
		t.Fatalf("arena enter: %v", err)
	}

	if got := m.TickOf(arenaID); got != 0 {
		t.Fatalf("fresh arena tick = %d, want 0", got)
	}
	for i := 1; i <= 3; i++ {
		if _, ok := m.ArenaStep(arenaID); !ok {
			t.Fatal("ArenaStep failed")
		}
		if got := m.TickOf(arenaID); got != uint64(i) {
			t.Fatalf("arena tick after %d steps = %d", i, got)
		}
	}
	if _, ok := m.Step(frameID); !ok {
		t.Fatal("frame Step failed")
	}
	if got := m.TickOf(frameID); got != 1 {
		t.Fatalf("frame tick = %d, want 1", got)
	}
	if got := m.TickOf("missing"); got != 0 {
		t.Fatalf("TickOf(missing) = %d, want 0", got)
	}

	if _, ok := m.ArenaSnapshotFor("missing", "a1"); ok {
		t.Fatal("ArenaSnapshotFor accepted an unknown room")
	}
	if _, ok := m.ArenaDelta("missing", "a1", 0); ok {
		t.Fatal("ArenaDelta accepted an unknown room")
	}
	if _, ok := m.ArenaStep("missing"); ok {
		t.Fatal("ArenaStep accepted an unknown room")
	}
}

func TestArenaRoomsAreCleanedUpWhenEmpty(t *testing.T) {
	m := NewManager(4)
	id, _, err := m.EnterSync("", "a1", "A", SyncArena)
	if err != nil {
		t.Fatalf("enter: %v", err)
	}
	m.Leave(id, "a1")

	for i := 0; i <= EmptyRoomGraceTicks; i++ {
		m.ArenaStep(id)
	}
	if removed := m.CleanupEmpty(EmptyRoomGraceTicks); removed != 1 {
		t.Fatalf("CleanupEmpty removed %d rooms, want 1", removed)
	}
	if ids := m.ArenaIDs(); len(ids) != 0 {
		t.Fatalf("ArenaIDs after cleanup = %v", ids)
	}
	if ids := m.RoomIDs(); len(ids) != 0 {
		t.Fatalf("RoomIDs after cleanup = %v", ids)
	}
	if m.Count() != 0 {
		t.Fatalf("Count after cleanup = %d", m.Count())
	}
}

// --- frame room through the manager ---------------------------------------

func TestManagerSetInputAndSnapshot(t *testing.T) {
	m := NewManager(4)
	if m.SetInput("missing", "p1", 0, Input{DX: 1}) {
		t.Fatal("SetInput accepted an unknown room")
	}
	if _, _, ok := m.Snapshot("missing"); ok {
		t.Fatal("Snapshot accepted an unknown room")
	}

	roomID, _, err := m.Enter("", "p1", "P")
	if err != nil {
		t.Fatalf("enter: %v", err)
	}
	tick, states, ok := m.Snapshot(roomID)
	if !ok || tick != 0 || len(states) != 1 {
		t.Fatalf("Snapshot = %d, %+v, %v", tick, states, ok)
	}
	startX := states[0].X

	if !m.SetInput(roomID, "p1", 0, Input{DX: 1}) {
		t.Fatal("SetInput rejected a known player")
	}
	frame, ok := m.Step(roomID)
	if !ok || len(frame.Players) != 1 {
		t.Fatalf("Step = %+v, %v", frame, ok)
	}
	if frame.Players[0].X <= startX {
		t.Fatalf("X after SetInput = %d, want > %d", frame.Players[0].X, startX)
	}

	tick, states, ok = m.Snapshot(roomID)
	if !ok || tick != 1 || states[0].X != frame.Players[0].X {
		t.Fatalf("Snapshot after step = %d, %+v, %v", tick, states, ok)
	}
}
