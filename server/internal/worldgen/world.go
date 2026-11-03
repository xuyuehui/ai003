package worldgen

import (
	"encoding/json"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"math"
	"os"
	"path/filepath"
)

// Biome ids written to the .r8 file. Clients use them for debug/HUD only;
// rendering uses the baked vertex colours so both engines match exactly.
const (
	BiomeOcean  uint8 = 0
	BiomeBeach  uint8 = 1
	BiomeGrass  uint8 = 2
	BiomeDesert uint8 = 3
	BiomeRock   uint8 = 4
	BiomeSnow   uint8 = 5
)

// Defaults for the first milestone world.
const (
	DefaultSeed       int64   = 20261103
	DefaultSize       float64 = 2048 // metres, square
	DefaultResolution int     = 513  // vertices per axis, 4 m per cell
)

// Vec3 is a plain 3D point in world metres (Y up).
type Vec3 struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

// Metadata is serialised to world.json.
type Metadata struct {
	Version     int               `json:"version"`
	Name        string            `json:"name"`
	Seed        int64             `json:"seed"`
	SizeMeters  float64           `json:"sizeMeters"`
	Resolution  int               `json:"resolution"`
	CellSize    float64           `json:"cellSize"`
	SeaLevel    float64           `json:"seaLevel"`
	MinHeight   float64           `json:"minHeight"`
	MaxHeight   float64           `json:"maxHeight"`
	Spawn       Vec3              `json:"spawn"`
	Files       map[string]string `json:"files"`
	GeneratedBy string            `json:"generatedBy"`
}

// World holds the generated arrays in row-major order: index = z*Resolution+x.
type World struct {
	Metadata Metadata
	Heights  []float32
	Colors   []byte // RGBA, linear colour space, 4 bytes per vertex
	Biomes   []byte // 1 byte per vertex
}

// HeightAt returns the bilinearly interpolated height in metres at world XZ.
func (w *World) HeightAt(x, z float64) float64 {
	res := w.Metadata.Resolution
	cell := w.Metadata.CellSize
	half := w.Metadata.SizeMeters / 2

	fx := (x + half) / cell
	fz := (z + half) / cell
	if fx < 0 || fz < 0 || fx > float64(res-1) || fz > float64(res-1) {
		return w.Metadata.SeaLevel - 50
	}

	x0 := int(fx)
	z0 := int(fz)
	x1 := minInt(x0+1, res-1)
	z1 := minInt(z0+1, res-1)
	tx := fx - float64(x0)
	tz := fz - float64(z0)

	h00 := float64(w.Heights[z0*res+x0])
	h10 := float64(w.Heights[z0*res+x1])
	h01 := float64(w.Heights[z1*res+x0])
	h11 := float64(w.Heights[z1*res+x1])

	return mix(mix(h00, h10, tx), mix(h01, h11, tx), tz)
}

