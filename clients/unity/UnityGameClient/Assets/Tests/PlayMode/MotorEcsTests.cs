using System.Collections;
using System.IO;
using AI003.Motor;
using AI003.World;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    public sealed class MotorEcsTests
    {
        [UnityTest]
        public IEnumerator EntityMovesAndStaysOnTerrain()
        {
            WorldData data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            TerrainHeightField.Register(data.Heights, data.Resolution, data.CellSize, data.SizeMeters);

            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            EntityManager em = world.EntityManager;
            Entity entity = em.CreateEntity(typeof(MotorTag), typeof(MotorInput), typeof(MotorState),
                typeof(LocalTransform), typeof(LocalToWorld));
            Vector3 start = data.Spawn + new Vector3(0f, 3f, 0f);
            em.SetComponentData(entity, LocalTransform.FromPositionRotation(start, Quaternion.identity));
            em.SetComponentData(entity, new MotorInput { Move = new Unity.Mathematics.float2(0f, 1f), Speed = 6f, TurnSpeed = 14f });
            em.SetComponentData(entity, new MotorState());

            try
            {
                for (int i = 0; i < 10; i++)
                {
                    yield return null;
                }
                LocalTransform transform = em.GetComponentData<LocalTransform>(entity);
                Assert.Greater(transform.Position.z, start.z + 0.05f, "entity should move forward");
                float height = TerrainHeightField.Sample(transform.Position.x, transform.Position.z);
                Assert.GreaterOrEqual(transform.Position.y, height - 0.5f, "entity should stay on terrain");
            }
            finally
            {
                if (em.Exists(entity)) em.DestroyEntity(entity);
                TerrainHeightField.Unregister();
            }
        }
    }
}