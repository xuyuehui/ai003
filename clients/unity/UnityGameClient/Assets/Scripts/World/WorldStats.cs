using System.Text;
using UnityEngine;

namespace AI003.World
{
    /// <summary>Runtime terrain streaming counters (chunks/vertices/build cost).</summary>
    public static class WorldStats
    {
        public static int TotalChunks;
        public static int LoadedChunks;
        public static int Vertices;
        public static int Colliders;
        public static float LastRefreshMs;
        public static float TotalBuildMs;

        public static void Reset()
        {
            TotalChunks = 0;
            LoadedChunks = 0;
            Vertices = 0;
            Colliders = 0;
            LastRefreshMs = 0f;
            TotalBuildMs = 0f;
        }

        public static string Describe()
        {
            var builder = new StringBuilder(160);
            builder.Append("chunks ").Append(LoadedChunks).Append('/').Append(TotalChunks);
            builder.Append("  verts ").Append(Vertices);
            builder.Append("  colliders ").Append(Colliders);
            builder.Append("  refresh ").Append(LastRefreshMs.ToString("F2")).Append(" ms");
            builder.Append("  build ").Append(TotalBuildMs.ToString("F1")).Append(" ms");
            return builder.ToString();
        }
    }
}
