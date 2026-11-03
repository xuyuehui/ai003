package room

import (
	"reflect"
	"testing"
)

func TestMovementAndArenaClamp(t *testing.T) {
	r := New()
	r.Enter("p1", "Alice")

	r.SetInput("p1", Input{DX: 1})
	frame := r.Step()
	if got := frame.Players[0].X; got != SpeedMMPerTick {
		t.Fatalf("x after one tick = %d, want %d", got, SpeedMMPerTick)
	}

	// Walking east for 200 ticks must stop at the arena edge.
	for i := 0; i < 200; i++ {
		r.Step()
	}
	if got := r.Step().Players[0].X; got != ArenaHalfMM {
		t.Fatalf("x after long walk = %d, want clamp %d", got, ArenaHalfMM)
	}
}

func TestDiagonalIsNotFaster(t *testing.T) {
	r := New()
	r.Enter("p1", "Alice")
	r.SetInput("p1", Input{DX: 1, DZ: 1})

	state := r.Step().Players[0]
	if state.X != DiagonalMMPerTick || state.Z != DiagonalMMPerTick {
		t.Fatalf("diagonal step = (%d,%d), want (%d,%d)", state.X, state.Z, DiagonalMMPerTick, DiagonalMMPerTick)
	}
}

func TestAttackCooldownAndDeath(t *testing.T) {
	r := New()
	r.Enter("attacker", "A")
	r.Enter("target", "B")

	// Attacker spawns at (0,0), target at (4000,0): walk into range first.
	r.SetInput("attacker", Input{DX: 1})
	for i := 0; i < 4; i++ {
		r.Step()
	}

	// In range now; stand still and hold attack.
	r.SetInput("attacker", Input{Buttons: ButtonAttack})
	frame := r.Step()
	if hp := playerByID(frame, "target").HP; hp != MaxHP-AttackDamage {
		t.Fatalf("hp after first hit = %d, want %d", hp, MaxHP-AttackDamage)
	}

	// The cooldown must swallow the next attack.
	frame = r.Step()
	if hp := playerByID(frame, "target").HP; hp != MaxHP-AttackDamage {
		t.Fatalf("hp during cooldown = %d, want %d", hp, MaxHP-AttackDamage)
	}

	// Wait out the cooldown, then keep attacking until the target dies.
	for i := 0; i < AttackCooldownTicks; i++ {
		r.Step()
	}
	var died bool
	for i := 0; i < 200 && !died; i++ {
		frame = r.Step()
		for _, event := range frame.Events {
			if event.Type == "death" && event.Target == "target" {
				died = true
			}
		}
	}
	if !died {
		t.Fatalf("target never died, hp=%d", playerByID(frame, "target").HP)
	}

	dead := playerByID(frame, "target")
	if dead.Alive || dead.HP != 0 {
		t.Fatalf("dead state = %+v, want alive=false hp=0", dead)
	}

	// Stop attacking so the respawned target is not hit again immediately.
	r.SetInput("attacker", Input{})

	// After the respawn delay it comes back at full health.
	var respawned bool
	for i := 0; i < RespawnTicks+2; i++ {
		frame = r.Step()
		for _, event := range frame.Events {
			if event.Type == "respawn" && event.Source == "target" {
				respawned = true
			}
		}
	}
	if !respawned {
		t.Fatal("target did not respawn")
	}
	if hp := playerByID(frame, "target").HP; hp != MaxHP {
		t.Fatalf("hp after respawn = %d, want %d", hp, MaxHP)
	}
}

func TestOutOfRangeAndCone(t *testing.T) {
	r := New()
	r.Enter("a", "A")
	r.Enter("b", "B")

	// Facing +z, target at +x 4 m: outside both cone and range.
	r.SetInput("a", Input{Buttons: ButtonAttack})
	frame := r.Step()
	if hp := playerByID(frame, "b").HP; hp != MaxHP {
		t.Fatalf("hit through range/cone: hp=%d", hp)
	}
}

func TestDeterministicFrames(t *testing.T) {
	first, second := New(), New()
	for _, r := range []*Room{first, second} {
		r.Enter("p1", "A")
		r.Enter("p2", "B")
	}

	for tick := 0; tick < 120; tick++ {
		input1 := Input{DX: int8(tick%3 - 1)}
		input2 := Input{DZ: int8(1 - tick%3)}
		if tick%7 == 0 {
			input1.Buttons = ButtonAttack
		}
		first.SetInput("p1", input1)
		second.SetInput("p1", input1)
		first.SetInput("p2", input2)
		second.SetInput("p2", input2)

		frameA := first.Step()
		frameB := second.Step()
		if !reflect.DeepEqual(frameA, frameB) {
			t.Fatalf("tick %d diverged:\nA=%+v\nB=%+v", tick, frameA, frameB)
		}
	}
}

func TestLeave(t *testing.T) {
	r := New()
	r.Enter("p1", "A")
	r.Enter("p2", "B")
	r.Leave("p1")

	frame := r.Step()
	if len(frame.Players) != 1 || frame.Players[0].ID != "p2" {
		t.Fatalf("players after leave = %+v", frame.Players)
	}
	if r.SetInput("p1", Input{}) {
		t.Fatal("SetInput accepted a player who left")
	}
}

func playerByID(frame Frame, id string) State {
	for _, state := range frame.Players {
		if state.ID == id {
			return state
		}
	}
	return State{}
}
