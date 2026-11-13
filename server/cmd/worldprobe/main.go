// Command worldprobe measures the L1 silhouette metrics on an exported world:
// the S2 sightline ridge count, slope percentiles, spike counts and the
// second-difference noise floor.
//
//	go run ./cmd/worldprobe -dir ../world -len 800
//
// The S2 site/view direction is reproduced from the Unity editor tool
// (AshkarStill.FindViewpoint) so the numbers match what the still renderer
// picks. Everything else is plain heightmap sampling.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sort"
)

type metadata struct {
	Resolution int     `json:"resolution"`
	CellSize   float64 `json:"cellSize"`
	SizeMeters float64 `json:"sizeMeters"`
	SeaLevel   float64 `json:"seaLevel"`
}

type field struct {
	meta    metadata
	half    float64
	heights []float32
	biomes  []byte
}

func loadField(dir string) (*field, error) {
	raw, err := os.ReadFile(filepath.Join(dir, "world.json"))
	if err != nil {
		return nil, fmt.Errorf("read world.json: %w", err)
	}
	var meta metadata
	if err := json.Unmarshal(raw, &meta); err != nil {
		return nil, fmt.Errorf("parse world.json: %w", err)
	}
	if meta.Resolution < 2 {
		return nil, fmt.Errorf("bad resolution %d", meta.Resolution)
	}

	buf, err := os.ReadFile(filepath.Join(dir, "heightmap.f32"))
	if err != nil {
		return nil, fmt.Errorf("read heightmap: %w", err)
	}
	want := meta.Resolution * meta.Resolution * 4
	if len(buf) != want {
		return nil, fmt.Errorf("heightmap size %d, want %d", len(buf), want)
	}
	h := make([]float32, meta.Resolution*meta.Resolution)
	for i := range h {
		bits := uint32(buf[i*4]) | uint32(buf[i*4+1])<<8 | uint32(buf[i*4+2])<<16 | uint32(buf[i*4+3])<<24
		h[i] = math.Float32frombits(bits)
	}

	var biomes []byte
	if b, err := os.ReadFile(filepath.Join(dir, "biomes.r8")); err == nil && len(b) == meta.Resolution*meta.Resolution {
		biomes = b
	}

	return &field{meta: meta, half: meta.SizeMeters / 2, heights: h, biomes: biomes}, nil
}

func (f *field) heightAt(x, z float64) float64 {
	res := f.meta.Resolution
	cell := f.meta.CellSize
	fx := (x + f.half) / cell
	fz := (z + f.half) / cell
	if fx < 0 || fz < 0 || fx > float64(res-1) || fz > float64(res-1) {
		return f.meta.SeaLevel - 50
	}
	x0 := int(fx)
	z0 := int(fz)
	x1 := minInt(x0+1, res-1)
	z1 := minInt(z0+1, res-1)
	tx := fx - float64(x0)
	tz := fz - float64(z0)
	h00 := float64(f.heights[z0*res+x0])
	h10 := float64(f.heights[z0*res+x1])
	h01 := float64(f.heights[z1*res+x0])
	h11 := float64(f.heights[z1*res+x1])
	return mix(mix(h00, h10, tx), mix(h01, h11, tx), tz)
}

func (f *field) biomeAt(x, z float64) byte {
	if f.biomes == nil {
		return 0
	}
	res := f.meta.Resolution
	ix := clampInt(int(math.Round((x+f.half)/f.meta.CellSize)), 0, res-1)
	iz := clampInt(int(math.Round((z+f.half)/f.meta.CellSize)), 0, res-1)
	return f.biomes[iz*res+ix]
}

// findViewpoint is a direct port of AshkarStill.FindViewpoint.
func (f *field) findViewpoint() (site, dir [2]float64, ok bool) {
	best := math.Inf(-1)
	half := f.half
	for z := -half + 200; z < half-200; z += 12 {
		for x := -half + 200; x < half-200; x += 12 {
			h := f.heightAt(x, z)
			if h < 40 || h > 130 {
				continue
			}
			ix := int(math.Round((x + half) / f.meta.CellSize))
			iz := int(math.Round((z + half) / f.meta.CellSize))
			ix = clampInt(ix, 0, f.meta.Resolution-1)
			iz = clampInt(iz, 0, f.meta.Resolution-1)
			if f.biomes != nil {
				b := f.biomes[iz*f.meta.Resolution+ix]
				if b != 2 && b != 3 {
					continue
				}
			}

			lowest := math.Inf(1)
			for k := 0; k < 8; k++ {
				ak := float64(k) * math.Pi / 4
				lowest = math.Min(lowest, f.heightAt(x+math.Cos(ak)*55, z+math.Sin(ak)*55))
			}
			prominence := h - lowest
			if prominence < 5 {
				continue
			}

			for b := 0; b < 8; b++ {
				a := float64(b) * math.Pi / 4
				dx := math.Cos(a)
				dz := math.Sin(a)
				far := 0.0
				n := 0
				for d := 180.0; d <= 620; d += 70 {
					far += f.heightAt(x+dx*d, z+dz*d)
					n++
				}
				far /= float64(n)
				if f.heightAt(x+dx*420, z+dz*420) < 8 || f.heightAt(x+dx*900, z+dz*900) < 4 {
					continue
				}
				drop := h - far
				if drop < 4 {
					continue
				}
				score := drop + prominence*1.3
				if score > best {
					best = score
					site = [2]float64{x, z}
					dir = [2]float64{dx, dz}
					ok = true
				}
			}
		}
	}
	return site, dir, ok
}

