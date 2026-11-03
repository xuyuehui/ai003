package worldgen

import (
	"bytes"
	"math"
	"testing"
)

func TestGenerateIsDeterministic(t *testing.T) {
	a, err := Generate(42, 512, 129)
	if err != nil {
		t.Fatal(err)
	}
	b, err := Generate(42, 512, 129)
	if err != nil {
		t.Fatal(err)
	}

	if !bytes.Equal(float32Bytes(a.Heights), float32Bytes(b.Heights)) {
		t.Fatal("heights differ between two runs with the same seed")
	}
	if !bytes.Equal(a.Colors, b.Colors) {
		t.Fatal("colours differ between two runs with the same seed")
	}
	if a.Metadata.Spawn != b.Metadata.Spawn {
		t.Fatalf("spawn differs: %+v vs %+v", a.Metadata.Spawn, b.Metadata.Spawn)
	}
}

func TestDifferentSeedsDiffer(t *testing.T) {
	a, err := Generate(1, 512, 129)
	if err != nil {
		t.Fatal(err)
	}
	b, err := Generate(2, 512, 129)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Equal(float32Bytes(a.Heights), float32Bytes(b.Heights)) {
		t.Fatal("different seeds produced identical heights")
	}
}

func TestWorldContainsEveryBiome(t *testing.T) {
	w, err := Generate(DefaultSeed, DefaultSize, DefaultResolution)
	if err != nil {
		t.Fatal(err)
	}

	counts := map[uint8]int{}
	for _, b := range w.Biomes {
		counts[b]++
	}
	for _, biome := range []struct {
		id   uint8
		name string
	}{
		{BiomeOcean, "ocean"},
		{BiomeGrass, "grass"},
		{BiomeDesert, "desert"},
		{BiomeRock, "rock"},
		{BiomeSnow, "snow"},
	} {
		if counts[biome.id] < len(w.Biomes)/200 {
			t.Errorf("biome %s covers only %d vertices", biome.name, counts[biome.id])
		}
	}
}

func TestSpawnIsDryAndFlat(t *testing.T) {
	w, err := Generate(DefaultSeed, DefaultSize, DefaultResolution)
	if err != nil {
		t.Fatal(err)
	}
	spawn := w.Metadata.Spawn
	if spawn.Y <= w.Metadata.SeaLevel+1 {
		t.Fatalf("spawn is not above sea level: %+v", spawn)
	}

	h := w.HeightAt(spawn.X, spawn.Z)
	if math.Abs(h-spawn.Y) > 2 {
		t.Fatalf("spawn height %.1f does not match terrain %.1f", spawn.Y, h)
	}

	// Sample a 4 m square around the spawn: it should be walkable.
	for _, d := range []float64{-4, 0, 4} {
		for _, e := range []float64{-4, 0, 4} {
			if got := w.HeightAt(spawn.X+d, spawn.Z+e); got < w.Metadata.SeaLevel {
				t.Fatalf("terrain around spawn is underwater at (%.0f,%.0f): %.1f", d, e, got)
			}
		}
	}
}

func TestHeightAtBilinear(t *testing.T) {
	w, err := Generate(7, 256, 65)
	if err != nil {
		t.Fatal(err)
	}
	cell := w.Metadata.CellSize
	half := w.Metadata.SizeMeters / 2

	// Exactly on a grid vertex.
	if got, want := w.HeightAt(-half+cell*3, -half+cell*5), float64(w.Heights[5*w.Metadata.Resolution+3]); math.Abs(got-want) > 1e-4 {
		t.Fatalf("grid sample: got %v want %v", got, want)
	}

	// Midway between vertices must sit between the neighbours.
	h00 := w.HeightAt(-half+cell*3, -half+cell*5)
	h11 := w.HeightAt(-half+cell*4, -half+cell*6)
	mid := w.HeightAt(-half+cell*3.5, -half+cell*5.5)
	if mid < math.Min(h00, h11)-0.01 || mid > math.Max(h00, h11)+0.01 {
		t.Fatalf("bilinear midpoint %v outside [%v,%v]", mid, h00, h11)
	}

	// Outside the map falls back to deep water.
	if got := w.HeightAt(1e6, 1e6); got >= w.Metadata.SeaLevel {
		t.Fatalf("outside map should be below sea level, got %v", got)
	}
}

func float32Bytes(values []float32) []byte {
	out := make([]byte, len(values)*4)
	for i, v := range values {
		bits := math.Float32bits(v)
		out[i*4+0] = byte(bits)
		out[i*4+1] = byte(bits >> 8)
		out[i*4+2] = byte(bits >> 16)
		out[i*4+3] = byte(bits >> 24)
	}
	return out
}
