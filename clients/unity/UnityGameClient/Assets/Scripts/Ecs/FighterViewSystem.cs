using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AI003.Ecs
{
    /// <summary>
    /// Applies fighter state to the managed presentation objects: local position,
    /// remote smoothing, attack lunge, hit pulse, death collapse and animation.
    /// This is the ECS replacement for the old per-avatar Update loops.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct FighterViewSystem : ISystem
    {
        private const float CollapseSeconds = 0.25f;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FighterState>();
        }

        public void OnDestroy(ref SystemState state)
        {
        }

        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f) deltaTime = 1f / 60f;
            MaterialPropertyBlock block = Block;

            foreach (var (fighter, anim, view) in SystemAPI
                         .Query<RefRW<FighterState>, RefRW<FighterAnim>, RefRO<FighterView>>())
            {
                ref FighterState fighterState = ref fighter.ValueRW;
                fighterState.LungeRemaining = math.max(0f, fighterState.LungeRemaining - deltaTime);
                fighterState.HitRemaining = math.max(0f, fighterState.HitRemaining - deltaTime);

                if (fighterState.Remote)
                {
                    float t = 1f - math.exp(-12f * deltaTime);
                    fighterState.Position = math.lerp(fighterState.Position, fighterState.TargetPosition, t);

                    float yawDelta = fighterState.TargetYaw - fighterState.Yaw;
                    yawDelta -= 360f * math.round(yawDelta / 360f);
                    fighterState.Yaw += yawDelta * t;
                }

                fighterState.Collapse = fighterState.Alive
                    ? math.max(0f, fighterState.Collapse - deltaTime / CollapseSeconds)
                    : math.min(1f, fighterState.Collapse + deltaTime / CollapseSeconds);

                Apply(view.ValueRO, fighterState, anim.ValueRO, block);
            }
        }

        private static void Apply(in FighterView view, in FighterState state, in FighterAnim anim,
            MaterialPropertyBlock block)
        {
            Transform transform = view.Transform.Value;
            if (transform == null) return;

            float3 position = state.Position;
            if (state.LungeRemaining > 0f && state.LungeDuration > 0f)
            {
                float progress = 1f - state.LungeRemaining / state.LungeDuration;
                float offset = math.sin(progress * math.PI) * 0.35f;
                float radians = math.radians(state.Yaw);
                position += new float3(math.sin(radians), 0f, math.cos(radians)) * offset;
            }

            transform.position = position;
            transform.rotation = Quaternion.Euler(90f * state.Collapse, state.Yaw, 0f);

            if (view.Animator.Value != null)
            {
                Animator animator = view.Animator.Value;
                animator.SetFloat("Speed", state.Alive ? anim.Speed : 0f);
                animator.SetBool("Grounded", anim.Grounded);
                animator.speed = state.Alive ? anim.PlayRate : 0f;
            }

            if (state.Remote && view.Renderer.Value != null)
            {
                Color color = Color.white;
                if (!state.Alive) color = new Color(0.35f, 0.35f, 0.35f, 1f);
                else if (state.HitRemaining > 0f) color = new Color(1f, 0.28f, 0.22f, 1f);

                block.SetColor(BaseColorId, color);
                view.Renderer.Value.SetPropertyBlock(block);
            }
        }
    }
}
