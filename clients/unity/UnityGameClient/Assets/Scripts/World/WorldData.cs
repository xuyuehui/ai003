using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AI003.World
{
    [Serializable]
    public class WorldMetadata
    {
        public int version;
        public string name;
        public long seed;
        public float sizeMeters;
        public int resolution;
        public float cellSize;
        public float seaLevel;
        public float minHeight;
        public float maxHeight;
        public WorldSpawn spawn;
    }

    [Serializable]
    public class WorldSpawn
    {
        public float x;
        public float y;
        public float z;
    }

    /// <summary>
    /// The shared world, loaded from StreamingAssets/world. Both engines read the
    /// exact same files (see server/cmd/worldgen) so the terrain cannot diverge.
    /// </summary>
    public sealed class WorldData
    {
        public WorldMetadata Meta { get; private set; }
        public float[] Heights { get; private set; }
        public Color32[] Colors { get; private set; }
        public byte[] Biomes { get; private set; }

        public int Resolution => Meta.resolution;
        public float SizeMeters => Meta.sizeMeters;
        public float CellSize => Meta.cellSize;
        public float SeaLevel => Meta.seaLevel;
        public Vector3 Spawn => new Vector3(Meta.spawn.x, Meta.spawn.y, Meta.spawn.z);

        public static WorldData Load(string directory)
        {
            string jsonPath = Path.Combine(directory, "world.json");
            if (!File.Exists(jsonPath))
            {
                throw new FileNotFoundException($"world.json not found at {jsonPath}; run scripts/generate-world.cmd", jsonPath);
            }

            var meta = JsonConvert.DeserializeObject<WorldMetadata>(File.ReadAllText(jsonPath));
            if (meta == null || meta.resolution < 2)
            {
                throw new InvalidDataException($"invalid world metadata at {jsonPath}");
            }

            int count = meta.resolution * meta.resolution;
            var data = new WorldData { Meta = meta };

            byte[] heightBytes = File.ReadAllBytes(Path.Combine(directory, "heightmap.f32"));
            if (heightBytes.Length != count * 4)
            {
                throw new InvalidDataException($"heightmap has {heightBytes.Length} bytes, expected {count * 4}");
            }
            data.Heights = new float[count];
            Buffer.BlockCopy(heightBytes, 0, data.Heights, 0, heightBytes.Length);

            byte[] colorBytes = File.ReadAllBytes(Path.Combine(directory, "colors.rgba8"));
            if (colorBytes.Length != count * 4)
            {
                throw new InvalidDataException($"colours have {colorBytes.Length} bytes, expected {count * 4}");
            }
            data.Colors = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                data.Colors[i] = new Color32(colorBytes[i * 4], colorBytes[i * 4 + 1], colorBytes[i * 4 + 2], colorBytes[i * 4 + 3]);
            }

            data.Biomes = File.ReadAllBytes(Path.Combine(directory, "biomes.r8"));
            if (data.Biomes.Length != count)
            {
                throw new InvalidDataException($"biomes have {data.Biomes.Length} bytes, expected {count}");
            }

            return data;
        }

        /// <summary>Bilinear height lookup in metres, matching the server's sampler.</summary>
        public float HeightAt(float x, float z)
        {
            int res = Meta.resolution;
            float cell = Meta.cellSize;
            float half = Meta.sizeMeters * 0.5f;

            float fx = (x + half) / cell;
            float fz = (z + half) / cell;
            if (fx < 0 || fz < 0 || fx > res - 1 || fz > res - 1)
            {
                return Meta.seaLevel - 50f;
            }

            int x0 = (int)fx;
            int z0 = (int)fz;
            int x1 = Mathf.Min(x0 + 1, res - 1);
            int z1 = Mathf.Min(z0 + 1, res - 1);
            float tx = fx - x0;
            float tz = fz - z0;

            float h00 = Heights[z0 * res + x0];
            float h10 = Heights[z0 * res + x1];
            float h01 = Heights[z1 * res + x0];
            float h11 = Heights[z1 * res + x1];

            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
        }

        /// <summary>Surface normal built from the height grid (matches the client mesh).</summary>
        public Vector3 NormalAt(int ix, int iz)
        {
            int res = Meta.resolution;
            float cell = Meta.cellSize;

            int x0 = Mathf.Max(ix - 1, 0);
            int x1 = Mathf.Min(ix + 1, res - 1);
            int z0 = Mathf.Max(iz - 1, 0);
            int z1 = Mathf.Min(iz + 1, res - 1);

            float dhdx = (Heights[iz * res + x1] - Heights[iz * res + x0]) / ((x1 - x0) * cell);
            float dhdz = (Heights[z1 * res + ix] - Heights[z0 * res + ix]) / ((z1 - z0) * cell);

            return new Vector3(-dhdx, 1f, -dhdz).normalized;
        }
    }
}
