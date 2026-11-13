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
	DefaultResolution int     = 2049 // vertices per axis, 1 m per cell
)

// L1 silhouette constants. The dune wavelength is a physical length, not a
// fraction of the map: 2048 m / 45 m == 45.5 cycles.
const (
	duneWavelength    = 45.0 // metres, crest to crest (design range 25-60 m)
	duneAmplitude     = 12.0 // metres, crest to trough on the sand sea
	duneDesertFloor   = 0.55 // relief kept on lowland outside the dry desert
	sandReposeDeg     = 33.0 // sand avalanches above this slope
	relaxIterations   = 6    // thermal-erosion passes at 1 m cells
	sharpenRadiusM    = 8.0  // unsharp-mask radius in metres
	sharpenAmount     = 0.25 // unsharp-mask strength on convex ground
	desertLowCeiling  = 120.0
	desertHighCeiling = 200.0
)

// duneWind is the prevailing wind in world XZ: sand is blown towards +X/+Z,
// so the windward face stays broad and the lee face collapses steeply.
var duneWind = [2]float64{0.62, 0.78}

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
	// Sand is the 0..1 sand-sea mask used while sculpting: it is not written
	// to disk, it only decides where dunes are added and where sand repose
	// applies.
	Sand []float32
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
	duneNoise := newPerlin(seed + 505)

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
		Sand:    make([]float32, resolution*resolution),
	}

	for iz := 0; iz < resolution; iz++ {
		z := -half + float64(iz)*cell
		for ix := 0; ix < resolution; ix++ {
			x := -half + float64(ix)*cell
			u := x / sizeMeters
			v := z / sizeMeters

			h := baseHeight(continent, mountain, detail, u, v)
			moisture := moistureAt(moistureNoise, u, v)
			temperature := temperatureAt(temperatureNoise, u, v)
			sand := duneMask(h, moisture, temperature)
			if sand > 0 {
				h += duneProfile(duneNoise, u, v, sizeMeters) * duneAmplitude * sand
			}
			idx := iz*resolution + ix
			w.Heights[idx] = float32(h)
			w.Sand[idx] = float32(sand)
		}
	}

	// L1 silhouette pass: sand cannot hold a slope past its repose angle, and
	// dune crests read better when the curvature is pushed the right way.
	// Order matters: needles are removed first, then over-steep faces creep
	// downhill, and only then are the crests sharpened.
	if os.Getenv("AI003_NO_SCULPT") == "" {
		w.medianFilterIsolatedSpikes(50)
		w.relaxSand(sandReposeDeg, relaxIterations)
		w.sharpenConvex(sharpenRadiusM, sharpenAmount)
	}

	// Heights moved during sculpting, so the reported range must be refreshed.
	w.Metadata.MinHeight = math.Inf(1)
	w.Metadata.MaxHeight = math.Inf(-1)
	for _, hv := range w.Heights {
		h := float64(hv)
		if h < w.Metadata.MinHeight {
			w.Metadata.MinHeight = h
		}
		if h > w.Metadata.MaxHeight {
			w.Metadata.MaxHeight = h
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
			moisture := moistureAt(moistureNoise, u, v)
			temperature := temperatureAt(temperatureNoise, u, v)
			variation := detail.fbm(u*26+3.3, v*26-7.1, 2, 2.0, 0.5)

			sand := duneMask(h, moisture, temperature)
			r, g, b, biome := surfaceColor(h, slope, moisture, temperature, variation, sand)
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

// baseHeight produces a continent ringed by ocean, with ridged mountains
// where the land is high. Dunes and sculpting are layered on top.
func baseHeight(continent, mountain, detail *perlin, u, v float64) float64 {
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

// moistureAt and temperatureAt are shared by the dune mask and the colour
// pass so the sand sea lines up with the painted desert.
func moistureAt(n *perlin, u, v float64) float64 {
	return 0.5 + 0.5*n.fbm(u*3.1+2.2, v*3.1-8.8, 4, 2.0, 0.5)
}

func temperatureAt(n *perlin, u, v float64) float64 {
	return clamp01(1.0 - math.Abs(v)*1.6 + 0.12*n.fbm(u*2.4+11, v*2.4+5, 3, 2.0, 0.5))
}

// duneMask is 1 on the dry sand sea and never reaches the ocean, the salt
// flats or the mountains. The 0.55 floor keeps lowland reg grained by the
// same 60 m rhythm: the measured S2 corridor sits at moisture 0.42-0.54, so
// a pure desert mask leaves it with zero dunes (verified with the probe).
func duneMask(h, moisture, temperature float64) float64 {
	desert := smoothstep(0.50, 0.30, moisture) * smoothstep(0.35, 0.60, temperature)
	return clamp01(lowlandMask(h) * (duneDesertFloor + (1-duneDesertFloor)*desert))
}

// lowlandMask is the low, dry shelf where wind-worked reg and sand both live.
// It stops well below the mountains and above the shoreline.
func lowlandMask(h float64) float64 {
	return clamp01((1.0 - smoothstep(desertLowCeiling, desertHighCeiling, h)) *
		smoothstep(2.0, 10.0, h))
}

// duneProfile is the sand-sea layer in 0..1: a 60 m wavelength ridge field
// with an asymmetric cross-section, so the windward face is broad and convex
// while the lee face drops steeply. The profile q^gamma stays continuous at
// the crest (q == 1) and the trough (q == 0) for any gamma.
//
// The raw fbm has std 0.24, so |n| rarely exceeds 0.5 and 1-|n| would sit on
// a 0.75..1 plateau. Scaling it to unit range is what turns the formula into
// an actual crest/trough sequence; without it the layer adds a uniform 6-9 m
// lift with no 60 m rhythm at all.
func duneProfile(n *perlin, u, v, sizeMeters float64) float64 {
	freq := sizeMeters / duneWavelength
	f := func(x, y float64) float64 {
		return n.fbm(x*freq+7.7, y*freq-2.3, 3, 2.0, 0.5)
	}
	val := clamp(2.0*f(u, v), -1, 1)

	// Gradient of the dune field tells us which face we are on. It vanishes
	// at a crest, which keeps the gamma blend continuous there.
	const e = 0.02
	gx := f(u+e/sizeMeters, v) - f(u-e/sizeMeters, v)
	gz := f(u, v+e/sizeMeters) - f(u, v-e/sizeMeters)
	gmag := math.Hypot(gx, gz)

	windward := 0.5
	if gmag > 1e-9 {
		rising := (gx*duneWind[0] + gz*duneWind[1]) / gmag
		windward = 0.5 * (1.0 + smoothstep(-0.25, 0.25, rising))
	}
	// windward 0.62 -> broad convex ramp; lee 1.65 -> steep slip face.
	gamma := mix(0.62, 1.65, windward)

	q := clamp01(1.0 - math.Abs(val))
	return math.Pow(q, gamma)
}

// medianFilterIsolatedSpikes removes single-vertex needles steeper than
// maxSlopeDegrees. A vertex only counts as isolated when at most two of its
// eight neighbours are over the limit, so real cliffs and dune faces survive.
func (w *World) medianFilterIsolatedSpikes(maxSlopeDegrees float64) {
	res := w.Metadata.Resolution
	if res < 3 {
		return
	}
	limit := math.Tan(maxSlopeDegrees * math.Pi / 180)
	slope := make([]float64, len(w.Heights))
	for iz := 0; iz < res; iz++ {
		for ix := 0; ix < res; ix++ {
			slope[iz*res+ix] = w.slopeAt(ix, iz)
		}
	}

	orig := append([]float32(nil), w.Heights...)
	var window [9]float64
	for iz := 1; iz < res-1; iz++ {
		for ix := 1; ix < res-1; ix++ {
			idx := iz*res + ix
			if slope[idx] <= limit {
				continue
			}
			steep := 0
			for dz := -1; dz <= 1; dz++ {
				for dx := -1; dx <= 1; dx++ {
					if dx == 0 && dz == 0 {
						continue
					}
					if slope[(iz+dz)*res+ix+dx] > limit {
						steep++
					}
				}
			}
			if steep > 2 {
				continue
			}
			k := 0
			for dz := -1; dz <= 1; dz++ {
				for dx := -1; dx <= 1; dx++ {
					window[k] = float64(orig[(iz+dz)*res+ix+dx])
					k++
				}
			}
			w.Heights[idx] = float32(median9(window))
		}
	}
}

func median9(v [9]float64) float64 {
	// Insertion sort of nine values is faster than sort.Float64s here.
	for i := 1; i < 9; i++ {
		x := v[i]
		j := i - 1
		for j >= 0 && v[j] > x {
			v[j+1] = v[j]
			j--
		}
		v[j+1] = x
	}
	return v[4]
}

// relaxSand is a small thermal-erosion pass: wherever a sandy cell stands
// more than tan(repose) above its neighbours, half of the excess creeps into
// the lowest neighbour. Moving to one target per step (instead of spreading
// over the whole neighbourhood) keeps the scheme stable; spreading the sum
// of all excesses diverges after a handful of iterations.
func (w *World) relaxSand(reposeDegrees float64, iterations int) {
	res := w.Metadata.Resolution
	cell := w.Metadata.CellSize
	if res < 3 || iterations < 1 {
		return
	}
	limit := math.Tan(reposeDegrees * math.Pi / 180)

	for it := 0; it < iterations; it++ {
		next := append([]float32(nil), w.Heights...)
		for iz := 1; iz < res-1; iz++ {
			for ix := 1; ix < res-1; ix++ {
				idx := iz*res + ix
				if w.Sand[idx] <= 0.05 {
					continue
				}
				h := float64(w.Heights[idx])
				minAllowed := math.Inf(1)
				lowest := math.Inf(1)
				lowIdx := -1
				for dz := -1; dz <= 1; dz++ {
					for dx := -1; dx <= 1; dx++ {
						if dx == 0 && dz == 0 {
							continue
						}
						dist := cell
						if dx != 0 && dz != 0 {
							dist *= math.Sqrt2
						}
						j := (iz+dz)*res + ix + dx
						hn := float64(w.Heights[j])
						if allowed := hn + limit*dist; allowed < minAllowed {
							minAllowed = allowed
						}
						if hn < lowest {
							lowest = hn
							lowIdx = j
						}
					}
				}
				if lowIdx < 0 || h <= minAllowed {
					continue
				}
				move := 0.5 * (h - minAllowed)
				next[idx] -= float32(move)
				next[lowIdx] += float32(move)
			}
		}
		w.Heights = next
	}
}

// sharpenConvex applies a light unsharp mask to convex ground only, which
// hardens dune crests without deepening the troughs.
func (w *World) sharpenConvex(radiusMetres, amount float64) {
	res := w.Metadata.Resolution
	cell := w.Metadata.CellSize
	if res < 3 || radiusMetres <= 0 {
		return
	}
	radius := maxInt(int(math.Round(radiusMetres/cell)), 1)
	blur := boxBlur(w.Heights, res, radius)
	for i := range w.Heights {
		d := float64(w.Heights[i]) - blur[i]
		if d > 0 {
			w.Heights[i] = float32(float64(w.Heights[i]) + amount*d)
		}
	}
}

// boxBlur is a separable running-sum box blur; edges clamp to the border.
func boxBlur(src []float32, res, radius int) []float64 {
	tmp := make([]float64, len(src))
	out := make([]float64, len(src))
	inv := 1.0 / float64(2*radius+1)

	for z := 0; z < res; z++ {
		base := z * res
		sum := 0.0
		for k := -radius; k <= radius; k++ {
			sum += float64(src[base+clampInt(k, 0, res-1)])
		}
		for x := 0; x < res; x++ {
			tmp[base+x] = sum * inv
			sum -= float64(src[base+clampInt(x-radius, 0, res-1)])
			sum += float64(src[base+clampInt(x+radius+1, 0, res-1)])
		}
	}

	for x := 0; x < res; x++ {
		sum := 0.0
		for k := -radius; k <= radius; k++ {
			sum += tmp[clampInt(k, 0, res-1)*res+x]
		}
		for z := 0; z < res; z++ {
			out[z*res+x] = sum * inv
			sum -= tmp[clampInt(z-radius, 0, res-1)*res+x]
			sum += tmp[clampInt(z+radius+1, 0, res-1)*res+x]
		}
	}
	return out
}

func clampInt(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
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

func surfaceColor(h, slope, moisture, temperature, variation, sand float64) (r, g, b float64, biome uint8) {
	variation = clamp(variation, -1, 1)

	if h < 0 {
		depth := clamp01(-h / 70.0)
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

	// Rock on steep slopes and high altitudes. A dune face can be steep and
	// still be sand, so the slope term is damped by the sand mask; the
	// altitude term is left alone.
	rockW := clamp01(smoothstep(0.55, 1.05, slope)*1.1*(1.0-0.8*sand) + smoothstep(210, 300, h)*0.9)

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
				depth := clamp01(-h / 60.0)
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
