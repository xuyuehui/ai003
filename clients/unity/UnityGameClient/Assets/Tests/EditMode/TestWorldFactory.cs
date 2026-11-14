using System;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using AI003.World;

namespace AI003.Tests.EditMode
{
    /// <summary>
    /// Builds tiny synthetic worlds on disk and loads them through the production
    /// <see cref="WorldData.Load"/>, so colour/theme tests exercise the real code
    /// path without depending on the 2049² StreamingAssets world.
    /// </summary>
    public static class TestWorldFactory
    {
        // worldgen's grass model (server/internal/worldgen/world.go):
        //   colour = shade * mix(colGrassD, colGrass, clamp01(moisture * 1.15))
        private static readonly double[] GrassDark = { 0.045, 0.150, 0.045 };
        private static readonly double[] GrassLight = { 0.095, 0.235, 0.075 };

        /// <summary>A baked grass colour for the given moisture, as worldgen writes it.</summary>
        public static Color32 GrassColour(float moisture, float shade = 1f)
        {
            double t = Mathf.Clamp01(moisture * 1.15f);
            var bytes = new byte[3];
            for (int i = 0; i < 3; i++)
            {
                double c = shade * (GrassDark[i] + t * (GrassLight[i] - GrassDark[i]));
                bytes[i] = (byte)Math.Round(Mathf.Clamp01((float)c) * 255f);
            }
            return new Color32(bytes[0], bytes[1], bytes[2], 255);
        }

        /// <summary>One uniform world: every vertex shares a biome, height and colour.</summary>
        public static WorldData Uniform(int resolution, byte biome, float height, Color32 colour)
        {
            int count = resolution * resolution;
            var biomes = new byte[count];
            var heights = new float[count];
            var colors = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                biomes[i] = biome;
                heights[i] = height;
                colors[i] = colour;
            }
            return Build(resolution, biomes, heights, colors);
        }

        /// <summary>One uniform grass world whose baked colour encodes the given moisture.</summary>
        public static WorldData Grass(int resolution, float moisture)
        {
            return Uniform(resolution, 2, 40f, GrassColour(moisture));
        }

        public static WorldData Build(int resolution, byte[] biomes, float[] heights, Color32[] colors)
        {
            Assert.AreEqual(resolution * resolution, biomes.Length, "biome count");
            Assert.AreEqual(resolution * resolution, heights.Length, "height count");
            Assert.AreEqual(resolution * resolution, colors.Length, "colour count");

            string dir = Path.Combine(Application.temporaryCachePath,
                "ai003-editmode-synthetic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                float sizeMeters = 64f;
                float cellSize = sizeMeters / (resolution - 1);

                float min = float.MaxValue;
                float max = float.MinValue;
                foreach (float h in heights)
                {
                    min = Mathf.Min(min, h);
                    max = Mathf.Max(max, h);
                }

                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\"version\":1,\"name\":\"editmode-synthetic\",\"seed\":1," +
                    "\"sizeMeters\":{0},\"resolution\":{1},\"cellSize\":{2},\"seaLevel\":0," +
                    "\"minHeight\":{3},\"maxHeight\":{4},\"spawn\":{{\"x\":0,\"y\":{5},\"z\":0}}}}",
                    sizeMeters, resolution, cellSize, min, max, heights[0]);

                File.WriteAllText(Path.Combine(dir, "world.json"), json);

                var heightBytes = new byte[heights.Length * 4];
                Buffer.BlockCopy(heights, 0, heightBytes, 0, heightBytes.Length);
                File.WriteAllBytes(Path.Combine(dir, "heightmap.f32"), heightBytes);

                var colorBytes = new byte[colors.Length * 4];
                for (int i = 0; i < colors.Length; i++)
                {
                    colorBytes[i * 4 + 0] = colors[i].r;
                    colorBytes[i * 4 + 1] = colors[i].g;
                    colorBytes[i * 4 + 2] = colors[i].b;
                    colorBytes[i * 4 + 3] = colors[i].a;
                }
                File.WriteAllBytes(Path.Combine(dir, "colors.rgba8"), colorBytes);
                File.WriteAllBytes(Path.Combine(dir, "biomes.r8"), biomes);

                return WorldData.Load(dir);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>Mean colour of a built grid, over all vertices, normalised to 0..1.</summary>
        public static Color Mean(Color32[] grid)
        {
            double r = 0, g = 0, b = 0;
            foreach (Color32 c in grid)
            {
                r += c.r;
                g += c.g;
                b += c.b;
            }
            double n = Math.Max(grid.Length, 1) * 255.0;
            return new Color((float)(r / n), (float)(g / n), (float)(b / n));
        }
    }
}
