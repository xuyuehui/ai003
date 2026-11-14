using NUnit.Framework;
using UnityEngine;
using AI003.World;

namespace AI003.Tests.EditMode
{
    /// <summary>
    /// Pins the biome -> theme mapping: every biome id the server writes maps to one
    /// palette, the palettes are the ones agreed in docs/worldview/03-art-direction.md,
    /// and no two biomes resolve to the same look (that is what keeps the six terrains
    /// recognisable).
    /// </summary>
    public class BiomeThemeTests
    {
        private const byte Ocean = 0;
        private const byte Beach = 1;
        private const byte Grass = 2;
        private const byte Desert = 3;
        private const byte Rock = 4;
        private const byte Snow = 5;

        private static void AssertHex(Color actual, string expectedHex, string what)
        {
            var expected = ColorUtility.TryParseHtmlString("#" + expectedHex, out Color parsed) ? parsed : Color.magenta;
            Assert.AreEqual(expected.r, actual.r, 1e-3f, $"{what} red (want #{expectedHex})");
            Assert.AreEqual(expected.g, actual.g, 1e-3f, $"{what} green (want #{expectedHex})");
            Assert.AreEqual(expected.b, actual.b, 1e-3f, $"{what} blue (want #{expectedHex})");
        }

        [Test]
        public void PaletteMatchesTheArtDirection()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                // Ocean / shore
                AssertHex(theme.saltSea, "EEE8DC", "saltSea");
                AssertHex(theme.saltShore, "E3DAC6", "saltShore");
                AssertHex(theme.saltDeep, "C6BFB0", "saltDeep");

                // Desert
                AssertHex(theme.dune, "D3A974", "dune");
                AssertHex(theme.duneShadow, "9C744C", "duneShadow");
                AssertHex(theme.rust, "764833", "rust");

                // Temperate forest (saturation <= 0.33 per §3.1)
                AssertHex(theme.forestLit, "8AA06B", "forestLit");
                AssertHex(theme.forest, "5F7A4C", "forest");
                AssertHex(theme.forestShadow, "33452F", "forestShadow");
                AssertHex(theme.litter, "7C6344", "litter");
                AssertHex(theme.bark, "4A3B2E", "bark");

                // Steppe
                AssertHex(theme.steppeLit, "BEB083", "steppeLit");
                AssertHex(theme.steppe, "9A8F68", "steppe");
                AssertHex(theme.steppeShadow, "5F5940", "steppeShadow");
                AssertHex(theme.ochre, "A89376", "ochre");
                AssertHex(theme.sparseBrown, "6B5A45", "sparseBrown");

                // Snow / high mountain
                AssertHex(theme.snowLit, "F5F8FA", "snowLit");
                AssertHex(theme.snow, "DDE7ED", "snow");
                AssertHex(theme.snowShadow, "A7BCD1", "snowShadow");
                AssertHex(theme.ice, "9DB4C6", "ice");
                AssertHex(theme.rockLit, "8C8273", "rockLit");
                AssertHex(theme.rock, "6A6156", "rock");
                AssertHex(theme.rockShadow, "3B362E", "rockShadow");
                AssertHex(theme.highSnow, "E9EFF3", "highSnow");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void OceanFloorIsDarkTealNotSaltWhite()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                // BuildColorGrid returns LINEAR bytes (the meshes consume them as-is,
                // see docs/specs/unity-client.md), so compare qualitatively.
                Color shallow = TestWorldFactory.Mean(theme.BuildColorGrid(
                    TestWorldFactory.Uniform(16, Ocean, -8f, new Color32(200, 190, 176, 255))));
                Color deep = TestWorldFactory.Mean(theme.BuildColorGrid(
                    TestWorldFactory.Uniform(16, Ocean, -60f, new Color32(200, 190, 176, 255))));

                Assert.Less(deep.r, 0.2f,
                    $"ocean floor must not read as salt white, got {deep}");
                Assert.Greater(deep.b, deep.r,
                    $"ocean floor must stay cool/teal (b > r), got {deep}");

                float shallowValue = 0.2126f * shallow.r + 0.7152f * shallow.g + 0.0722f * shallow.b;
                float deepValue = 0.2126f * deep.r + 0.7152f * deep.g + 0.0722f * deep.b;
                Assert.Greater(shallowValue, deepValue,
                    $"the sea floor must darken with depth (shallow {shallowValue:F4} vs deep {deepValue:F4})");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void OceanFloorIsDarkerThanTheBeach()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                Color beach = TestWorldFactory.Mean(theme.BuildColorGrid(
                    TestWorldFactory.Uniform(16, Beach, 2f, new Color32(210, 200, 180, 255))));
                Color deep = TestWorldFactory.Mean(theme.BuildColorGrid(
                    TestWorldFactory.Uniform(16, Ocean, -60f, new Color32(200, 190, 176, 255))));

