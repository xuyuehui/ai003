using NUnit.Framework;
using UnityEngine;
using AI003.World;

namespace AI003.Tests.EditMode
{
    /// <summary>
    /// Pins the pinned-still env contract: <c>AI003_STILL_&lt;SHOT&gt;_&lt;FIELD&gt;</c>
    /// for the six terrain frames, and the rule that malformed values must fall back
    /// (return false) instead of throwing or silently writing NaN into the camera.
    /// </summary>
    public class StillOverrideTests
    {
        private static readonly string[] Shots =
        {
            "desert", "ocean", "forest", "steppe", "snow", "mountain",
        };

        private static readonly string[] Fields =
        {
            "SITE", "YAW", "PITCH", "FOV", "DIST",
        };

        [Test]
        public void VariableNamesCoverSixShotsTimesFiveFields()
        {
            foreach (string shot in Shots)
            {
                foreach (string field in Fields)
                {
                    string name = StillOverrides.Variable(shot, field);
                    Assert.AreEqual(
                        "AI003_STILL_" + shot.ToUpperInvariant() + "_" + field.ToUpperInvariant(),
                        name);
                }
            }

            // Spot-check the exact strings the author menu reads.
            Assert.AreEqual("AI003_STILL_DESERT_SITE", StillOverrides.Variable("desert", "SITE"));
            Assert.AreEqual("AI003_STILL_SNOW_PITCH", StillOverrides.Variable("snow", "PITCH"));
            Assert.AreEqual("AI003_STILL_MOUNTAIN_FOV", StillOverrides.Variable("mountain", "fov"));
        }

        [Test]
        public void VariableNamesAreCaseInsensitiveAndTotal()
        {
            Assert.AreEqual(StillOverrides.Variable("forest", "yaw"), StillOverrides.Variable("Forest", "Yaw"));
            Assert.AreEqual(StillOverrides.Variable("forest", "yaw"), StillOverrides.Variable("FOREST", "YAW"));

            // Nulls must not throw; they degrade to the empty segment.
            Assert.DoesNotThrow(() => StillOverrides.Variable(null, "FOV"));
            Assert.DoesNotThrow(() => StillOverrides.Variable("snow", null));
            Assert.AreEqual("AI003_STILL__FOV", StillOverrides.Variable(null, "FOV"));
        }

        [Test]
        public void EveryShotAndFieldProducesAUniqueVariable()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (string shot in Shots)
            {
                foreach (string field in Fields)
                {
                    string name = StillOverrides.Variable(shot, field);
                    Assert.IsTrue(seen.Add(name), $"duplicate env var name {name}");
                }
            }
            Assert.AreEqual(Shots.Length * Fields.Length, seen.Count);
        }

        [Test]
        public void FloatParsingAcceptsTheValuesTheReportUses()
        {
            var cases = new (string raw, float expected)[]
            {
                ("40", 40f),
                ("44", 44f),
                ("46", 46f),
                ("0", 0f),
                ("-12", -12f),
                ("-4", -4f),
                ("76", 76f),
                ("230", 230f),
                ("236", 236f),
                ("210", 210f),
                ("355", 355f),
                ("-592", -592f),
                ("290.1", 290.1f),
                (" 12.5 ", 12.5f),
                ("1e3", 1000f),
                ("-0.15", -0.15f),
            };

            foreach ((string raw, float expected) in cases)
            {
                Assert.IsTrue(StillOverrides.TryParseFloat(raw, out float value), $"'{raw}' should parse");
                Assert.AreEqual(expected, value, 1e-4f, $"'{raw}'");
            }
        }

        [Test]
        public void FloatParsingRejectsMalformedAndNonFiniteValues()
        {
            foreach (string raw in new[]
                     {
                         null, "", "   ", "abc", "40m", "12,5", "NaN", "nan", "Infinity", "-Infinity",
                         "1.2.3", "--4", "0x10",
                     })
            {
                Assert.IsFalse(StillOverrides.TryParseFloat(raw, out float value),
                    $"'{raw ?? "<null>"}' must be rejected so the frame falls back to its baked value");
                Assert.AreEqual(0f, value, 0f, $"'{raw ?? "<null>"}' must not leak a partial value");
            }
        }

