using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;
using AI003.Player;
using AI003.World;

namespace AI003.Motor
{
    public struct MotorTag : IComponentData { }

    public struct MotorInput : IComponentData
    {
        public float2 Move;
        public bool Jump;
        public float Speed;
        public float TurnSpeed;
    }

    public struct MotorState : IComponentData
    {
        public float3 Velocity;
        public bool Grounded;
    }

    [BurstCompile]
    public partial struct MotorStepJob : IJobEntity
    {
        public float Dt;
        public float Gravity;
        public float JumpSpeed;
        public float Half;
        [Unity.Collections.ReadOnly] public NativeArray<float> Heights;
        public int Resolution;
        public float CellSize;
        public float SizeMeters;

        public void Execute(ref LocalTransform transform, ref MotorState state, in MotorInput input)
        {
            float3 wish = new float3(input.Move.x, 0f, input.Move.y);
            if (math.lengthsq(wish) > 1f)
            {
                wish = math.normalize(wish);
            }

            float height = TerrainHeightField.SampleHeight(
                Heights, Resolution, CellSize, SizeMeters, transform.Position.x, transform.Position.z);

            state.Velocity.xz = wish.xz * input.Speed;
            if (state.Grounded && input.Jump)
            {
                state.Velocity.y = JumpSpeed;
            }
            if (!state.Grounded || state.Velocity.y > 0f)
            {
                state.Velocity.y += Gravity * Dt;
            }
            else
            {
                state.Velocity.y = -2f;
            }

            float3 next = transform.Position + state.Velocity * Dt;
            if (next.y < height)
            {
                next.y = height;
                state.Velocity.y = 0f;
                state.Grounded = true;
            }
            else
            {
                state.Grounded = false;
            }

            if (math.lengthsq(wish) > 0.0001f)
            {
                quaternion target = quaternion.LookRotationSafe(wish, math.up());
                transform.Rotation = math.slerp(transform.Rotation, target, math.saturate(input.TurnSpeed * Dt));
            }
            transform.Position = next;
        }
    }

    /// <summary>ECS authoritative movement: the entity owns position/velocity, GO is a view.</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class EcsMotorSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            if (!TerrainHeightField.IsValid)
            {
                return;
            }
            new MotorStepJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                Gravity = -24f,
                JumpSpeed = 8f,
                Half = TerrainHeightField.Half,
                Heights = TerrainHeightField.Heights,
                Resolution = TerrainHeightField.Resolution,
                CellSize = TerrainHeightField.CellSize,
                SizeMeters = TerrainHeightField.SizeMeters
            }.ScheduleParallel();
        }
    }

    /// <summary>Opt-in driver: replaces CharacterController movement with the ECS entity.</summary>
    public sealed class EcsMotorDriver : MonoBehaviour
    {
        public float WalkSpeed = 6f;
        public float SprintSpeed = 10f;

        private Entity _entity;
        private CharacterController _controller;
        private ThirdPersonController _legacy;

        private void Start()
        {
            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                enabled = false;
                return;
            }
            EntityManager em = world.EntityManager;
            _entity = em.CreateEntity(typeof(MotorTag), typeof(MotorInput), typeof(MotorState),
                typeof(LocalTransform), typeof(LocalToWorld));
            var local = LocalTransform.FromPositionRotation(transform.position, transform.rotation);
            em.SetComponentData(_entity, local);
            em.SetComponentData(_entity, new MotorInput { Speed = WalkSpeed, TurnSpeed = 14f });
            em.SetComponentData(_entity, new MotorState());
            em.SetComponentData(_entity, new LocalToWorld { Value = float4x4.TRS(transform.position, transform.rotation, 1f) });

            _controller = GetComponent<CharacterController>();
            if (_controller != null) _controller.enabled = false;
            _legacy = GetComponent<ThirdPersonController>();
            if (_legacy != null) _legacy.enabled = false;

            Debug.Log("[AI003][ECS-MOTOR] entity owns movement");
        }

        private void Update()
        {
            if (_entity == Entity.Null) return;
            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            EntityManager em = world.EntityManager;
            if (!em.Exists(_entity)) return;

            var keyboard = Keyboard.current;
            float x = 0f, z = 0f;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed) z += 1f;
                if (keyboard.sKey.isPressed) z -= 1f;
                if (keyboard.dKey.isPressed) x += 1f;
                if (keyboard.aKey.isPressed) x -= 1f;
            }
            Camera camera = Camera.main;
            float3 forward = camera != null ? math.normalize(new float3(camera.transform.forward.x, 0f, camera.transform.forward.z)) : new float3(0, 0, 1);
            float3 right = new float3(forward.z, 0f, -forward.x);
            float3 wish = forward * z + right * x;

            em.SetComponentData(_entity, new MotorInput
            {
                Move = new float2(wish.x, wish.z),
                Jump = keyboard != null && keyboard.spaceKey.wasPressedThisFrame,
                Speed = keyboard != null && keyboard.leftShiftKey.isPressed ? SprintSpeed : WalkSpeed,
                TurnSpeed = 14f
            });
        }

        private void LateUpdate()
        {
            if (_entity == Entity.Null) return;
            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            EntityManager em = world.EntityManager;
            if (!em.Exists(_entity)) return;
            LocalTransform local = em.GetComponentData<LocalTransform>(_entity);
            transform.SetPositionAndRotation(local.Position, local.Rotation);
        }

        private void OnDestroy()
        {
            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (_entity == Entity.Null || world == null || !world.IsCreated) return;
            EntityManager em = world.EntityManager;
            if (em.Exists(_entity)) em.DestroyEntity(_entity);
        }
    }
}
