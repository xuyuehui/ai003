using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AI003.Ecs
{
    /// <summary>
    /// Applies the Burst-updated fighter state to managed presentation objects:
    /// attack lunge, hit colour, death rotation and animation parameters.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(FighterStateUpdateSystem))]
    public partial struct FighterViewSystem : ISystem
    {
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
            MaterialPropertyBlock block = Block;

            foreach (var (fighter, anim, view) in SystemAPI
                         .Query<RefRO<FighterState>, RefRO<FighterAnim>, RefRO<FighterView>>())
            {
                Apply(view.ValueRO, fighter.ValueRO, anim.ValueRO, block);
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
