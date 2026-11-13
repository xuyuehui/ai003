package worldgen

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"image/png"
	"math"
	"os"
	"path/filepath"
	"reflect"
	"testing"
	"time"
)

func TestExportWritesEveryConsumableFile(t *testing.T) {
	w, err := Generate(7, 256, 33)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	dir := t.TempDir()
	if err := w.Export(dir); err != nil {
		t.Fatalf("export: %v", err)
	}

	res := w.Metadata.Resolution
	vertices := res * res

	// File names come from Metadata.Files because the clients read world.json
	// first and then open exactly these names.
	for key, want := range map[string]int{
		w.Metadata.Files["heightmap"]: vertices * 4,
		w.Metadata.Files["colors"]:    vertices * 4,
		w.Metadata.Files["biomes"]:    vertices,
	} {
		info, err := os.Stat(filepath.Join(dir, key))
		if err != nil {
			t.Fatalf("stat %s: %v", key, err)
		}
		if int(info.Size()) != want {
			t.Fatalf("%s size = %d bytes, want %d", key, info.Size(), want)
		}
	}

	// Heightmap: little-endian float32, bit-for-bit identical to the array.
	raw, err := os.ReadFile(filepath.Join(dir, w.Metadata.Files["heightmap"]))
	if err != nil {
		t.Fatalf("read heightmap: %v", err)
	}
	if len(raw) != len(w.Heights)*4 {
		t.Fatalf("heightmap bytes = %d, want %d", len(raw), len(w.Heights)*4)
	}
	for i, want := range w.Heights {
		got := math.Float32frombits(binary.LittleEndian.Uint32(raw[i*4 : i*4+4]))
		if got != want {
			t.Fatalf("height[%d] = %v, want %v", i, got, want)
		}
	}

	colors, err := os.ReadFile(filepath.Join(dir, w.Metadata.Files["colors"]))
	if err != nil {
		t.Fatalf("read colors: %v", err)
	}
	if !bytes.Equal(colors, w.Colors) {
		t.Fatal("colors.rgba8 does not match the in-memory array")
	}
	// Every vertex must be fully opaque; a zero alpha would render invisible.
	for i := 0; i < len(colors); i += 4 {
		if colors[i+3] != 255 {
			t.Fatalf("vertex %d alpha = %d, want 255", i/4, colors[i+3])
		}
	}

	biomes, err := os.ReadFile(filepath.Join(dir, w.Metadata.Files["biomes"]))
	if err != nil {
		t.Fatalf("read biomes: %v", err)
	}
	if !bytes.Equal(biomes, w.Biomes) {
		t.Fatal("biomes.r8 does not match the in-memory array")
	}
	for i, b := range biomes {
		if b > BiomeSnow {
			t.Fatalf("biome[%d] = %d, outside the documented 0..%d range", i, b, BiomeSnow)
		}
	}

	metaRaw, err := os.ReadFile(filepath.Join(dir, "world.json"))
	if err != nil {
		t.Fatalf("read world.json: %v", err)
	}
	var meta Metadata
	if err := json.Unmarshal(metaRaw, &meta); err != nil {
		t.Fatalf("decode world.json: %v", err)
	}
	if !reflect.DeepEqual(meta, w.Metadata) {
		t.Fatalf("world.json metadata mismatch:\n got %+v\nwant %+v", meta, w.Metadata)
	}
	if meta.Version != 1 {
		t.Fatalf("metadata version = %d, want 1", meta.Version)
	}

	f, err := os.Open(filepath.Join(dir, "preview.png"))
	if err != nil {
		t.Fatalf("open preview: %v", err)
	}
	defer f.Close()
	img, err := png.Decode(f)
	if err != nil {
		t.Fatalf("decode preview.png: %v", err)
	}
	if b := img.Bounds(); b.Dx() != res || b.Dy() != res {
		t.Fatalf("preview size = %dx%d, want %dx%d", b.Dx(), b.Dy(), res, res)
	}
}

