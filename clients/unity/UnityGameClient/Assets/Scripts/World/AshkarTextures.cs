using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Small procedural textures. The desert look lives or dies on surface detail,
    /// and a tiling detail-normal is far cheaper than subdividing the 4 m terrain.
    /// </summary>
    public static class AshkarTextures
    {
        /// <summary>Tileable fBm height turned into a tangent-space normal map (RGB, 0..1).</summary>
        public static Texture2D DetailNormal(int size = 512, int seed = 1234, int octaves = 5)
        {
            var height = new float[size * size];
            var rng = new System.Random(seed);

            float[] xs = new float[8];
            float[] ys = new float[8];
            float[] amps = new float[8];
            float amplitude = 1f;
            float total = 0f;
            for (int o = 0; o < octaves && o < 8; o++)
            {
                xs[o] = (float)rng.NextDouble() * 100f;
                ys[o] = (float)rng.NextDouble() * 100f;
                amps[o] = amplitude;
                total += amplitude;
                amplitude *= 0.5f;
            }
            for (int i = 0; i < amps.Length; i++)
            {
                amps[i] /= total;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;
                    float v = (float)y / size;
                    float h = 0f;
                    for (int o = 0; o < octaves && o < 8; o++)
                    {
                        int freq = 4 << o;
                        h += amps[o] * Tileable(u * freq + xs[o], v * freq + ys[o]);
                    }
                    height[y * size + x] = h;
                }
            }

            // linear: true — a normal map must not be sRGB-decoded, or every
            // sampled normal collapses to one direction and the terrain goes black.
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "AshkarDetailNormal",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            var pixels = new Color32[size * size];
            float strength = 6f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float hl = height[y * size + ((x - 1 + size) % size)];
                    float hr = height[y * size + ((x + 1) % size)];
                    float hd = height[((y - 1 + size) % size) * size + x];
                    float hu = height[((y + 1) % size) * size + x];

                    Vector3 n = new Vector3((hl - hr) * strength, 1f, (hd - hu) * strength).normalized;
                    pixels[y * size + x] = new Color32(
                        (byte)((n.x * 0.5f + 0.5f) * 255f),
                        (byte)((n.z * 0.5f + 0.5f) * 255f),
                        (byte)((n.y * 0.5f + 0.5f) * 255f),
                        255);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>Periodic value noise in [0,1] using a smooth lattice.</summary>
        private static float Tileable(float x, float y)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;
            float u = xf * xf * (3f - 2f * xf);
            float v = yf * yf * (3f - 2f * yf);

            float a = Lattice(xi, yi);
            float b = Lattice(xi + 1, yi);
            float c = Lattice(xi, yi + 1);
            float d = Lattice(xi + 1, yi + 1);

            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        private static float Lattice(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