// Generate builds a deterministic world.
func Generate(seed int64, sizeMeters float64, resolution int) (*World, error) {
	if sizeMeters <= 0 {
		return nil, fmt.Errorf("size must be positive, got %v", sizeMeters)
	}
	if resolution < 2 {
		return nil, fmt.Errorf("resolution must be >= 2, got %d", resolution)
	}

	cell := sizeMeters / float64(resolution-1)
	half := sizeMeters / 2

	continent := newPerlin(seed)
	mountain := newPerlin(seed + 101)
	detail := newPerlin(seed + 202)
	moistureNoise := newPerlin(seed + 303)
	temperatureNoise := newPerlin(seed + 404)

	w := &World{
		Metadata: Metadata{
			Version:     1,
			Name:        "ai003-open-world",
			Seed:        seed,
			SizeMeters:  sizeMeters,
			Resolution:  resolution,
			CellSize:    cell,
			SeaLevel:    0,
			MinHeight:   math.Inf(1),
			MaxHeight:   math.Inf(-1),
			GeneratedBy: "server/cmd/worldgen",
			Files: map[string]string{
				"heightmap": "heightmap.f32",
				"colors":    "colors.rgba8",
				"biomes":    "biomes.r8",
			},
		},
		Heights: make([]float32, resolution*resolution),
		Colors:  make([]byte, resolution*resolution*4),
		Biomes:  make([]byte, resolution*resolution),
	}

	for iz := 0; iz < resolution; iz++ {
		z := -half + float64(iz)*cell
		for ix := 0; ix < resolution; ix++ {
			x := -half + float64(ix)*cell
			u := x / sizeMeters
			v := z / sizeMeters

			h := terrainHeight(continent, mountain, detail, u, v)
			idx := iz*resolution + ix
			w.Heights[idx] = float32(h)
			if h < w.Metadata.MinHeight {
				w.Metadata.MinHeight = h
			}
			if h > w.Metadata.MaxHeight {
				w.Metadata.MaxHeight = h
			}
		}
	}

	// Second pass: colours need the slope, which needs the finished heightmap.
	for iz := 0; iz < resolution; iz++ {
		z := -half + float64(iz)*cell
		for ix := 0; ix < resolution; ix++ {
			x := -half + float64(ix)*cell
			u := x / sizeMeters
			v := z / sizeMeters
			idx := iz*resolution + ix

			h := float64(w.Heights[idx])
			slope := w.slopeAt(ix, iz)
			moisture := 0.5 + 0.5*moistureNoise.fbm(u*3.1+2.2, v*3.1-8.8, 4, 2.0, 0.5)
			temperature := clamp01(1.0-math.Abs(v)*1.6+0.12*temperatureNoise.fbm(u*2.4+11, v*2.4+5, 3, 2.0, 0.5))
			variation := detail.fbm(u*26+3.3, v*26-7.1, 2, 2.0, 0.5)

			r, g, b, biome := surfaceColor(h, slope, moisture, temperature, variation)
			w.Colors[idx*4+0] = toByte(r)
			w.Colors[idx*4+1] = toByte(g)
			w.Colors[idx*4+2] = toByte(b)
			w.Colors[idx*4+3] = 255
			w.Biomes[idx] = biome
		}
	}

	w.Metadata.Spawn = w.findSpawn()
	return w, nil
}

// terrainHeight produces a continent ringed by ocean, with ridged mountains
// where the land is high.
func terrainHeight(continent, mountain, detail *perlin, u, v float64) float64 {
	// Distance from the centre, scaled so the corners are farthest away.
	d := math.Hypot(u*1.02, v) * 2.0
	falloff := smoothstep(0.70, 1.12, d)

	base := 0.60 + 0.50*continent.fbm(u*2.1+13.7, v*2.1-4.1, 5, 2.0, 0.5)
	land := base * (1.0 - 0.80*falloff)

	// Sea floor slopes down towards the map edge.
	h := -85 + land*205

	// Ridged mountains only appear where the continent is high.
	mask := clamp01((land - 0.62) / 0.38)
	if mask > 0 {
		ridge := 1.0 - math.Abs(mountain.fbm(u*5.4+77.3, v*5.4-19.2, 4, 2.05, 0.5))
		ridge = math.Pow(clamp01(ridge), 2.1)
		h += mask * mask * ridge * 460
	}

	// Rolling detail, faded out over the ocean.
	h += detail.fbm(u*9.3+31.1, v*9.3-17.7, 3, 2.1, 0.45) * 14.0 * (1.0 - falloff)

	// A soft beach shelf so the coastline is not a cliff.
	if h > -6 && h < 14 {
		h *= 0.92
	}
	return h
}

func (w *World) slopeAt(ix, iz int) float64 {
	res := w.Metadata.Resolution
	cell := w.Metadata.CellSize

	x0 := maxInt(ix-1, 0)
	x1 := minInt(ix+1, res-1)
	z0 := maxInt(iz-1, 0)
	z1 := minInt(iz+1, res-1)

	dx := (float64(w.Heights[iz*res+x1]) - float64(w.Heights[iz*res+x0])) / (float64(x1-x0) * cell)
	dz := (float64(w.Heights[z1*res+ix]) - float64(w.Heights[z0*res+ix])) / (float64(z1-z0) * cell)
	return math.Hypot(dx, dz) // rise over run; 1.0 == 45 degrees
}

