// Command worldgen generates the shared open-world terrain data.
//
//	go run ./cmd/worldgen -out ../world
//
// It is deterministic: the same seed always produces byte-identical arrays,
// which is what lets the Unity and Unreal clients agree on the world.
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"ai003/server/internal/worldgen"
)

func main() {
	out := flag.String("out", filepath.Join("..", "world"), "output directory (world.json + arrays)")
	seed := flag.Int64("seed", worldgen.DefaultSeed, "terrain seed")
	size := flag.Float64("size", worldgen.DefaultSize, "world size in metres")
	resolution := flag.Int("res", worldgen.DefaultResolution, "vertices per axis (odd, >= 65)")
	flag.Parse()

	start := time.Now()
	world, err := worldgen.Generate(*seed, *size, *resolution)
	if err != nil {
		fmt.Fprintf(os.Stderr, "worldgen: %v\n", err)
		os.Exit(1)
	}
	if err := world.Export(*out); err != nil {
		fmt.Fprintf(os.Stderr, "worldgen: export: %v\n", err)
		os.Exit(1)
	}

	meta := world.Metadata
	counts := map[string]int{}
	names := map[uint8]string{
		worldgen.BiomeOcean:  "ocean",
		worldgen.BiomeBeach:  "beach",
		worldgen.BiomeGrass:  "grass",
		worldgen.BiomeDesert: "desert",
		worldgen.BiomeRock:   "rock",
		worldgen.BiomeSnow:   "snow",
	}
	for _, b := range world.Biomes {
		counts[names[b]]++
	}
	total := float64(len(world.Biomes))

	fmt.Printf("world      : %s seed=%d\n", meta.Name, meta.Seed)
	fmt.Printf("size       : %.0f m x %.0f m, %d x %d vertices (%.1f m/cell)\n",
		meta.SizeMeters, meta.SizeMeters, meta.Resolution, meta.Resolution, meta.CellSize)
	fmt.Printf("height     : %.1f m .. %.1f m (sea level %.0f)\n", meta.MinHeight, meta.MaxHeight, meta.SeaLevel)
	fmt.Printf("spawn      : x=%.1f y=%.1f z=%.1f\n", meta.Spawn.X, meta.Spawn.Y, meta.Spawn.Z)
	for _, name := range []string{"ocean", "beach", "grass", "desert", "rock", "snow"} {
		fmt.Printf("  %-7s: %5.1f%%\n", name, float64(counts[name])/total*100)
	}
	fmt.Printf("output     : %s\n", *out)
	fmt.Printf("elapsed    : %s\n", time.Since(start).Round(time.Millisecond))
}
