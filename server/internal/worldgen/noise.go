// Package worldgen builds the shared open-world terrain used by both the
// Unity and the Unreal client.
//
// Everything is derived from a single integer seed: heights, biome ids, vertex
// colours and the spawn point. Both engines load the exported arrays instead of
// re-running the noise, so the two clients are guaranteed to show exactly the
// same world regardless of floating point differences between C#, C++ and Go.
package worldgen

import "math"

// splitmix64 is a tiny deterministic PRNG; math/rand's algorithm is not
// guaranteed to stay stable across Go releases, this one is frozen by design.
type splitmix64 struct {
	state uint64
}

func (s *splitmix64) next() uint64 {
	s.state += 0x9E3779B97F4A7C15
	z := s.state
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
	z = (z ^ (z >> 27)) * 0x94D049BB133111EB
	return z ^ (z >> 31)
}

// perlin is a classic 2D gradient noise with a seeded permutation table.
type perlin struct {
	perm [512]int
}

func newPerlin(seed int64) *perlin {
	var p [256]int
	for i := range p {
		p[i] = i
	}

	rng := splitmix64{state: uint64(seed)}
	for i := 255; i > 0; i-- {
		j := int(rng.next() % uint64(i+1))
		p[i], p[j] = p[j], p[i]
	}

	noise := &perlin{}
	for i := 0; i < 512; i++ {
		noise.perm[i] = p[i&255]
	}
	return noise
}

func fade(t float64) float64 { return t * t * t * (t*(t*6-15) + 10) }

func lerp(a, b, t float64) float64 { return a + (b-a)*t }

func grad(hash int, x, y float64) float64 {
	switch hash & 7 {
	case 0:
		return x + y
	case 1:
		return -x + y
	case 2:
		return x - y
	case 3:
		return -x - y
	case 4:
		return x
	case 5:
		return -x
	case 6:
		return y
	default:
		return -y
	}
}

// noise2 returns roughly [-1, 1].
func (p *perlin) noise2(x, y float64) float64 {
	xf := math.Floor(x)
	yf := math.Floor(y)
	xi := int(xf) & 255
	yi := int(yf) & 255
	dx := x - xf
	dy := y - yf

	u := fade(dx)
	v := fade(dy)

	a := p.perm[xi] + yi
	b := p.perm[xi+1] + yi
	aa := p.perm[a]
	ab := p.perm[a+1]
	ba := p.perm[b]
	bb := p.perm[b+1]

	x1 := lerp(grad(aa, dx, dy), grad(ba, dx-1, dy), u)
	x2 := lerp(grad(ab, dx, dy-1), grad(bb, dx-1, dy-1), u)
	return lerp(x1, x2, v) * 1.4142135623730951
}

// fbm is fractional Brownian motion over the gradient noise.
func (p *perlin) fbm(x, y float64, octaves int, lacunarity, gain float64) float64 {
	amp, freq := 1.0, 1.0
	sum, norm := 0.0, 0.0
	for i := 0; i < octaves; i++ {
		sum += amp * p.noise2(x*freq, y*freq)
		norm += amp
		amp *= gain
		freq *= lacunarity
	}
	if norm == 0 {
		return 0
	}
	return sum / norm
}

func clamp01(v float64) float64 {
	if v < 0 {
		return 0
	}
	if v > 1 {
		return 1
	}
	return v
}

func clamp(v, lo, hi float64) float64 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func smoothstep(edge0, edge1, x float64) float64 {
	if edge0 == edge1 {
		return 0
	}
	t := clamp01((x - edge0) / (edge1 - edge0))
	return t * t * (3 - 2*t)
}

func mix(a, b, t float64) float64 { return a + (b-a)*t }
