using System;
using System.Globalization;
using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Env-var contract for the pinned stills: AI003_STILL_&lt;SHOT&gt;_&lt;FIELD&gt;.
    /// These are pure string/parse helpers so EditMode tests can pin the naming and
    /// the malformed-input behaviour without an Editor or a scene.
    /// </summary>
    public static class StillOverrides
    {
        /// <summary>AI003_STILL_DESERT_SITE, AI003_STILL_SNOW_PITCH, …</summary>
        public static string Variable(string shot, string field)
        {
            return "AI003_STILL_" + (shot ?? string.Empty).ToUpperInvariant() + "_" +
                   (field ?? string.Empty).ToUpperInvariant();
        }

        /// <summary>
        /// Invariant float. Every rejection path leaves <paramref name="value"/> at 0 —
        /// notably "NaN"/"Infinity", which float.TryParse accepts but must not be handed
        /// back to the caller.
        /// </summary>
        public static bool TryParseFloat(string raw, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
            if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                return false;
            }
            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
            {
                return false;
            }
            value = parsed;
            return true;
        }

        /// <summary>Exactly "x,z" in invariant float; anything else is rejected.</summary>
        public static bool TryParsePoint(string raw, out Vector2 point)
        {
            point = Vector2.zero;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
            string[] parts = raw.Split(',');
            if (parts.Length != 2)
            {
                return false;
            }
            if (!TryParseFloat(parts[0], out float x) || !TryParseFloat(parts[1], out float z))
            {
                return false;
            }
            point = new Vector2(x, z);
            return true;
        }

        public static bool TryValue(string shot, string field, out float value)
        {
            return TryParseFloat(Environment.GetEnvironmentVariable(Variable(shot, field)), out value);
        }

        public static bool TrySite(string shot, out Vector2 site)
        {
            return TryParsePoint(Environment.GetEnvironmentVariable(Variable(shot, "SITE")), out site);
        }
    }
}