// Linear-space palette. sRGB values were converted with pow(c, 2.2) so both
// engines can feed the bytes straight into a shader without colour management.
var (
	colDeep   = [3]float64{0.010, 0.055, 0.090}
	colSeabed = [3]float64{0.070, 0.150, 0.135}
	colSand   = [3]float64{0.520, 0.440, 0.270}
	colGrass  = [3]float64{0.095, 0.235, 0.075}
	colGrassD = [3]float64{0.045, 0.150, 0.045}
	colDesert = [3]float64{0.610, 0.430, 0.190}
	colRock   = [3]float64{0.160, 0.150, 0.135}
	colSnow   = [3]float64{0.820, 0.860, 0.900}
)

func surfaceColor(h, slope, moisture, temperature, variation float64) (r, g, b float64, biome uint8) {
	variation = clamp(variation, -1, 1)

	if h < 0 {
		depth := clamp01(-h/70.0)
		r = mix(colSeabed[0], colDeep[0], depth)
		g = mix(colSeabed[1], colDeep[1], depth)
		b = mix(colSeabed[2], colDeep[2], depth)
		shade := 1.0 + variation*0.05
		return clamp01(r * shade), clamp01(g * shade), clamp01(b * shade), BiomeOcean
	}

	// Beach blends from sand at the waterline into the inland biome.
	sandW := 1.0 - smoothstep(1.5, 9.0, h)

	grass := [3]float64{
		mix(colGrassD[0], colGrass[0], clamp01(moisture*1.15)),
		mix(colGrassD[1], colGrass[1], clamp01(moisture*1.15)),
		mix(colGrassD[2], colGrass[2], clamp01(moisture*1.15)),
	}

	// Deserts need dry air and warmth.
	desertW := smoothstep(0.50, 0.30, moisture) * smoothstep(0.35, 0.60, temperature) * (1.0 - smoothstep(150, 240, h))

	// Rock on steep slopes and high altitudes.
	rockW := clamp01(smoothstep(0.55, 1.05, slope)*1.1 + smoothstep(210, 300, h)*0.9)

	// Snow on the highest peaks and on cold northern/southern edges.
	cold := smoothstep(0.34, 0.12, temperature)
	snowW := clamp01(smoothstep(215, 300, h) + cold*smoothstep(80, 180, h)*0.85)

	r = mix(grass[0], colDesert[0], desertW)
	g = mix(grass[1], colDesert[1], desertW)
	b = mix(grass[2], colDesert[2], desertW)

	r = mix(r, colRock[0], rockW)
	g = mix(g, colRock[1], rockW)
	b = mix(b, colRock[2], rockW)

	r = mix(r, colSnow[0], snowW)
	g = mix(g, colSnow[1], snowW)
	b = mix(b, colSnow[2], snowW)

	r = mix(r, colSand[0], sandW)
	g = mix(g, colSand[1], sandW)
	b = mix(b, colSand[2], sandW)

	shade := 1.0 + variation*0.07
	r = clamp01(r * shade)
	g = clamp01(g * shade)
	b = clamp01(b * shade)

	switch {
	case sandW > 0.6:
		biome = BiomeBeach
	case snowW > 0.45:
		biome = BiomeSnow
	case rockW > 0.55:
		biome = BiomeRock
	case desertW > 0.5:
		biome = BiomeDesert
	default:
		biome = BiomeGrass
	}
	return r, g, b, biome
}

func (w *World) findSpawn() Vec3 {
	res := w.Metadata.Resolution
	half := w.Metadata.SizeMeters / 2
	cell := w.Metadata.CellSize

	best := Vec3{X: 0, Y: 0, Z: 0}
	bestScore := math.Inf(1)

	for iz := res / 4; iz < res*3/4; iz++ {
		for ix := res / 4; ix < res*3/4; ix++ {
			h := float64(w.Heights[iz*res+ix])
			if h < 6 || h > 90 {
				continue
			}
			slope := w.slopeAt(ix, iz)
			if slope > 0.28 {
				continue
			}
			if w.Biomes[iz*res+ix] == BiomeRock || w.Biomes[iz*res+ix] == BiomeSnow {
				continue
			}

			x := -half + float64(ix)*cell
			z := -half + float64(iz)*cell

			// Require open, dry land around the candidate so the player does not
			// start on a cliff edge or a tiny island.
			open := true
			for k := 0; k < 8; k++ {
				angle := float64(k) * math.Pi / 4
				if w.HeightAt(x+math.Cos(angle)*30, z+math.Sin(angle)*30) < 4 {
					open = false
					break
				}
			}
			if !open {
				continue
			}

			// Prefer flat, low ground close to the centre of the map.
			score := slope*40 + math.Hypot(x, z)/half*6
			if score < bestScore {
				bestScore = score
				best = Vec3{X: x, Y: h, Z: z}
			}
		}
	}

	if math.IsInf(bestScore, 1) {
		best = Vec3{X: 0, Y: float64(w.Heights[(res/2)*res+res/2]), Z: 0}
	}
	return best
}