type peak struct {
	s    float64 // metres along the sightline
	h    float64
	prom float64
}

func main() {
	dir := flag.String("dir", "../world", "world directory (world.json + heightmap.f32)")
	length := flag.Float64("len", 800, "sightline length in metres")
	step := flag.Float64("step", 1.0, "profile sample spacing in metres")
	siteX := flag.Float64("x", math.NaN(), "override site x")
	siteZ := flag.Float64("z", math.NaN(), "override site z")
	dirX := flag.Float64("dx", math.NaN(), "override direction x")
	dirZ := flag.Float64("dz", math.NaN(), "override direction z")
	windX := flag.Float64("windx", 0.62, "wind direction x")
	windZ := flag.Float64("windz", 0.78, "wind direction z")
	profEvery := flag.Float64("prof", 0, "print the height profile every N metres (0 = off)")
	flag.Parse()

	f, err := loadField(*dir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "worldprobe: %v\n", err)
		os.Exit(1)
	}

	var site, d [2]float64
	var ok bool
	site, d, ok = f.findViewpoint()
	if !math.IsNaN(*siteX) {
		site = [2]float64{*siteX, *siteZ}
		ok = true
	}
	if !math.IsNaN(*dirX) {
		d = [2]float64{*dirX, *dirZ}
		ok = true
	}
	if !ok {
		fmt.Fprintln(os.Stderr, "worldprobe: FindViewpoint found no site")
		os.Exit(1)
	}
	// Normalise the direction.
	dl := math.Hypot(d[0], d[1])
	d[0] /= dl
	d[1] /= dl

	n := int(*length / *step)
	prof := make([]float64, n+1)
	for i := 0; i <= n; i++ {
		s := float64(i) * *step
		prof[i] = f.heightAt(site[0]+d[0]*s, site[1]+d[1]*s)
	}

	wx, wz := *windX, *windZ
	if wl := math.Hypot(wx, wz); wl > 0 {
		wx /= wl
		wz /= wl
	}

	peaks := detectPeaks(prof, *step, 1.0)
	slopes := make([]float64, 0, n+1)
	windSlopes := []float64{}
	leeSlopes := []float64{}
	for i := 1; i < n; i++ {
		dh := (prof[i+1] - prof[i-1]) / (2 * *step)
		deg := math.Atan(math.Abs(dh)) * 180 / math.Pi
		slopes = append(slopes, deg)
		// Classify the face by whether the ground rises toward the wind.
		rising := dh * (d[0]*wx + d[1]*wz)
		if rising > 0 {
			windSlopes = append(windSlopes, deg)
		} else {
			leeSlopes = append(leeSlopes, deg)
		}
	}
	sort.Float64s(slopes)

	absD2 := make([]float64, 0, n-1)
	type d2at struct {
		s, v float64
	}
	var d2worst []d2at
	bigD2, bigD2Crest := 0, 0
	for i := 1; i < n; i++ {
		d := math.Abs(prof[i+1] - 2*prof[i] + prof[i-1])
		absD2 = append(absD2, d)
		d2worst = append(d2worst, d2at{s: float64(i) * *step, v: d})
		if d > 0.5 {
			bigD2++
			if prof[i] >= prof[i-1] && prof[i] >= prof[i+1] {
				bigD2Crest++
			}
		}
	}
	sort.Float64s(absD2)
	sort.Slice(d2worst, func(a, b int) bool { return d2worst[a].v > d2worst[b].v })
	if len(d2worst) > 5 {
		d2worst = d2worst[:5]
	}

	spacings := []float64{}
	for i := 1; i < len(peaks); i++ {
		spacings = append(spacings, peaks[i].s-peaks[i-1].s)
	}
	meanGap := 0.0
	if len(spacings) > 0 {
		for _, v := range spacings {
			meanGap += v
		}
		meanGap /= float64(len(spacings))
	}

	// Ridges per 200 m window.
	windows := make([]int, int(math.Ceil(*length/200)))
	for _, p := range peaks {
		w := int(p.s / 200)
		if w >= len(windows) {
			w = len(windows) - 1
		}
		windows[w]++
	}

	count := func(sl []float64, deg float64) int {
		c := 0
		for _, v := range sl {
			if v > deg {
				c++
			}
		}
		return c
	}

	meanFloat := func(v []float64) float64 {
		if len(v) == 0 {
			return math.NaN()
		}
		s := 0.0
		for _, x := range v {
			s += x
		}
		return s / float64(len(v))
	}

	fmt.Printf("world      : %s res=%d cell=%.2fm size=%.0fm\n", *dir, f.meta.Resolution, f.meta.CellSize, f.meta.SizeMeters)
	fmt.Printf("site       : x=%.1f z=%.1f h=%.1f\n", site[0], site[1], f.heightAt(site[0], site[1]))
	fmt.Printf("dir        : x=%.3f z=%.3f (yaw=%.1f deg)\n", d[0], d[1], math.Atan2(d[0], d[1])*180/math.Pi)
	fmt.Printf("corridor   : %.0f m, %d samples at %.1f m\n", *length, n+1, *step)
	if *profEvery > 0 {
		fmt.Print("profile    :")
		every := int(*profEvery / *step)
		if every < 1 {
			every = 1
		}
		for i := 0; i <= n; i += every {
			fmt.Printf(" %.0f:%.1f", float64(i)**step, prof[i])
		}
		fmt.Println()
	}
	fmt.Printf("ridges     : count=%d prom>=1.0m, meanGap=%.1f m, per200=%v\n", len(peaks), meanGap, windows)
	for _, p := range peaks {
		fmt.Printf("  ridge @ %6.1f m  h=%7.2f  prom=%5.2f\n", p.s, p.h, p.prom)
	}
	fmt.Printf("slope deg  : p50=%.1f p90=%.1f p99=%.1f max=%.1f\n",
		pct(slopes, 0.50), pct(slopes, 0.90), pct(slopes, 0.99), maxFloat(slopes))
	fmt.Printf("slope >50  : %.1f%%  >60: %.1f%%  >75: %.1f%%  >80: %.1f%%\n",
		100*float64(count(slopes, 50))/float64(len(slopes)),
		100*float64(count(slopes, 60))/float64(len(slopes)),
		100*float64(count(slopes, 75))/float64(len(slopes)),
		100*float64(count(slopes, 80))/float64(len(slopes)))
	fmt.Printf("d2 abs     : mean=%.4f max=%.4f (m, at %.1fm spacing)\n", meanFloat(absD2), maxFloat(absD2), *step)
	fmt.Printf("d2 >0.5m   : %d samples, %d of them are local maxima\n", bigD2, bigD2Crest)
	for _, dp := range d2worst {
		fmt.Printf("  d2 peak @ %6.1f m  |d2|=%.3f  h=%.2f\n", dp.s, dp.v, f.heightAt(site[0]+d[0]*dp.s, site[1]+d[1]*dp.s))
	}
	fmt.Printf("asym       : windward mean slope=%.1f deg, lee mean slope=%.1f deg\n",
		meanFloat(windSlopes), meanFloat(leeSlopes))

	if f.biomes != nil {
		names := map[byte]string{0: "ocean", 1: "beach", 2: "grass", 3: "desert", 4: "rock", 5: "snow"}
		runStart := 0.0
		var last byte
		for i := 0; i <= n; i++ {
			s := float64(i) * *step
			bi := f.biomeAt(site[0]+d[0]*s, site[1]+d[1]*s)
			if i == 0 {
				last = bi
				continue
			}
			if bi != last || i == n {
				fmt.Printf("  biome %-6s from %5.0f m to %5.0f m\n", names[last], runStart, s)
				runStart = s
				last = bi
			}
		}
	}
}

