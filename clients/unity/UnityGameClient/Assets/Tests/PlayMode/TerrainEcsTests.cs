using System.Collections;
using System.IO;
using AI003.World;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    /// <summary>ECS terrain collision (height-field clamp) for tagged bodies.</summary>
    public sealed class TerrainEcsTests
    {
        [UnityTest]
        public IEnumerator TerrainBodyIsClampedToHeightField()
        {
            WorldData data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            TerrainHeightField.Register(data.Heights, data.Resolution, data.CellSize, data.SizeMeters);

            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            EntityManager em = world.EntityManager;
            Entity body = em.CreateEntity(typeof(LocalTransform), typeof(TerrainBody));
            em.SetComponentData(body, LocalTransform.FromPosition(new Vector3(data.Spawn.x, -500f, data.Spawn.z)));
            em.SetComponentData(body, new TerrainBody { Radius = 0f });

            try
            {
                yield return null;
                yield return null;

                float y = em.GetComponentData<LocalTransform>(body).Position.y;
                Assert.GreaterOrEqual(y, data.Spawn.y - 2f, "body should rest on the terrain height");
            }
            finally
            {
                if (em.Exists(body))
                {
                    em.DestroyEntity(body);
                }
                TerrainHeightField.Unregister();
            }
        }
    }
}
