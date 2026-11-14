using System.Collections.Generic;
using System.IO;
using AI003.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AI003.Tests.EditMode
{
    /// <summary>
    /// Stage 3 guards for the two things that are cheaper to break than to notice:
    /// the 254.7 m snow line, and the "one grass biome, split by moisture" reading
    /// that makes the forest frame read as forest and the steppe frame as steppe.
    ///
    /// Deliberately NOT covered here: the per-frame crown coverage percentage. That is
    /// measured on the rendered stills by 验证 (scripts/stills-vegdiff.py). Duplicating
    /// an image metric in an EditMode test would only produce a number nobody can keep
    /// in sync with the camera table.
    ///
    /// Every threshold below was measured first, offline, from the checked-in world
    /// data with a faithful port of WorldTheme.BuildMoistureMap:
    /// artifacts/stage3-moisture-probe.py
    ///   forest site  150 m disc: 88.6% of samples reach the 0.72 forest gate
    ///   steppe site  150 m disc: 54.0% sit at or below the 0.62 steppe gate
    ///   mean moisture: 0.779 (forest) vs 0.631 (steppe)
    ///   ocean site   300 m disc: 57.6% ocean vertices, lowest vertex -64.1 m
    ///   mountain     1000 m disc: 458,296 rock/snow vertices, max height 396.5 m
    /// Site coordinates mirror AshkarStill.Frames (the one source of truth for the
    /// six stills); they are pinned here on purpose so a camera move shows up as a
    /// test change instead of a silently different picture.
    /// </summary>
    public class Stage3TerrainAssertions
    {
        private const byte Ocean = 0;
        private const byte Beach = 1;
        private const byte Grass = 2;
        private const byte Desert = 3;
        private const byte Rock = 4;
        private const byte Snow = 5;

        // AshkarStill.cs:80 forest, :84 steppe, :70 desert, :87 snow, :88 mountain, :71 ocean
        private const float ForestSiteX = -237f;
        private const float ForestSiteZ = 727f;
        private const float SteppeSiteX = -592f;
        private const float SteppeSiteZ = -368f;
        private const float DesertSiteX = -592f;
        private const float DesertSiteZ = 248f;
        private const float SnowSiteX = 109f;
        private const float SnowSiteZ = 512f;
        private const float OceanSiteX = -556f;
        private const float OceanSiteZ = 576f;
        private const float MountainSiteX = 115f;
        private const float MountainSiteZ = -409f;

        private const float SampleRadiusMeters = 150f;
        private const float SampleStepMeters = 6f;

        private static WorldData _world;

        private static WorldData World
        {
            get
            {
                if (_world == null)
                {
                    _world = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
                }
                return _world;
            }
        }

        private static float Luma(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        private static float MeanLuma(WorldTheme theme, byte biome, float height)
        {
            return Luma(TestWorldFactory.Mean(
                theme.BuildColorGrid(TestWorldFactory.Uniform(16, biome, height, new Color32(120, 110, 100, 255)))));
        }

        [Test]
        public void SnowCapStartsExactlyAtTheSnowLineAndSaturatesBeforeTheBandEnds()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                // 254.7 is the line; the cap ramps 254.7 -> 276.7 (SnowLineMeters + 22).
                float below = MeanLuma(theme, Rock, WorldTheme.SnowLineMeters - 0.1f);
                float onTheLine = MeanLuma(theme, Rock, WorldTheme.SnowLineMeters);
                float justAbove = MeanLuma(theme, Rock, WorldTheme.SnowLineMeters + 0.05f);
                float bandEnd = MeanLuma(theme, Rock, WorldTheme.SnowLineMeters + 22f);
                float wellAbove = MeanLuma(theme, Rock, 320f);

                Assert.AreEqual(below, onTheLine, 1e-4f,
                    $"rock at {WorldTheme.SnowLineMeters - 0.1f} m and exactly on the " +
                    $"{WorldTheme.SnowLineMeters} m line must be the same colour (the cap is `h > line`)");
                Assert.GreaterOrEqual(justAbove, onTheLine - 1e-4f,
                    "crossing the snow line must never darken the rock");
                Assert.Greater(bandEnd - onTheLine, 0.04f,
                    $"the snow cap must be clearly brighter by the end of its band " +
                    $"({WorldTheme.SnowLineMeters + 22f} m): line {onTheLine:F3} vs band end {bandEnd:F3}");
                Assert.AreEqual(bandEnd, wellAbove, 1e-3f,
                    $"the cap must saturate at {WorldTheme.SnowLineMeters + 22f} m; a 320 m wall " +
                    $"({wellAbove:F3}) must not be whiter than the band end ({bandEnd:F3})");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void SnowCapRampIsMonotonic()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                float previous = float.NegativeInfinity;
                for (float h = WorldTheme.SnowLineMeters; h <= WorldTheme.SnowLineMeters + 30f; h += 0.5f)
                {
                    float luma = MeanLuma(theme, Rock, h);
                    Assert.GreaterOrEqual(luma, previous - 1e-4f,
                        $"snow cap luma fell at {h:F1} m ({luma:F4} after {previous:F4})");
                    previous = luma;
                }
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void ForestSiteReadsAsForestAndSteppeSiteReadsAsSteppe()
        {
            float[] moisture = WorldTheme.BuildMoistureMap(World);

            float forestFraction = FractionAtOrAbove(moisture, ForestSiteX, ForestSiteZ, WorldTheme.ForestMoisture);
            float steppeFraction = FractionAtOrBelow(moisture, SteppeSiteX, SteppeSiteZ, WorldTheme.SteppeMoisture);
            float forestMean = MeanMoisture(moisture, ForestSiteX, ForestSiteZ);
            float steppeMean = MeanMoisture(moisture, SteppeSiteX, SteppeSiteZ);

            Assert.GreaterOrEqual(forestFraction, 0.60f,
                $"only {100f * forestFraction:F1}% of the {SampleRadiusMeters:F0} m disc around the forest " +
                $"site reaches the {WorldTheme.ForestMoisture} forest gate; the forest frame has nothing to show");
            Assert.GreaterOrEqual(steppeFraction, 0.40f,
                $"only {100f * steppeFraction:F1}% of the {SampleRadiusMeters:F0} m disc around the steppe " +
                $"site is at or below the {WorldTheme.SteppeMoisture} steppe gate; the steppe frame would read " +
                "as forest");
            Assert.Greater(forestMean - steppeMean, 0.10f,
                $"forest site moisture {forestMean:F3} and steppe site moisture {steppeMean:F3} are too close; " +
                "the two frames would not separate");

            // The split only exists because both frames sit on the same grass biome.
            Assert.AreEqual(Grass, BiomeAt(ForestSiteX, ForestSiteZ), "forest site biome");
            Assert.AreEqual(Grass, BiomeAt(SteppeSiteX, SteppeSiteZ), "steppe site biome");
        }

        [Test]
        public void SixFramesSitOnSixDifferentTerrains()
        {
            Assert.AreEqual(Grass, BiomeAt(ForestSiteX, ForestSiteZ), "forest frame must sit on grass");
            Assert.AreEqual(Grass, BiomeAt(SteppeSiteX, SteppeSiteZ), "steppe frame must sit on grass");
            Assert.AreEqual(Desert, BiomeAt(DesertSiteX, DesertSiteZ), "desert frame must sit on the desert biome");
            Assert.AreEqual(Snow, BiomeAt(SnowSiteX, SnowSiteZ), "snow frame must sit on the snow biome");

            // Ocean frame stands on the shore and looks at the sea.
            byte oceanBiome = BiomeAt(OceanSiteX, OceanSiteZ);
            Assert.IsTrue(oceanBiome == Beach || oceanBiome == Ocean,
                $"ocean frame site should sit on beach or ocean, got biome {oceanBiome}");
            DiscCounts(OceanSiteX, OceanSiteZ, 300f, out int oceanTotal, out int oceanOcean, out _, out _);
            Assert.Greater(oceanOcean / (float)oceanTotal, 0.40f,
                $"only {100f * oceanOcean / oceanTotal:F1}% of the 300 m disc around the ocean frame is water; " +
                "the frame would read as a salt flat");

            // Mountain frame stands on grass and looks 900 m away at real relief.
            DiscCounts(MountainSiteX, MountainSiteZ, 1000f, out int mountainTotal, out _, out int rockOrSnow, out float maxHeight);
            Assert.Greater(rockOrSnow, 50000,
                $"only {rockOrSnow} rock/snow vertices within 1000 m of the mountain frame site " +
                $"(of {mountainTotal}); there is no mountain in the shot");
            Assert.Greater(maxHeight, 300f,
                $"the highest vertex within 1000 m of the mountain frame site is {maxHeight:F1} m; " +
                "the mountain frame would lose its peak");
        }

        // ------------------------------------------------------------------ data ---

        private static float FractionAtOrAbove(float[] moisture, float x, float z, float gate)
        {
            int hit = 0;
            int total = 0;
            ForEachSample(x, z, (wx, wz) =>
            {
                total++;
                if (WorldTheme.SampleMoistureAt(moisture, World, wx, wz) >= gate)
                {
                    hit++;
                }
            });
            return total == 0 ? 0f : (float)hit / total;
        }

        private static float FractionAtOrBelow(float[] moisture, float x, float z, float gate)
        {
            int hit = 0;
            int total = 0;
            ForEachSample(x, z, (wx, wz) =>
            {
                total++;
                if (WorldTheme.SampleMoistureAt(moisture, World, wx, wz) <= gate)
                {
                    hit++;
                }
            });
            return total == 0 ? 0f : (float)hit / total;
        }

        private static float MeanMoisture(float[] moisture, float x, float z)
        {
            double sum = 0;
            int total = 0;
            ForEachSample(x, z, (wx, wz) =>
            {
                sum += WorldTheme.SampleMoistureAt(moisture, World, wx, wz);
                total++;
            });
            return total == 0 ? 0f : (float)(sum / total);
        }

        private static void ForEachSample(float cx, float cz, System.Action<float, float> visit)
        {
            for (float dz = -SampleRadiusMeters; dz <= SampleRadiusMeters; dz += SampleStepMeters)
            {
                for (float dx = -SampleRadiusMeters; dx <= SampleRadiusMeters; dx += SampleStepMeters)
                {
                    if (dx * dx + dz * dz > SampleRadiusMeters * SampleRadiusMeters)
                    {
                        continue;
                    }
                    visit(cx + dx, cz + dz);
                }
            }
        }

        private static void DiscCounts(float cx, float cz, float radius,
            out int total, out int ocean, out int rockOrSnow, out float maxHeight)
        {
            total = 0;
            ocean = 0;
            rockOrSnow = 0;
            maxHeight = float.NegativeInfinity;

            float half = World.SizeMeters * 0.5f;
            int res = World.Resolution;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((cx - radius + half) / World.CellSize), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((cx + radius + half) / World.CellSize), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((cz - radius + half) / World.CellSize), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((cz + radius + half) / World.CellSize), 0, res - 1);

            for (int iz = z0; iz <= z1; iz++)
            {
                float wz = iz * World.CellSize - half;
                for (int ix = x0; ix <= x1; ix++)
                {
                    float wx = ix * World.CellSize - half;
                    if ((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz) > radius * radius)
                    {
                        continue;
                    }
                    int i = iz * res + ix;
                    total++;
                    byte biome = World.Biomes[i];
                    if (biome == Ocean)
                    {
                        ocean++;
                    }
                    if (biome == Rock || biome == Snow)
                    {
                        rockOrSnow++;
                    }
                    maxHeight = Mathf.Max(maxHeight, World.Heights[i]);
                }
            }

            if (total == 0)
            {
                maxHeight = 0f;
            }
        }

        private static byte BiomeAt(float x, float z)
        {
            float half = World.SizeMeters * 0.5f;
            int ix = Mathf.Clamp((int)((x + half) / World.CellSize), 0, World.Resolution - 1);
            int iz = Mathf.Clamp((int)((z + half) / World.CellSize), 0, World.Resolution - 1);
            return World.Biomes[iz * World.Resolution + ix];
        }
    }

    /// <summary>
    /// Pins the imported size of every vegetation source model. The unit-scale bug
    /// (centimetre FBX imported at 1/100) made every tree invisible without changing a
    /// single scatter position, so the sizes are asserted here instead of being read
    /// off a RunBatch log by hand.
    ///
    /// Expected sizes are the `srcHeight` values AuthorVegetation.RunBatch prints
    /// (artifacts/logs/author-veg-headstyle.log), measured as the model mesh bounds —
    /// exactly what AuthorVegetation.LoadModelMesh consumes.
    /// </summary>
    public class VegetationSourceSizeTests
    {
        private const string ModelDir = "Assets/Art/Models";
        private const float Tolerance = 0.05f; // 5%: catches unit/scale drift, allows art tweaks

        private static readonly (string Slug, float SrcHeight, float MinScale, float MaxScale)[] Species =
        {
            ("conifer", 12.000f, 0.85f, 1.15f),
            ("dead_tree", 2.601f, 0.85f, 1.15f),
            ("acacia_small", 6.563f, 0.85f, 1.15f),
            ("acacia", 11.045f, 0.90f, 1.15f),
            ("snow_rock", 2.631f, 0.85f, 1.15f),
            ("snow_rock_b", 3.123f, 0.85f, 1.15f),
            ("grass_tuft_b", 0.787f, 0.85f, 1.15f),
            ("bush_a", 1.412f, 0.85f, 1.15f),
        };

        private static Mesh LoadSourceMesh(string slug)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelDir}/{slug}/{slug}_LOD0.fbx");
            Assert.IsNotNull(model, $"{slug}: {ModelDir}/{slug}/{slug}_LOD0.fbx is missing");
            var filter = model.GetComponentInChildren<MeshFilter>(true);
            Assert.IsNotNull(filter, $"{slug}: model has no MeshFilter");
            Assert.IsNotNull(filter.sharedMesh, $"{slug}: model mesh is missing");
            return filter.sharedMesh;
        }

        [Test]
        public void EverySpeciesKeepsItsDocumentedSourceHeight()
        {
            var failures = new List<string>();
            foreach (var species in Species)
            {
                float actual = LoadSourceMesh(species.Slug).bounds.size.y;
                float expected = species.SrcHeight;
                if (Mathf.Abs(actual - expected) > expected * Tolerance)
                {
                    failures.Add($"{species.Slug}: mesh height {actual:F3} m, expected {expected:F3} m ±5%");
                }
            }
            Assert.IsEmpty(failures,
                "vegetation source heights drifted (unit scale or model change):\n" + string.Join("\n", failures));
        }

        [Test]
        public void InstanceHeightBandStaysInTheDocumentedRange()
        {
            // The scatter applies 0.85..1.15 (acacia 0.9..1.15) on top of srcHeight; if a
            // scale band changes, tree heights in the stills change without any camera move.
            var expectedInstances = new Dictionary<string, (float Min, float Max)>
            {
                { "conifer", (10.20f, 13.80f) },
                { "acacia_small", (5.58f, 7.55f) },
                { "snow_rock", (2.24f, 3.03f) },
            };

            foreach (var species in Species)
            {
                float src = LoadSourceMesh(species.Slug).bounds.size.y;
                float min = src * species.MinScale;
                float max = src * species.MaxScale;
                Assert.Less(min, max, $"{species.Slug}: empty instance height band");

                if (expectedInstances.TryGetValue(species.Slug, out var expected))
                {
                    Assert.AreEqual(expected.Min, min, 0.01f,
                        $"{species.Slug}: shortest instance {min:F2} m, logged {expected.Min:F2} m");
                    Assert.AreEqual(expected.Max, max, 0.01f,
                        $"{species.Slug}: tallest instance {max:F2} m, logged {expected.Max:F2} m");
                }
            }
        }

        [Test]
        public void ConiferKeepsItsTrunkAndCrownSubmeshes()
        {
            // Two slots are what give the crown its own material; one slot silently draws
            // the canopy with the bark texture (the bug 建模 fixed with the HEAD rebuild).
            // The other two rebuilt species are covered by 建模's check-fbx-assets gate.
            Assert.GreaterOrEqual(LoadSourceMesh("conifer").subMeshCount, 2,
                "conifer lost its crown submesh; the canopy would render with the bark material");
        }
    }
}