func detectPeaks(h []float64, step, minProm float64) []peak {
	var peaks []peak
	for i := 1; i < len(h)-1; i++ {
		if !(h[i] > h[i-1] && h[i] >= h[i+1]) {
			continue
		}
		// Prominence: highest of the two intervening saddles.
		leftMin := h[i]
		for j := i - 1; j >= 0; j-- {
			if h[j] < leftMin {
				leftMin = h[j]
			}
			if h[j] > h[i] {
				break
			}
		}
		rightMin := h[i]
		for j := i + 1; j < len(h); j++ {
			if h[j] < rightMin {
				rightMin = h[j]
			}
			if h[j] > h[i] {
				break
			}
		}
		prom := h[i] - math.Max(leftMin, rightMin)
		if prom >= minProm {
			peaks = append(peaks, peak{s: float64(i) * step, h: h[i], prom: prom})
		}
	}
	return peaks
}

func pct(sorted []float64, q float64) float64 {
	if len(sorted) == 0 {
		return math.NaN()
	}
	i := int(q * float64(len(sorted)-1))
	return sorted[i]
}

func maxFloat(v []float64) float64 {
	if len(v) == 0 {
		return math.NaN()
	}
	m := v[0]
	for _, x := range v {
		if x > m {
			m = x
		}
	}
	return m
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
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

func mix(a, b, t float64) float64 { return a + (b-a)*t }
