using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AI003.Ecs
{
    /// <summary>
    /// Manual benchmark (not auto-created): fills the world with remote fighters
    /// and compares the Burst IJobEntity against an equivalent managed loop.
    /// Used by the PlayMode test to report a concrete number.
    /// </summary>
    [DisableAutoCreation]
    public partial class FighterBenchmarkSystem : SystemBase
    {
        public int EntityCount = 4000;
        public int Iterations = 10;
        public bool Done;
        public double JobMs;
        public double ManagedMs;

        protected override void OnUpdate()
        {
            if (Done) return;
            Done = true;

            EntityManager em = EntityManager;
            var archetype = em.CreateArchetype(
                typeof(FighterState), typeof(RemoteFighterTag), typeof(FighterAnim));
            var entities = new NativeArray<Entity>(EntityCount, Allocator.Temp);
            em.CreateEntity(archetype, entities);

            try
            {
                for (int i = 0; i < EntityCount; i++)
                {
                    em.SetComponentData(entities[i], new FighterState
                    {
                        Remote = true,
                        Alive = true,
                        Position = new float3(i, 0f, 0f),
                        TargetPosition = new float3(i + 10f, 0f, 5f),
                        TargetYaw = 90f,
                        LungeDuration = 0.18f
                    });
                }

                var query = em.CreateEntityQuery(typeof(FighterState), typeof(RemoteFighterTag));

                // Warm up Burst so compilation is excluded from the measurement.
                new FighterStateUpdateJob { DeltaTime = 1f / 60f }
                    .Schedule(Dependency).Complete();

                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < Iterations; i++)
                {
                    new FighterStateUpdateJob { DeltaTime = 1f / 60f }
                        .Schedule(Dependency).Complete();
                }
                JobMs = stopwatch.Elapsed.TotalMilliseconds / Iterations;

                stopwatch.Restart();
                for (int i = 0; i < Iterations; i++)
                {
                    var states = query.ToComponentDataArray<FighterState>(Allocator.Temp);
                    for (int j = 0; j < states.Length; j++)
                    {
                        FighterState state = states[j];
                        state.Position = math.lerp(state.Position, state.TargetPosition, 0.2f);
                        float yawDelta = state.TargetYaw - state.Yaw;
                        yawDelta -= 360f * math.round(yawDelta / 360f);
                        state.Yaw += yawDelta * 0.2f;
                        state.Collapse = math.max(0f, state.Collapse - 0.01f);
                        states[j] = state;
                    }
                    query.CopyFromComponentDataArray(states);
                    states.Dispose();
                }
                ManagedMs = stopwatch.Elapsed.TotalMilliseconds / Iterations;
            }
            finally
            {
                em.DestroyEntity(entities);
                entities.Dispose();
            }
        }
    }
}
