using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace AI003.World
{
    /// <summary>Marks an ECS entity that mirrors one character bone.</summary>
    public struct BoneTag : IComponentData
    {
        public int Index;
        public quaternion Rest;
    }

    [BurstCompile]
    public partial struct BoneAnimationJob : IJobEntity
    {
        public float Time;

        public void Execute(ref LocalTransform transform, in BoneTag bone)
        {
            float phase = Time * 7f + bone.Index * 0.6f;
            var swing = quaternion.RotateX(math.sin(phase) * 0.35f);
            transform.Rotation = math.mul(swing, bone.Rest);
        }
    }

    /// <summary>ECS-side bone animation: drives the skeleton with a simple run cycle.</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class BoneAnimationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            new BoneAnimationJob { Time = (float)SystemAPI.Time.ElapsedTime }.ScheduleParallel();
        }
    }

    /// <summary>
    /// Hybrid skinning bridge: ECS entities animate the bones (Burst job), and the
    /// resulting rotations are copied back into the GameObject skeleton so the
    /// SkinnedMeshRenderer deforms the mesh. This keeps ECS as the animation
    /// source while Unity's skinning pipeline renders it.
    /// </summary>
    public sealed class DotsBoneRig : MonoBehaviour
    {
        private SkinnedMeshRenderer _skin;
        private Transform[] _bones;
        private Entity[] _entities;
        private bool _ready;

        private void Start()
        {
            _skin = GetComponentInChildren<SkinnedMeshRenderer>();
            if (_skin == null)
            {
                enabled = false;
                return;
            }
            _bones = _skin.bones;
            if (_bones == null || _bones.Length == 0)
            {
                enabled = false;
                return;
            }

            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                enabled = false;
                return;
            }

            Animator animator = GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.enabled = false;
            }

            EntityManager em = world.EntityManager;
            _entities = new Entity[_bones.Length];
            for (int i = 0; i < _bones.Length; i++)
            {
                Entity entity = em.CreateEntity(typeof(LocalTransform), typeof(BoneTag));
                em.SetComponentData(entity, LocalTransform.FromPositionRotation(
                    _bones[i].position, _bones[i].localRotation));
                em.SetComponentData(entity, new BoneTag { Index = i, Rest = _bones[i].localRotation });
                _entities[i] = entity;
            }
            _ready = true;
            Debug.Log($"[AI003][DOTS-BONES] driving {_bones.Length} bones from ECS");
        }

        private void LateUpdate()
        {
            if (!_ready)
            {
                return;
            }
            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return;
            }
            EntityManager em = world.EntityManager;
            for (int i = 0; i < _entities.Length; i++)
            {
                if (!em.Exists(_entities[i]))
                {
                    continue;
                }
                _bones[i].localRotation = em.GetComponentData<LocalTransform>(_entities[i]).Rotation;
            }
        }

        private void OnDestroy()
        {
            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (_entities == null || world == null || !world.IsCreated)
            {
                return;
            }
            EntityManager em = world.EntityManager;
            for (int i = 0; i < _entities.Length; i++)
            {
                if (em.Exists(_entities[i]))
                {
                    em.DestroyEntity(_entities[i]);
                }
            }
        }
    }
}