                float beachValue = 0.2126f * beach.r + 0.7152f * beach.g + 0.0722f * beach.b;
                float deepValue = 0.2126f * deep.r + 0.7152f * deep.g + 0.0722f * deep.b;
                Assert.Greater(beachValue, deepValue * 4f,
                    $"the sea floor ({deepValue:F4}) must be far darker than the beach ({beachValue:F4}); " +
                    "otherwise the water plane reads as salt flat");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void BeachIsTheSaltShorePalette()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                Color c = TestWorldFactory.Mean(
                    theme.BuildColorGrid(TestWorldFactory.Uniform(16, Beach, 2f, new Color32(210, 200, 180, 255))));

                Assert.Greater(c.r, c.g, "beach must be warm sand (r > g)");
                Assert.Greater(c.g, c.b, "beach must be warm sand (g > b)");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void DesertIsTheDunePalette()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                Color low = TestWorldFactory.Mean(
                    theme.BuildColorGrid(TestWorldFactory.Uniform(16, Desert, 20f, new Color32(211, 169, 116, 255))));
                Color high = TestWorldFactory.Mean(
                    theme.BuildColorGrid(TestWorldFactory.Uniform(16, Desert, 165f, new Color32(211, 169, 116, 255))));

                Assert.Greater(high.r, high.b + 0.2f, $"dune must read warm, got {high}");
                Assert.Greater(high.r, low.r - 0.02f, "high dunes must not be darker than the dune shadow");

                // duneShadow -> dune lerp by h/170: a 165 m dune is near the light end.
                float highValue = 0.2126f * high.r + 0.7152f * high.g + 0.0722f * high.b;
                float lowValue = 0.2126f * low.r + 0.7152f * low.g + 0.0722f * low.b;
                Assert.Greater(highValue, lowValue, "dune shading must lighten with height");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void RockWallIsNeutralAndSnowCappedAboveTheSnowLine()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                // 100-255 m is the rock band; 254.7 m is the snow line.
                Color wall = TestWorldFactory.Mean(
                    theme.BuildColorGrid(TestWorldFactory.Uniform(16, Rock, 180f, new Color32(120, 110, 100, 255))));
                Color capped = TestWorldFactory.Mean(
                    theme.BuildColorGrid(TestWorldFactory.Uniform(16, Rock, 320f, new Color32(120, 110, 100, 255))));

                Assert.Less(Mathf.Abs(wall.r - wall.g), 0.12f, $"rock must be near-neutral, got {wall}");

                float wallValue = 0.2126f * wall.r + 0.7152f * wall.g + 0.0722f * wall.b;
                float cappedValue = 0.2126f * capped.r + 0.7152f * capped.g + 0.0722f * capped.b;
                Assert.Greater(cappedValue, wallValue + 0.05f,
                    $"rock above the {WorldTheme.SnowLineMeters} m snow line must lighten (cap), " +
                    $"wall {wallValue:F3} vs capped {cappedValue:F3}");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void SnowIsCoolAndBright()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                Color c = TestWorldFactory.Mean(
                    theme.BuildColorGrid(TestWorldFactory.Uniform(16, Snow, 300f, new Color32(230, 236, 240, 255))));

                Assert.Greater(c.b, c.r, $"snow must be cool (b > r), got {c}");
                float value = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                Assert.Greater(value, 0.6f, $"snow must read bright, got {value:F3}");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void AllSixBiomesResolveToDistinctLooks()
        {
            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                var looks = new[]
                {
                    ("ocean", theme.BuildColorGrid(TestWorldFactory.Uniform(16, Ocean, -60f, new Color32(200, 190, 176, 255)))),
                    ("beach", theme.BuildColorGrid(TestWorldFactory.Uniform(16, Beach, 2f, new Color32(210, 200, 180, 255)))),
                    ("grass", theme.BuildColorGrid(TestWorldFactory.Grass(16, 0.3f))),
                    ("desert", theme.BuildColorGrid(TestWorldFactory.Uniform(16, Desert, 90f, new Color32(211, 169, 116, 255)))),
                    ("rock", theme.BuildColorGrid(TestWorldFactory.Uniform(16, Rock, 180f, new Color32(120, 110, 100, 255)))),
                    ("snow", theme.BuildColorGrid(TestWorldFactory.Uniform(16, Snow, 300f, new Color32(230, 236, 240, 255)))),
                };

                for (int i = 0; i < looks.Length; i++)
                {
                    for (int j = i + 1; j < looks.Length; j++)
                    {
                        Color a = TestWorldFactory.Mean(looks[i].Item2);
                        Color b = TestWorldFactory.Mean(looks[j].Item2);
                        float distance = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
                        Assert.Greater(distance, 0.05f,
                            $"{looks[i].Item1} and {looks[j].Item1} resolve to nearly the same colour " +
                            $"(distance {distance:F3}); the six terrains would not be separable");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }
    }
}
