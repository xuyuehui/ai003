using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// A world "skin": biome palette + material knobs. The shared world files stay
    /// untouched (UE5 keeps reading the baked colours); a theme re-colours the same
    /// height/biome data on the Unity side only.
    ///
    /// Colours are authored in sRGB and converted to the linear values the meshes
    /// need (the baked colours are already linear, see docs/specs/unity-client.md).
    /// </summary>
    [CreateAssetMenu(fileName = "WorldTheme", menuName = "AI003/World Theme")]
    public sealed class WorldTheme : ScriptableObject
    {
        [Header("Biomes (sRGB)")]
        public Color saltSea = Hex("EEE8DC");
        public Color saltShore = Hex("E3DAC6");
        public Color reg = Hex("A89376");
        public Color dune = Hex("D3A974");
        public Color basalt = Hex("2B2621");
        public Color frost = Hex("E6ECEE");

        [Header("Modifiers (sRGB)")]
        public Color duneShadow = Hex("9C744C");
        public Color rust = Hex("764833");
        public Color saltDeep = Hex("C6BFB0");

        [Header("Material")]
        [Range(0f, 1f)] public float smoothness = 0.18f;
        [Range(0f, 2f)] public float rippleStrength = 0.35f;
        [Range(0f, 1f)] public float speckle = 0.25f;

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
            var grid = new Color[res * res];

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    grid[z * res + x] = SampleRaw(data, x, z);
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

        private Color SampleRaw(WorldData data, int x, int z)
        {
            int res = data.Resolution;
            int idx = z * res + x;
            float h = data.Heights[idx];
            byte biome = data.Biomes[idx];
            float t;

            Color c;
            switch (biome)
            {
                case 0: // salt sea
                    c = Color.Lerp(saltSea, saltDeep, Mathf.Clamp01(-h / 70f));
                    break;
                case 1: // salt shore
                    c = saltShore;
                    break;
                case 2: // reg (gravel plain)
                    c = reg;
                    break;
                case 3: // dune sea
                    t = Mathf.Clamp01(h / 170f);
                    c = Color.Lerp(duneShadow, dune, t);
                    break;
                case 4: // basalt
                    t = Mathf.Clamp01((160f - h) / 260f) * 0.55f;
                    c = Color.Lerp(basalt, rust, t);
                    break;
                default: // frost crown
                    c = frost;
                    break;
            }

            // Deterministic mottling so flat areas do not read as plastic.
            float v = Hash01(x, z);
            c *= 1f + (v - 0.5f) * 0.12f;

            // Steep faces are darker: dunes read better with a little slope shading.
            Vector3 n = data.NormalAt(x, z);
            c *= Mathf.Lerp(1f, 0.78f, Mathf.Clamp01((1f - n.y) * 2.2f));

            // Curvature AO: concave hollows darken, convex crests catch a little light.
            // This is what makes a 4 m mesh read as dunes instead of a smooth blob.
            float lap = (data.HeightAt(x + 1, z) + data.HeightAt(x - 1, z)
                         + data.HeightAt(x, z + 1) + data.HeightAt(x, z - 1)) * 0.25f - h;
            float ao = 1f - Mathf.Clamp(lap * 0.34f, -0.16f, 0.34f);
            c *= ao;

            c.a = 1f;
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
