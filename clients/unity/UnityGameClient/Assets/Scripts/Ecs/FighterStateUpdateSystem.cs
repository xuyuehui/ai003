using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace AI003.Ecs
{
    /// <summary>
    /// Burst-compiled state update for every fighter: timers, remote smoothing and
    /// death collapse. Presentation (Transform/Animator) stays in the managed
    /// system, which runs after this one.
    /// </summary>
    [BurstCompile]
    public partial struct FighterStateUpdateJob : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref FighterState state)
        {
            state.LungeRemaining = math.max(0f, state.LungeRemaining - DeltaTime);
            state.HitRemaining = math.max(0f, state.HitRemaining - DeltaTime);

            if (state.Remote)
            {
                float t = 1f - math.exp(-12f * DeltaTime);
                state.Position = math.lerp(state.Position, state.TargetPosition, t);

                float yawDelta = state.TargetYaw - state.Yaw;
                yawDelta -= 360f * math.round(yawDelta / 360f);
                state.Yaw += yawDelta * t;
            }

            state.Collapse = state.Alive
                ? math.max(0f, state.Collapse - DeltaTime / 0.25f)
                : math.min(1f, state.Collapse + DeltaTime / 0.25f);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct FighterStateUpdateSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = math.min(SystemAPI.Time.DeltaTime, 0.2f);
            if (deltaTime <= 0f) return;

            state.Dependency = new FighterStateUpdateJob
            {
                DeltaTime = deltaTime
            }.Schedule(state.Dependency);
        }
    }
}