        [Test]
        public void PointParsingAcceptsExactlyTwoInvariantFloats()
        {
            Assert.IsTrue(StillOverrides.TryParsePoint("-592,248", out Vector2 desert));
            Assert.AreEqual(-592f, desert.x, 1e-4f);
            Assert.AreEqual(248f, desert.y, 1e-4f);

            Assert.IsTrue(StillOverrides.TryParsePoint(" 115 , -409 ", out Vector2 mountain));
            Assert.AreEqual(115f, mountain.x, 1e-4f);
            Assert.AreEqual(-409f, mountain.y, 1e-4f);

            Assert.IsTrue(StillOverrides.TryParsePoint("290.1,-0.5", out Vector2 decimals));
            Assert.AreEqual(290.1f, decimals.x, 1e-4f);
            Assert.AreEqual(-0.5f, decimals.y, 1e-4f);
        }

        [Test]
        public void PointParsingRejectsAnythingElse()
        {
            foreach (string raw in new[]
                     {
                         null, "", "1", "1,2,3", "a,b", "1,", ",2", "1,NaN", "12.5;248", "1,2)",
                     })
            {
                Assert.IsFalse(StillOverrides.TryParsePoint(raw, out Vector2 point),
                    $"'{raw ?? "<null>"}' must be rejected");
                Assert.AreEqual(0f, point.x, 0f, $"'{raw ?? "<null>"}' left an x component");
                Assert.AreEqual(0f, point.y, 0f, $"'{raw ?? "<null>"}' left a z component");
            }
        }

        // The env round-trip uses a probe shot name so a live author-menu run with the
        // six real pinned variables is never disturbed.
        private const string ProbeShot = "editmodeprobe";

        [TearDown]
        public void ClearProbeVariables()
        {
            foreach (string field in Fields)
            {
                System.Environment.SetEnvironmentVariable(StillOverrides.Variable(ProbeShot, field), null);
            }
        }

        [Test]
        public void EnvironmentRoundTripReadsTheNamedVariable()
        {
            Assert.IsFalse(StillOverrides.TryValue(ProbeShot, "FOV", out _), "unset must fall back");

            System.Environment.SetEnvironmentVariable(StillOverrides.Variable(ProbeShot, "FOV"), "55.5");
            Assert.IsTrue(StillOverrides.TryValue(ProbeShot, "FOV", out float fov));
            Assert.AreEqual(55.5f, fov, 1e-4f);

            System.Environment.SetEnvironmentVariable(StillOverrides.Variable(ProbeShot, "PITCH"), "-12");
            Assert.IsTrue(StillOverrides.TryValue(ProbeShot, "PITCH", out float pitch));
            Assert.AreEqual(-12f, pitch, 1e-4f);

            System.Environment.SetEnvironmentVariable(StillOverrides.Variable(ProbeShot, "SITE"), "10,-20");
            Assert.IsTrue(StillOverrides.TrySite(ProbeShot, out Vector2 site));
            Assert.AreEqual(10f, site.x, 1e-4f);
            Assert.AreEqual(-20f, site.y, 1e-4f);

            // A malformed value must read as "not set" so the baked frame survives.
            System.Environment.SetEnvironmentVariable(StillOverrides.Variable(ProbeShot, "FOV"), "wide");
            Assert.IsFalse(StillOverrides.TryValue(ProbeShot, "FOV", out _));

            System.Environment.SetEnvironmentVariable(StillOverrides.Variable(ProbeShot, "SITE"), "1,2,3");
            Assert.IsFalse(StillOverrides.TrySite(ProbeShot, out _));
        }

        [Test]
        public void FieldsOfDifferentShotsDoNotLeak()
        {
            System.Environment.SetEnvironmentVariable(StillOverrides.Variable(ProbeShot, "FOV"), "60");

            // The probe's FOV is set; the real shots' FOVs were never written by this test.
            Assert.IsTrue(StillOverrides.TryValue(ProbeShot, "FOV", out float probe));
            Assert.AreEqual(60f, probe, 1e-4f);
            Assert.IsFalse(StillOverrides.TryValue(ProbeShot, "DIST", out _), "DIST was never set");
        }
    }
}
