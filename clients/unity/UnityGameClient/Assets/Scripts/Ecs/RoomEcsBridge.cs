using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace AI003.Ecs
{
    /// <summary>
    /// Bridges the WebSocket layer (MonoBehaviour) into the ECS world. The network
    /// code keeps parsing protocol messages; fighters and their presentation live
    /// as entities and are driven by <see cref="FighterViewSystem"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoomEcsBridge : MonoBehaviour
    {
        public static RoomEcsBridge Instance { get; private set; }

        private EntityManager _entityManager;
        private Entity _local = Entity.Null;
        private readonly Dictionary<string, Entity> _remotes = new Dictionary<string, Entity>();

        public static RoomEcsBridge EnsureExists()
        {
            if (Instance != null) return Instance;
            var existing = FindFirstObjectByType<RoomEcsBridge>();
            if (existing != null) return existing;
            var go = new GameObject("[AI003] ECS Bridge");
            DontDestroyOnLoad(go);
            return go.AddComponent<RoomEcsBridge>();
        }

        private void Awake()
        {
            Instance = this;
            _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        }

        private EntityManager Em
        {
            get
            {
                if (!_entityManager.World.IsCreated)
                {
                    _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
                }
                return _entityManager;
            }
        }

        public void EnsureLocal(Transform transform, Animator animator, Renderer renderer)
        {
            if (_local != Entity.Null && Em.Exists(_local))
            {
                Em.SetComponentData(_local, new FighterView
                {
                    Transform = transform,
                    Animator = animator,
                    Renderer = renderer
                });
                return;
            }

            _local = Em.CreateEntity();
            Em.AddComponentData(_local, new LocalFighterTag());
            Em.AddComponentData(_local, new FighterState
            {
                Alive = true,
                LungeDuration = 0.18f,
                Yaw = 180f,
                TargetYaw = 180f
            });
            Em.AddComponentData(_local, new FighterAnim { Speed = 0f, Grounded = true, PlayRate = 1f });
            Em.AddComponentData(_local, new FighterView
            {
                Transform = transform,
                Animator = animator,
                Renderer = renderer
            });
        }

        public void SetLocal(Vector3 position, float yaw, float animSpeed, bool alive)
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return;
            var state = Em.GetComponentData<FighterState>(_local);
            state.Position = position;
            state.TargetPosition = position;
            state.Yaw = yaw;
            state.TargetYaw = yaw;
            state.Alive = alive;
            Em.SetComponentData(_local, state);

            var anim = Em.GetComponentData<FighterAnim>(_local);
            anim.Speed = animSpeed;
            anim.Grounded = true;
            anim.PlayRate = 1f;
            Em.SetComponentData(_local, anim);
        }

        public void UpsertRemote(string key, Vector3 position, float yaw, bool alive,
            float animSpeed, float playRate, Transform transform, Animator animator, Renderer renderer)
        {
            if (!_remotes.TryGetValue(key, out Entity entity) || !Em.Exists(entity))
            {
                entity = Em.CreateEntity();
                Em.AddComponentData(entity, new RemoteFighterTag { Key = key.GetHashCode() });
                Em.AddComponentData(entity, new FighterState
                {
                    Alive = alive,
                    Remote = true,
                    Position = position,
                    TargetPosition = position,
                    Yaw = yaw,
                    TargetYaw = yaw,
                    LungeDuration = 0.18f
                });
                Em.AddComponentData(entity, new FighterAnim { Speed = animSpeed, Grounded = true, PlayRate = playRate });
                Em.AddComponentData(entity, new FighterView
                {
                    Transform = transform,
                    Animator = animator,
                    Renderer = renderer
                });
                _remotes[key] = entity;
                return;
            }

            var state = Em.GetComponentData<FighterState>(entity);
            state.TargetPosition = position;
            state.TargetYaw = yaw;
            state.Alive = alive;
            Em.SetComponentData(entity, state);

            var anim = Em.GetComponentData<FighterAnim>(entity);
            anim.Speed = animSpeed;
            anim.PlayRate = playRate;
            Em.SetComponentData(entity, anim);

            Em.SetComponentData(entity, new FighterView
            {
                Transform = transform,
                Animator = animator,
                Renderer = renderer
            });
        }

        public void RemoveRemote(string key)
        {
            if (!_remotes.TryGetValue(key, out Entity entity)) return;
            if (Em.Exists(entity)) Em.DestroyEntity(entity);
            _remotes.Remove(key);
        }

        public void TriggerAttack(string key)
        {
            Entity entity = Resolve(key);
            if (entity == Entity.Null) return;
            var state = Em.GetComponentData<FighterState>(entity);
            state.LungeRemaining = 0.18f;
            state.LungeDuration = 0.18f;
            Em.SetComponentData(entity, state);
        }

        public void TriggerHit(string key)
        {
            Entity entity = Resolve(key);
            if (entity == Entity.Null) return;
            var state = Em.GetComponentData<FighterState>(entity);
            state.HitRemaining = 0.15f;
            Em.SetComponentData(entity, state);
        }

        public void SetRemoteAlive(string key, bool alive)
        {
            Entity entity = Resolve(key);
            if (entity == Entity.Null) return;
            var state = Em.GetComponentData<FighterState>(entity);
            state.Alive = alive;
            Em.SetComponentData(entity, state);
        }

        public void Clear()
        {
            if (_local != Entity.Null && Em.Exists(_local)) Em.DestroyEntity(_local);
            _local = Entity.Null;
            foreach (Entity entity in _remotes.Values)
            {
                if (Em.Exists(entity)) Em.DestroyEntity(entity);
            }
            _remotes.Clear();
        }

        private Entity Resolve(string key)
        {
            if (!_remotes.TryGetValue(key, out Entity entity) || !Em.Exists(entity)) return Entity.Null;
            return entity;
        }
    }
}