func TestExportIsReproducibleAcrossRegeneration(t *testing.T) {
	first, err := Generate(20261103, 256, 65)
	if err != nil {
		t.Fatalf("generate first: %v", err)
	}
	second, err := Generate(20261103, 256, 65)
	if err != nil {
		t.Fatalf("generate second: %v", err)
	}

	dirA, dirB := t.TempDir(), t.TempDir()
	if err := first.Export(dirA); err != nil {
		t.Fatalf("export first: %v", err)
	}
	if err := second.Export(dirB); err != nil {
		t.Fatalf("export second: %v", err)
	}

	for _, name := range []string{"heightmap.f32", "colors.rgba8", "biomes.r8", "world.json"} {
		a, err := os.ReadFile(filepath.Join(dirA, name))
		if err != nil {
			t.Fatalf("read %s from first export: %v", name, err)
		}
		b, err := os.ReadFile(filepath.Join(dirB, name))
		if err != nil {
			t.Fatalf("read %s from second export: %v", name, err)
		}
		if !bytes.Equal(a, b) {
			t.Fatalf("%s differs between two exports of the same seed", name)
		}
	}
}

func TestGenerateIsDeterministicAtProductionScale(t *testing.T) {
	// Determinism is a property of the generator, not of one resolution, so this
	// runs the full pipeline at a production-sized 1025^2 grid: the shipped
	// 2049^2 world is already generated once per test binary by defaultWorld
	// (see TestWorldContainsEveryBiome / TestSpawnIsDryAndFlat), and a second
	// full-size run would blow the package's runtime budget.
	const resolution = 1025

	start := time.Now()
	first, err := Generate(DefaultSeed, DefaultSize, resolution)
	if err != nil {
		t.Fatalf("first run: %v", err)
	}
	one := time.Since(start)

	start = time.Now()
	second, err := Generate(DefaultSeed, DefaultSize, resolution)
	if err != nil {
		t.Fatalf("second run: %v", err)
	}
	two := time.Since(start)
	t.Logf("%d^2 generated in %v and %v", resolution, one, two)

	if len(first.Heights) != resolution*resolution {
		t.Fatalf("vertices = %d, want %d", len(first.Heights), resolution*resolution)
	}
	if !bytes.Equal(float32Bytes(first.Heights), float32Bytes(second.Heights)) {
		t.Fatal("heights differ between two runs with the same seed")
	}
	if !bytes.Equal(first.Colors, second.Colors) {
		t.Fatal("colours differ between two runs with the same seed")
	}
	if !bytes.Equal(first.Biomes, second.Biomes) {
		t.Fatal("biomes differ between two runs with the same seed")
	}
	if first.Metadata.Spawn != second.Metadata.Spawn {
		t.Fatalf("spawn differs: %+v vs %+v", first.Metadata.Spawn, second.Metadata.Spawn)
	}
}

func TestShippedWorldMatchesItsMetadata(t *testing.T) {
	w := defaultWorld(t)

	res := w.Metadata.Resolution
	if res != DefaultResolution {
		t.Fatalf("shipped resolution = %d, want %d", res, DefaultResolution)
	}
	if len(w.Heights) != res*res || len(w.Colors) != res*res*4 || len(w.Biomes) != res*res {
		t.Fatalf("array sizes do not match a %d^2 world: heights=%d colors=%d biomes=%d",
			res, len(w.Heights), len(w.Colors), len(w.Biomes))
	}
	if cell := w.Metadata.CellSize; math.Abs(cell-DefaultSize/float64(res-1)) > 1e-9 {
		t.Fatalf("cell size = %v, want %v", cell, DefaultSize/float64(res-1))
	}
	if w.Metadata.MinHeight >= w.Metadata.MaxHeight {
		t.Fatalf("height range = [%v, %v]", w.Metadata.MinHeight, w.Metadata.MaxHeight)
	}
	// The extreme values in the metadata must come from the height array.
	low, high := math.Inf(1), math.Inf(-1)
	for _, h := range w.Heights {
		v := float64(h)
		if v < low {
			low = v
		}
		if v > high {
			high = v
		}
	}
	if math.Abs(low-w.Metadata.MinHeight) > 1e-6 || math.Abs(high-w.Metadata.MaxHeight) > 1e-6 {
		t.Fatalf("metadata range = [%v, %v], actual = [%v, %v]",
			w.Metadata.MinHeight, w.Metadata.MaxHeight, low, high)
	}
	// The spawn must sit on the built world.
	if h := w.HeightAt(w.Metadata.Spawn.X, w.Metadata.Spawn.Z); math.Abs(h-w.Metadata.Spawn.Y) > 2 {
		t.Fatalf("spawn height %v does not match terrain %v", w.Metadata.Spawn.Y, h)
	}
}

