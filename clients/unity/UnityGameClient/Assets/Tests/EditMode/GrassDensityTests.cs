using NUnit.Framework;
using UnityEngine;
using AI003.World;

namespace AI003.Tests.EditMode
{
    /// <summary>
    /// Pins the forest/steppe separation rule the group agreed on: both live in the
    /// same <c>grass</c> biome, split by moisture recovered over 32 m blocks, with a
    /// 0.62–0.72 transition band. 0.62 -> steppe, 0.67 -> 50/50, 0.72 -> forest.
    /// </summary>
    public class GrassDensityTests
    {
        [Test]
        public void ThresholdsAreTheAgreedNumbers()
        {
            Assert.AreEqual(0.62f, WorldTheme.SteppeMoisture, 0f, "steppe threshold");
            Assert.AreEqual(0.72f, WorldTheme.ForestMoisture, 0f, "forest threshold");
            Assert.AreEqual(32, WorldTheme.MoistureBlockCells, "moisture must be averaged over 32 m blocks");
            Assert.AreEqual(254.7f, WorldTheme.SnowLineMeters, 1e-4f, "snow line");
            Assert.AreEqual(0.70f, WorldTheme.NoTreeSlope, 1e-4f, "no-tree slope");
            Assert.Less(WorldTheme.SteppeMoisture, WorldTheme.ForestMoisture, "steppe < forest");
        }

        [Test]
        public void SteppeEndIsPureSteppe()
        {
            Assert.AreEqual(0f, WorldTheme.ForestBlend(WorldTheme.SteppeMoisture), 0f,
                "m_block == 0.62 must be 100% steppe");
        }

        [Test]
        public void ForestEndIsPureForest()
        {
            Assert.AreEqual(1f, WorldTheme.ForestBlend(WorldTheme.ForestMoisture), 0f,
                "m_block == 0.72 must be 100% forest");
        }

        [Test]
        public void MidpointIsHalfAndHalf()
        {
            float mid = (WorldTheme.SteppeMoisture + WorldTheme.ForestMoisture) * 0.5f;
            Assert.AreEqual(0.67f, mid, 1e-6f, "the transition midpoint is 0.67");
            Assert.AreEqual(0.5f, WorldTheme.ForestBlend(mid), 1e-6f,
                "m_block == 0.67 must be a 50/50 forest/steppe blend");
        }

        [Test]
        public void BelowTheBandClampsToSteppe()
        {
            foreach (float m in new[] { 0f, 0.25f, 0.6199f, -0.5f, -100f })
            {
                Assert.AreEqual(0f, WorldTheme.ForestBlend(m), 0f, $"m_block {m} must clamp to steppe");
            }
        }

        [Test]
        public void AboveTheBandClampsToForest()
        {
            foreach (float m in new[] { 0.7201f, 0.9f, 1f, 2f, 100f })
            {
                Assert.AreEqual(1f, WorldTheme.ForestBlend(m), 0f, $"m_block {m} must clamp to forest");
            }
        }

        [Test]
        public void NonFiniteInputStaysFinite()
        {
            // A bad sample must never tint a vertex: NaN lands on steppe, +/-Inf on
            // their own end, and every result stays inside [0,1].
            float nan = WorldTheme.ForestBlend(float.NaN);
            Assert.IsFalse(float.IsNaN(nan), "NaN input produced a NaN blend");
            Assert.AreEqual(0f, nan, 0f, "NaN must fall back to the steppe end");

            Assert.AreEqual(0f, WorldTheme.ForestBlend(float.NegativeInfinity), 0f,
                "-Infinity must clamp to the steppe end");
            Assert.AreEqual(1f, WorldTheme.ForestBlend(float.PositiveInfinity), 0f,
                "+Infinity must clamp to the forest end");
        }

        [Test]
        public void BlendIsMonotonicAndStaysInRange()
        {
            float previous = -1f;
            for (int i = 0; i <= 2000; i++)
            {
                float m = i / 2000f;
                float blend = WorldTheme.ForestBlend(m);
                if (float.IsNaN(blend) || blend < 0f || blend > 1f)
                {
                    Assert.Fail($"ForestBlend({m}) = {blend} is outside [0,1]");
                }
                if (blend < previous)
                {
                    Assert.Fail($"ForestBlend is not monotonic at m_block={m}: {blend} after {previous}");
                }
                previous = blend;
            }
        }

        [Test]
        public void TransitionIsSymmetricAroundTheMidpoint()
        {
            const float delta = 0.03f;
            float low = WorldTheme.ForestBlend(0.67f - delta);
            float high = WorldTheme.ForestBlend(0.67f + delta);
            Assert.AreEqual(1f, low + high, 1e-4f,
                "the 0.62–0.72 ramp must be symmetric around 0.67");
        }

        [Test]
        public void ForestAndSteppeWorldsProduceDifferentGrassColours()
        {
            // The same grass biome, only the moisture in colors.rgba8 differs: the
            // theme must resolve forest and steppe from that, not from a biome id.
            WorldData forestWorld = TestWorldFactory.Grass(16, 0.95f);
            WorldData steppeWorld = TestWorldFactory.Grass(16, 0.05f);

            WorldTheme theme = WorldTheme.CreateAshkar();
            try
            {
                Color32[] forest = theme.BuildColorGrid(forestWorld);
                Color32[] steppe = theme.BuildColorGrid(steppeWorld);

                Color forestMean = TestWorldFactory.Mean(forest);
                Color steppeMean = TestWorldFactory.Mean(steppe);

                // Forest palette is a green canopy (g > r), steppe palette is straw
                // (r > g). That is exactly what makes the two readable.
                Assert.Greater(forestMean.g, forestMean.r,
                    $"forest grass mean ({forestMean}) must read green, not straw");
                Assert.Greater(steppeMean.r, steppeMean.g,
                    $"steppe grass mean ({steppeMean}) must read straw, not green");

                // Steppe is the lighter of the two: forestShadow..forest is much darker
                // than steppeShadow..steppe, so mean luminance must separate them.
                float forestLuma = 0.2126f * forestMean.r + 0.7152f * forestMean.g + 0.0722f * forestMean.b;
                float steppeLuma = 0.2126f * steppeMean.r + 0.7152f * steppeMean.g + 0.0722f * steppeMean.b;
                Assert.Less(forestLuma, steppeLuma,
                    $"forest ({forestLuma:F3}) must be darker than steppe ({steppeLuma:F3})");
                Assert.IsTrue(steppeLuma - forestLuma > 0.05f,
                    $"forest/steppe luminance gap {steppeLuma - forestLuma:F3} is too small to be readable");
            }
            finally
            {
                Object.DestroyImmediate(theme);
            }
        }
    }
}
