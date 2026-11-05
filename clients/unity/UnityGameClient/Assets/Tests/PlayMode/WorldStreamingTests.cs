using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using AI003.World;

namespace AI003.Tests
{
    public sealed class WorldStreamingTests
    {
        [UnityTest]
        public IEnumerator TerrainStreamsNearChunksOnly()
        {
            string directory = Path.Combine(Application.streamingAssetsPath, "world");
            WorldData data = WorldData.Load(directory);

            var root = new GameObject("[test] streaming terrain");
            var player = new GameObject("[test] streaming player");
            try
            {
                var terrain = root.AddComponent<WorldTerrain>();
                terrain.Build(data);
                var streaming = root.GetComponent<TerrainStreaming>();
                Assert.IsNotNull(streaming, "play mode should install TerrainStreaming");

                player.transform.position = new Vector3(data.Spawn.x, data.Spawn.y, data.Spawn.z);
                streaming.SetPlayer(player.transform);
                streaming.SetViewDistance(400f);

                float started = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - started < 1.5f)
                {
                    yield return null;
                }

                Debug.Log($"[AI003][WORLD] streaming {WorldStats.Describe()} loaded={streaming.LoadedChunks}");
                Assert.Greater(streaming.LoadedChunks, 0, "no chunk was built");
                Assert.Less(streaming.LoadedChunks, terrain.ChunkCount, "streaming should not build the whole world");
                Assert.Greater(WorldStats.Vertices, 0);
            }
            finally
            {
                Object.Destroy(player);
                Object.Destroy(root);
                WorldStats.Reset();
            }
        }
    }
}
