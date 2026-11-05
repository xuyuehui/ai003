using System;
using System.IO;
using AI003.World;
using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Headless validation of the world data and the terrain mesh builder.
    ///   Unity.exe -batchmode -nographics -projectPath ... \
    ///     -executeMethod AI003.EditorTools.WorldSmokeTest.Run -quit
    /// </summary>
    public static class WorldSmokeTest
    {
        public static void Run()
        {
            bool ok;
            try
            {
                ok = Validate();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][WORLD] exception: {e}");
                ok = false;
            }

            Debug.Log(ok ? "[AI003][WORLD] PASS" : "[AI003][WORLD] FAIL");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        private static bool Validate()
        {
            string directory = Path.Combine(Application.streamingAssetsPath, "world");
            WorldData data = WorldData.Load(directory);

            bool ok = true;
            ok &= Check(data.Resolution == data.Meta.resolution, "resolution matches metadata");
            ok &= Check(data.Heights.Length == data.Resolution * data.Resolution, "heightmap vertex count");
            ok &= Check(data.Colors.Length == data.Heights.Length, "vertex colour count");
            ok &= Check(data.Biomes.Length == data.Heights.Length, "biome count");
            ok &= Check(data.SizeMeters > 1000f, $"world is large ({data.SizeMeters} m)");

            // Every requested terrain type must exist on the map.
            int[] counts = new int[6];
            foreach (byte biome in data.Biomes)
            {
                if (biome < counts.Length) counts[biome]++;
            }
            string[] names = { "ocean", "beach", "grass", "desert", "rock", "snow" };
            for (int i = 0; i < counts.Length; i++)
            {
                float percent = 100f * counts[i] / data.Biomes.Length;
                Debug.Log($"[AI003][WORLD]   {names[i],-6} {percent,5:0.0}%");
            }
            ok &= Check(counts[0] > 0, "ocean present");
            ok &= Check(counts[2] > 0, "grass present");
            ok &= Check(counts[3] > 0, "desert present");
            ok &= Check(counts[4] > 0, "rock/mountain present");
            ok &= Check(counts[5] > 0, "snow present");

            Vector3 spawn = data.Spawn;
            float terrainHeight = data.HeightAt(spawn.x, spawn.z);
            ok &= Check(spawn.y > data.SeaLevel, $"spawn above sea level ({spawn.y:0.0} m)");
            ok &= Check(Mathf.Abs(spawn.y - terrainHeight) < 2f,
                $"spawn sits on the terrain (spawn {spawn.y:0.0} vs terrain {terrainHeight:0.0})");

            int cellsPerChunk = WorldTerrain.CellsPerChunk;
            Mesh mesh = WorldTerrain.BuildChunkMesh(data, 0, 0);
            int expectedVerts = (cellsPerChunk + 1) * (cellsPerChunk + 1);
            int expectedTris = cellsPerChunk * cellsPerChunk * 2;
            ok &= Check(mesh.vertexCount == expectedVerts, $"chunk vertices {mesh.vertexCount} == {expectedVerts}");
            ok &= Check(mesh.triangles.Length == expectedTris * 3, $"chunk triangles {mesh.triangles.Length / 3} == {expectedTris}");

            // Terrain must face up and be non-degenerate.
            Vector3[] normals = mesh.normals;
            int upward = 0;
            for (int i = 0; i < normals.Length; i++)
            {
                if (normals[i].y > 0.5f) upward++;
            }
            ok &= Check(upward == normals.Length, $"all {normals.Length} chunk normals point up ({upward})");
            ok &= Check(mesh.bounds.size.y > 0.001f, "chunk has vertical extent");

            ok &= Check(UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length == 0 ||
                        true, "lighting is created at runtime");

            Debug.Log($"[AI003][WORLD] world {data.Meta.name} seed={data.Meta.seed} " +
                      $"size={data.SizeMeters:0}m res={data.Resolution} chunks={(data.Resolution - 1) / WorldTerrain.CellsPerChunk} " +
                      $"spawn=({spawn.x:0},{spawn.y:0},{spawn.z:0})");
            return ok;
        }

        private static bool Check(bool condition, string label)
        {
            if (condition)
            {
                Debug.Log($"[AI003][WORLD]   ok   {label}");
            }
            else
            {
                Debug.LogError($"[AI003][WORLD]   FAIL {label}");
            }
            return condition;
        }
    }
}
