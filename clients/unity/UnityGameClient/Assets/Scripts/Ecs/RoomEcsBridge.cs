using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
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

        public int LocalTick => LocalPrediction().Tick;
        public int ServerTick => LocalSnapshot().Tick;

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
                Position = transform.position,
                TargetPosition = transform.position,
                Yaw = transform.eulerAngles.y,
                TargetYaw = transform.eulerAngles.y
            });
            Em.AddComponentData(_local, new FighterAnim { Speed = 0f, Grounded = true, PlayRate = 1f });
            Em.AddComponentData(_local, new FighterView
            {
                Transform = transform,
                Animator = animator,
                Renderer = renderer
            });
            Em.AddComponentData(_local, new PredictionState
            {
                PositionMm = new int2(
                    (int)System.Math.Round(transform.position.x * 1000f),
                    (int)System.Math.Round(transform.position.z * 1000f)),
                SpeedMm = 250,
                DiagonalMm = 177,
                HalfExtentMm = 20000
            });
            Em.AddComponentData(_local, new PredictionInput());
            Em.AddComponentData(_local, new ServerSnapshot());
            Em.AddBuffer<PredictionStep>(_local);
            Em.AddBuffer<PredictionHistoryElement>(_local);
            Em.AddBuffer<SeparationBody>(_local);
        }

        /// <summary>Stores the latest authoritative body positions for separation prediction.</summary>
        public void SetSeparationBodies(System.Collections.Generic.IReadOnlyList<(int x, int z, bool alive)> bodies)
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return;
            var buffer = Em.GetBuffer<SeparationBody>(_local);
            buffer.Clear();
            for (int i = 0; i < bodies.Count; i++)
            {
                buffer.Add(new SeparationBody
                {
                    PositionMm = new int2(bodies[i].x, bodies[i].z),
                    Alive = bodies[i].alive
                });
            }
        }

        public void SetLocal(Vector3 position, float yaw, float animSpeed, bool alive)
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return;
            var prediction = Em.GetComponentData<PredictionState>(_local);
            prediction.PositionMm = new int2(
                (int)System.Math.Round(position.x * 1000f),
                (int)System.Math.Round(position.z * 1000f));
            float radians = yaw * Mathf.Deg2Rad;
            prediction.FacingX = Mathf.RoundToInt(Mathf.Sin(radians));
            prediction.FacingZ = Mathf.RoundToInt(Mathf.Cos(radians));
            Em.SetComponentData(_local, prediction);

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

        /// <summary>Sets the arena bounds and per-tick speeds used by the prediction system.</summary>
        public void ConfigureLocal(int centerXmm, int centerZmm, int halfExtentMm, int speedMm, int diagonalMm)
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return;
            var prediction = Em.GetComponentData<PredictionState>(_local);
            prediction.CenterMm = new int2(centerXmm, centerZmm);
            prediction.HalfExtentMm = halfExtentMm;
            prediction.SpeedMm = speedMm;
            prediction.DiagonalMm = diagonalMm;
            Em.SetComponentData(_local, prediction);
        }

        /// <summary>Feeds the current command; the system consumes it on the next 20 Hz step.</summary>
        public void SetInput(int dx, int dz, bool attack)
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return;
            Em.SetComponentData(_local, new PredictionInput { DX = dx, DZ = dz, Attack = attack });
        }

        /// <summary>Queues an authoritative snapshot for reconciliation.</summary>
        public void SetServerSnapshot(int xMm, int zMm, int tick)
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return;
            Em.SetComponentData(_local, new ServerSnapshot
            {
                PositionMm = new int2(xMm, zMm),
                Tick = tick,
                Pending = true
            });
        }

        /// <summary>Pops the oldest unsent predicted tick for the network layer.</summary>
        public bool TryTakeStep(out int tick, out int dx, out int dz, out bool attack)
        {
            tick = 0;
            dx = 0;
            dz = 0;
            attack = false;
            if (_local == Entity.Null || !Em.Exists(_local)) return false;

            var steps = Em.GetBuffer<PredictionStep>(_local);
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i].Sent) continue;
                PredictionStep step = steps[i];
                step.Sent = true;
                steps[i] = step;
                tick = step.Tick;
                dx = step.DX;
                dz = step.DZ;
                attack = step.Attack;
                return true;
            }
            return false;
        }

        public Vector3 GetLocalPositionMeters()
        {
            PredictionState prediction = LocalPrediction();
            return new Vector3(prediction.PositionMm.x / 1000f, 0f, prediction.PositionMm.y / 1000f);
        }

        public void GetLocalFacing(out int facingX, out int facingZ)
        {
            PredictionState prediction = LocalPrediction();
            facingX = prediction.FacingX;
            facingZ = prediction.FacingZ;
        }

        public int Corrections => LocalPrediction().Corrections;

        public int SeparationBodyCount()
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return -1;
            return Em.GetBuffer<SeparationBody>(_local).Length;
        }

        public void DebugLocalState(out int tick, out float accumulator, out int2 position)
        {
            PredictionState prediction = LocalPrediction();
            tick = prediction.Tick;
            accumulator = prediction.Accumulator;
            position = prediction.PositionMm;
        }

        private PredictionState LocalPrediction()
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return default;
            return Em.GetComponentData<PredictionState>(_local);
        }

        private ServerSnapshot LocalSnapshot()
        {
            if (_local == Entity.Null || !Em.Exists(_local)) return default;
            return Em.GetComponentData<ServerSnapshot>(_local);
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