func TestWriteFloat32IsLittleEndian(t *testing.T) {
	path := filepath.Join(t.TempDir(), "values.f32")
	values := []float32{0, 1, -1, 0.5, 123.456}
	if err := writeFloat32(path, values); err != nil {
		t.Fatalf("writeFloat32: %v", err)
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	if len(raw) != len(values)*4 {
		t.Fatalf("bytes = %d, want %d", len(raw), len(values)*4)
	}

	// Hand-written expectations for the two most endianness-sensitive values.
	if got := raw[0:4]; !bytes.Equal(got, []byte{0x00, 0x00, 0x00, 0x00}) {
		t.Fatalf("float32(0) = %x, want 00000000", got)
	}
	if got := raw[4:8]; !bytes.Equal(got, []byte{0x00, 0x00, 0x80, 0x3f}) {
		t.Fatalf("float32(1) = %x, want 0000803f", got)
	}
	if got := raw[8:12]; !bytes.Equal(got, []byte{0x00, 0x00, 0x80, 0xbf}) {
		t.Fatalf("float32(-1) = %x, want 000080bf", got)
	}

	for i, want := range values {
		got := math.Float32frombits(binary.LittleEndian.Uint32(raw[i*4 : i*4+4]))
		if got != want {
			t.Fatalf("value[%d] = %v, want %v", i, got, want)
		}
	}
}

func TestLinearToSRGBByteMatchesTheStandardCurve(t *testing.T) {
	// Reference points of the sRGB transfer function (linear -> 8-bit sRGB).
	tests := []struct {
		name   string
		linear float64
		want   byte
	}{
		{"black", 0, 0},
		{"linear toe", 0.0031308, 10},
		{"middle grey", 0.18, 118},
		{"half", 0.5, 188},
		{"white", 1, 255},
		{"negative clamps", -1, 0},
		{"above one clamps", 2, 255},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := linearToSRGBByte(tc.linear); got != tc.want {
				t.Fatalf("linearToSRGBByte(%v) = %d, want %d", tc.linear, got, tc.want)
			}
		})
	}

	// The curve must be monotonic: a preview never has reversed bands.
	prev := byte(0)
	for i := 0; i <= 1024; i++ {
		v := float64(i) / 1024
		got := linearToSRGBByte(v)
		if got < prev {
			t.Fatalf("curve is not monotonic at %v: %d after %d", v, got, prev)
		}
		prev = got
	}
}

func TestExportReportsDirectoryErrors(t *testing.T) {
	w, err := Generate(3, 128, 17)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}

	// A regular file cannot be used as the output directory.
	blocker := filepath.Join(t.TempDir(), "not-a-dir")
	if err := os.WriteFile(blocker, []byte("x"), 0o644); err != nil {
		t.Fatalf("write blocker: %v", err)
	}
	if err := w.Export(filepath.Join(blocker, "world")); err == nil {
		t.Fatal("Export silently accepted a path below a regular file")
	}
}

func TestToByteRoundsAndClamps(t *testing.T) {
	tests := []struct {
		in   float64
		want byte
	}{
		{0, 0},
		{1, 255},
		{0.5, 128},
		{-3, 0},
		{7, 255},
	}
	for _, tc := range tests {
		if got := toByte(tc.in); got != tc.want {
			t.Fatalf("toByte(%v) = %d, want %d", tc.in, got, tc.want)
		}
	}
}