// Export writes world.json, the three binary arrays and a preview PNG.
func (w *World) Export(dir string) error {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}

	if err := writeFloat32(filepath.Join(dir, w.Metadata.Files["heightmap"]), w.Heights); err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, w.Metadata.Files["colors"]), w.Colors, 0o644); err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, w.Metadata.Files["biomes"]), w.Biomes, 0o644); err != nil {
		return err
	}

	meta, err := json.MarshalIndent(w.Metadata, "", "  ")
	if err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, "world.json"), append(meta, '\n'), 0o644); err != nil {
		return err
	}

	return w.writePreview(filepath.Join(dir, "preview.png"))
}

func writeFloat32(path string, values []float32) error {
	buf := make([]byte, len(values)*4)
	for i, v := range values {
		bits := math.Float32bits(v)
		buf[i*4+0] = byte(bits)
		buf[i*4+1] = byte(bits >> 8)
		buf[i*4+2] = byte(bits >> 16)
		buf[i*4+3] = byte(bits >> 24)
	}
	return os.WriteFile(path, buf, 0o644)
}

// writePreview renders a hill-shaded top-down image for docs and quick review.
func (w *World) writePreview(path string) error {
	res := w.Metadata.Resolution
	cell := w.Metadata.CellSize
	img := image.NewRGBA(image.Rect(0, 0, res, res))

	light := [3]float64{-0.45, 0.78, -0.44}
	ln := math.Sqrt(light[0]*light[0] + light[1]*light[1] + light[2]*light[2])
	light[0] /= ln
	light[1] /= ln
	light[2] /= ln

	for iz := 0; iz < res; iz++ {
		for ix := 0; ix < res; ix++ {
			idx := iz*res + ix
			h := float64(w.Heights[idx])

			x0 := maxInt(ix-1, 0)
			x1 := minInt(ix+1, res-1)
			z0 := maxInt(iz-1, 0)
			z1 := minInt(iz+1, res-1)
			dx := (float64(w.Heights[iz*res+x1]) - float64(w.Heights[iz*res+x0])) / (float64(x1-x0) * cell)
			dz := (float64(w.Heights[z1*res+ix]) - float64(w.Heights[z0*res+ix])) / (float64(z1-z0) * cell)
			nx, ny, nz := -dx, 1.0, -dz
			nl := math.Sqrt(nx*nx + ny*ny + nz*nz)
			nx /= nl
			ny /= nl
			nz /= nl

			shade := clamp01(0.35 + 0.75*(nx*light[0]+ny*light[1]+nz*light[2]))

			r := float64(w.Colors[idx*4+0]) / 255
			g := float64(w.Colors[idx*4+1]) / 255
			b := float64(w.Colors[idx*4+2]) / 255

			if h < w.Metadata.SeaLevel {
				depth := clamp01(-h/60.0)
				shade = clamp01(0.55 + 0.25*(1-depth))
			}

			img.SetRGBA(ix, iz, color.RGBA{
				R: linearToSRGBByte(r * shade),
				G: linearToSRGBByte(g * shade),
				B: linearToSRGBByte(b * shade),
				A: 255,
			})
		}
	}

	f, err := os.Create(path)
	if err != nil {
		return err
	}
	defer f.Close()
	return png.Encode(f, img)
}

func toByte(v float64) byte {
	return byte(math.Round(clamp01(v) * 255))
}

func linearToSRGBByte(v float64) byte {
	v = clamp01(v)
	if v <= 0.0031308 {
		return byte(math.Round(v * 12.92 * 255))
	}
	return byte(math.Round((1.055*math.Pow(v, 1/2.4) - 0.055) * 255))
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
