using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// A world "skin": biome palette + material knobs. The shared world files stay
    /// untouched (UE5 keeps reading the baked colours); a theme re-colours the same
    /// height/biome data on the Unity side only.
    ///
    /// Biome ids are the server's (server/internal/worldgen/world.go):
    /// 0 ocean, 1 beach, 2 grass, 3 desert, 4 rock, 5 snow.
    /// Grass carries no biome split in the data, so temperate forest and steppe are
    /// separated here by the moisture recovered from colors.rgba8 (ResolveMoisture),
    /// first averaged over 32 m blocks — vertex level is noise (sd 8.3% per vertex).
    ///
    /// Colours are authored in sRGB and converted to the linear values the meshes
    /// need (the baked colours are already linear, see docs/specs/unity-client.md).
    /// </summary>
    [CreateAssetMenu(fileName = "WorldTheme", menuName = "AI003/World Theme")]
    public sealed class WorldTheme : ScriptableObject
    {
        [Header("Ocean / shore (sRGB)")]
        public Color saltSea = Hex("EEE8DC");
        public Color saltShore = Hex("E3DAC6");
        public Color saltDeep = Hex("C6BFB0");
        /// <summary>Sea floor under the water plane; pale here and the sea reads as a salt flat.</summary>
        public Color seaFloor = Hex("30434A");
        public Color seaFloorDeep = Hex("12242A");

        [Header("Desert (sRGB)")]
        public Color dune = Hex("D3A974");
        public Color duneShadow = Hex("9C744C");
        public Color rust = Hex("764833");

        [Header("Temperate forest (sRGB)")]
        public Color forestLit = Hex("8AA06B");
        public Color forest = Hex("5F7A4C");
        public Color forestShadow = Hex("33452F");
        public Color litter = Hex("7C6344");
        public Color bark = Hex("4A3B2E");

        [Header("Steppe (sRGB)")]
        public Color steppeLit = Hex("BEB083");
        public Color steppe = Hex("9A8F68");
        public Color steppeShadow = Hex("5F5940");
        public Color ochre = Hex("A89376");
        public Color sparseBrown = Hex("6B5A45");

        [Header("Snow / high mountain (sRGB)")]
        public Color snowLit = Hex("F5F8FA");
        public Color snow = Hex("DDE7ED");
        public Color snowShadow = Hex("A7BCD1");
        public Color ice = Hex("9DB4C6");
        public Color rockLit = Hex("8C8273");
        public Color rock = Hex("6A6156");
        public Color rockShadow = Hex("3B362E");
        public Color highSnow = Hex("E9EFF3");

        [Header("Material")]
        [Range(0f, 1f)] public float smoothness = 0.18f;
        [Range(0f, 2f)] public float rippleStrength = 0.35f;
        [Range(0f, 1f)] public float speckle = 0.25f;

        /// <summary>Snow line measured by the world audit; rock above it gets a cap.</summary>
        public const float SnowLineMeters = 254.7f;

        /// <summary>Moisture is only stable at this block size (see docs §5.1).</summary>
        public const int MoistureBlockCells = 32;

        public const float ForestMoisture = 0.72f;
        public const float SteppeMoisture = 0.62f;
        public const float NoTreeSlope = 0.70f;

        /// <summary>Ashkar / 白喉盆地 — the palette from docs/worldview/03-art-direction.md.</summary>
        public static WorldTheme CreateAshkar()
        {
            var theme = CreateInstance<WorldTheme>();
            theme.name = "WorldTheme_Ashkar";
            return theme;
        }

        /// <summary>One colour per heightmap vertex, box-blurred so biome seams do not stair-step.</summary>
        public Color32[] BuildColorGrid(WorldData data)
        {
            int res = data.Resolution;
            float[] blocks = BuildMoistureBlocks(data);

            var grid = new Color[res * res];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    grid[z * res + x] = SampleRaw(data, x, z, SampleBlockMoisture(blocks, res, x, z));
                }
            }

            var blurred = new Color[res * res];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    Color sum = Color.black;
                    int n = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = Mathf.Clamp(x + dx, 0, res - 1);
                            int sz = Mathf.Clamp(z + dz, 0, res - 1);
                            sum += grid[sz * res + sx];
                            n++;
                        }
                    }
                    blurred[z * res + x] = sum / n;
                }
            }

            var result = new Color32[res * res];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = ToLinearByte(blurred[i]);
            }
            return result;
        }

        // ------------------------------------------------------------ moisture ---

        /// <summary>
        /// Recovers moisture from a baked grass colour. worldgen draws pure grass as
        /// color = shade * mix(colGrassD, colGrass, moisture * 1.15), so shade and
        /// moisture are solvable by weighted least squares. Returns false for vertices
        /// blended with sand/rock/snow, where the model does not hold (78% of vertices).
        /// </summary>
        private static bool ResolveMoisture(Color32 colour, out float moisture)
        {
            moisture = 0f;
            double[] b = { 0.045, 0.150, 0.045 };
            double[] g = { 0.095, 0.235, 0.075 };
            double[] c = { colour.r / 255.0, colour.g / 255.0, colour.b / 255.0 };

            double sbb = 0, sdd = 0, sbd = 0, sbc = 0, sdc = 0;
            for (int i = 0; i < 3; i++)
            {
                double d = g[i] - b[i];
                double w = d * d;
                sbb += w * b[i] * b[i];
                sdd += w * d * d;
                sbd += w * b[i] * d;
                sbc += w * b[i] * c[i];
                sdc += w * d * c[i];
            }

            double det = sbb * sdd - sbd * sbd;
            if (System.Math.Abs(det) < 1e-18)
            {
                return false;
            }

            double shade = (sbc * sdd - sdc * sbd) / det;
            double e = (sbb * sdc - sbd * sbc) / det;
            if (System.Math.Abs(shade) < 1e-9)
            {
                return false;
            }

            double m = e / shade;
            double worst = 0;
            for (int i = 0; i < 3; i++)
            {
                double want = shade * b[i] + e * (g[i] - b[i]);
                worst = System.Math.Max(worst, System.Math.Abs(want - c[i]) / System.Math.Max(c[i], 1e-6));
            }

            if (worst >= 0.02 || shade <= 0.85 || shade >= 1.15 || m <= -0.02 || m >= 1.02)
            {
                return false;
            }

            moisture = (float)m;
            return true;
        }

        /// <summary>32 m block map of recovered moisture, for the vegetation author.</summary>
        public static float[] BuildMoistureMap(WorldData data)
        {
            return BuildMoistureBlocks(data);
        }

        /// <summary>Bilinear moisture at a world position (m).</summary>
        public static float SampleMoistureAt(float[] blocks, WorldData data, float worldX, float worldZ)
        {
            float half = data.SizeMeters * 0.5f;
            int ix = Mathf.Clamp((int)((worldX + half) / data.CellSize), 0, data.Resolution - 1);
            int iz = Mathf.Clamp((int)((worldZ + half) / data.CellSize), 0, data.Resolution - 1);
            return SampleBlockMoisture(blocks, data.Resolution, ix, iz);
        }

        private static float[] BuildMoistureBlocks(WorldData data)
        {
            int res = data.Resolution;
            int size = MoistureBlockCells;
            int blocks = (res + size - 1) / size;
            var sum = new double[blocks * blocks];
            var count = new int[blocks * blocks];

            int known = 0;
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    if (data.Biomes[i] != 2) // grass only
                    {
                        continue;
                    }
                    if (!ResolveMoisture(data.Colors[i], out float m))
                    {
                        continue;
                    }
                    int block = (z / size) * blocks + (x / size);
                    sum[block] += m;
                    count[block]++;
                    known++;
                }
            }

            var mean = new float[blocks * blocks];
            for (int i = 0; i < mean.Length; i++)
            {
                mean[i] = count[i] > 0 ? (float)(sum[i] / count[i]) : -1f;
            }

            if (known == 0)
            {
                for (int i = 0; i < mean.Length; i++)
                {
                    mean[i] = 0.5f;
                }
                return mean;
            }

            // Blocks without a readable grass vertex (ocean, steep rock) inherit the
            // nearest known value, so coastal grass does not inherit "0 = steppe".
            for (int pass = 0; pass < 16; pass++)
            {
                bool changed = false;
                for (int bz = 0; bz < blocks; bz++)
                {
                    for (int bx = 0; bx < blocks; bx++)
                    {
                        int i = bz * blocks + bx;
                        if (mean[i] >= 0f)
                        {
                            continue;
                        }
                        double s = 0;
                        int n = 0;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = bx + dx;
                                int nz = bz + dz;
                                if (nx < 0 || nz < 0 || nx >= blocks || nz >= blocks)
                                {
                                    continue;
                                }
                                float v = mean[nz * blocks + nx];
                                if (v < 0f)
                                {
                                    continue;
                                }
                                s += v;
                                n++;
                            }
                        }
                        if (n > 0)
                        {
                            mean[i] = (float)(s / n);
                            changed = true;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }

            return mean;
        }

        /// <summary>Bilinear block lookup; block centres are half a block apart.</summary>
        private static float SampleBlockMoisture(float[] blocks, int res, int x, int z)
        {
            int size = MoistureBlockCells;
            int n = (res + size - 1) / size;
            float fx = Mathf.Clamp((x + 0.5f) / size - 0.5f, 0f, n - 1);
            float fz = Mathf.Clamp((z + 0.5f) / size - 0.5f, 0f, n - 1);
            int x0 = (int)fx;
            int z0 = (int)fz;
            int x1 = Mathf.Min(x0 + 1, n - 1);
            int z1 = Mathf.Min(z0 + 1, n - 1);
            float tx = fx - x0;
            float tz = fz - z0;

            float v00 = Mathf.Max(blocks[z0 * n + x0], 0f);
            float v10 = Mathf.Max(blocks[z0 * n + x1], 0f);
            float v01 = Mathf.Max(blocks[z1 * n + x0], 0f);
            float v11 = Mathf.Max(blocks[z1 * n + x1], 0f);
            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), tz);
        }

        // --------------------------------------------------------------- colour ---

        private Color SampleRaw(WorldData data, int x, int z, float moisture)
        {
            int res = data.Resolution;
            int idx = z * res + x;
            float h = data.Heights[idx];
            byte biome = data.Biomes[idx];
            Vector3 n = data.NormalAt(x, z);
            float up = Mathf.Clamp01(n.y);
            float slope = Mathf.Sqrt(Mathf.Max(0f, 1f - up * up)) / Mathf.Max(up, 0.001f);

            Color c;
            switch (biome)
            {
                case 0: // ocean floor — dark, so the water plane reads as water
                    c = Color.Lerp(seaFloor, seaFloorDeep, Mathf.Clamp01(-h / 60f));
                    break;
                case 1: // beach
                    c = saltShore;
                    break;
                case 2: // grass -> forest or steppe
                    c = GrassColor(h, up, slope, moisture, Hash01(x, z));
                    break;
                case 3: // desert
                    c = Color.Lerp(duneShadow, dune, Mathf.Clamp01(h / 170f));
                    break;
                case 4: // rock / cliff
                    c = RockColor(h, up, slope);
                    break;
                default: // snow
                    c = SnowColor(up, slope);
                    break;
            }

            // Deterministic mottling so flat areas do not read as plastic.
            float v = Hash01(x, z);
            c *= 1f + (v - 0.5f) * 0.12f;

            // Steep faces are darker: dunes read better with a little slope shading.
            c *= Mathf.Lerp(1f, 0.78f, Mathf.Clamp01((1f - up) * 2.2f));

            // Curvature AO: concave hollows darken, convex crests catch a little light.
            // This is what makes a 4 m mesh read as dunes instead of a smooth blob.
            float lap = (data.HeightAt(x + 1, z) + data.HeightAt(x - 1, z)
                         + data.HeightAt(x, z + 1) + data.HeightAt(x, z - 1)) * 0.25f - h;
            float ao = 1f - Mathf.Clamp(lap * 0.34f, -0.16f, 0.34f);
            c *= ao;

            c.a = 1f;
            return c;
        }

        /// <summary>
        /// Grass has no biome id split: forest and steppe are the same data, separated
        /// by recovered moisture. The 0.62–0.72 band is the 24 m transition.
        /// </summary>
        /// <summary>
        /// Forest weight for a recovered moisture: 0 = steppe, 1 = forest, smooth over
        /// the 0.62–0.72 band. NaN clamps to the steppe end and ±Inf to its own end,
        /// so a bad sample can never turn a vertex colour into NaN.
        /// </summary>
        public static float ForestBlend(float moisture)
        {
            if (float.IsNaN(moisture) || moisture <= SteppeMoisture)
            {
                return 0f;
            }
            if (moisture >= ForestMoisture)
            {
                return 1f;
            }
            float t = (moisture - SteppeMoisture) / (ForestMoisture - SteppeMoisture);
            return Mathf.SmoothStep(0f, 1f, t);
        }

        private Color GrassColor(float h, float up, float slope, float moisture, float hash)
        {
            float t = ForestBlend(moisture);

            Color canopy = Color.Lerp(forestShadow, forest, up);
            canopy = Color.Lerp(canopy, forestLit, Mathf.Clamp01((up - 0.92f) * 8f));
            if (up > 0.93f && h < 120f)
            {
                canopy = Color.Lerp(canopy, litter, 0.35f * hash);
            }
            canopy = Color.Lerp(canopy, bark, Mathf.Clamp01((slope - 0.5f) * 1.2f));

            Color straw = Color.Lerp(steppeShadow, steppe, up);
            straw = Color.Lerp(straw, steppeLit, Mathf.Clamp01((up - 0.94f) * 10f));
            straw = Color.Lerp(straw, ochre, 0.30f * hash);
            if (hash > 0.86f)
            {
                straw = Color.Lerp(straw, sparseBrown, 0.35f);
            }

            Color c = Color.Lerp(straw, canopy, t);

            // No trees above the slope threshold: the ground reads as bare ochre.
            if (slope > NoTreeSlope)
            {
                c = Color.Lerp(c, ochre, Mathf.Clamp01((slope - NoTreeSlope) * 2.5f));
            }
            return c;
        }

        private Color RockColor(float h, float up, float slope)
        {
            Color c = Color.Lerp(rockShadow, rock, up);
            c = Color.Lerp(c, rockLit, Mathf.Clamp01((up - 0.90f) * 6f));
            c = Color.Lerp(c, rockShadow, Mathf.Clamp01((slope - 0.8f) * 0.6f));

            if (h > SnowLineMeters)
            {
                float cap = Mathf.SmoothStep(0f, 1f,
                               Mathf.InverseLerp(SnowLineMeters, SnowLineMeters + 22f, h))
                           * Mathf.Clamp01(up * 1.4f);
                c = Color.Lerp(c, highSnow, cap);
            }
            return c;
        }

        private Color SnowColor(float up, float slope)
        {
            Color c = Color.Lerp(snowShadow, snow, up);
            c = Color.Lerp(c, snowLit, Mathf.Clamp01((up - 0.93f) * 10f));
            if (slope > 0.9f)
            {
                c = Color.Lerp(c, ice, Mathf.Clamp01((slope - 0.9f) * 2f));
            }
            return c;
        }

        private static float Hash01(int x, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663);
                h ^= h >> 13;
                h *= 0x85ebca6b;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        private static Color32 ToLinearByte(Color srgb)
        {
            float r = Mathf.Pow(Mathf.Clamp01(srgb.r), 2.2f);
            float g = Mathf.Pow(Mathf.Clamp01(srgb.g), 2.2f);
            float b = Mathf.Pow(Mathf.Clamp01(srgb.b), 2.2f);
            return new Color32(
                (byte)(Mathf.Clamp01(r) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(g) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(b) * 255f + 0.5f),
                255);
        }

        private static Color Hex(string hex)
        {
            return ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.magenta;
        }
    }
}
