using System.Collections.Generic;
using AI003.Ecs;
using AI003.Player;
using AI003.World;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AI003.Net
{
    /// <summary>
    /// Protocol v3 combat room client: enters the arena, sends 20 Hz input frames
    /// and drives both the local avatar and the other fighters from the server's
    /// authoritative frames. The arena is overlaid on the terrain around the
    /// spawn point, so the existing camera and character visuals are reused.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoomClient : MonoBehaviour
    {
        public static RoomClient Instance { get; private set; }

        private const string ClientName = "UnityPlayer";

        public bool InRoom { get; private set; }
        public string RoomId { get; private set; } = "";

        /// <summary>True while any client is in the arena; world HUDs use it to hide.</summary>
        public static bool AnyActive { get; private set; }
        public int HP { get; private set; } = 100;
        public int MaxHP { get; private set; } = 100;
        public int Tick { get; private set; }
        public int Players { get; private set; }
        public bool Dead { get; private set; }

        public int PredictedTick => _bridge != null ? _bridge.LocalTick : 0;
        public int ServerTick => _bridge != null ? _bridge.ServerTick : 0;
        public int Corrections => _bridge != null ? _bridge.Corrections : 0;
        public float LastError => _lastError;
        public Vector3 PredictedPosition => _bridge != null ? _bridge.GetLocalPositionMeters() : Vector3.zero;
        public int LastCommandDX => _lastDX;
        public int LastCommandDZ => _lastDZ;

        private readonly List<Envelope> _inbox = new List<Envelope>();
        private readonly Dictionary<string, RemoteAvatar> _remotes = new Dictionary<string, RemoteAvatar>();
        private readonly Dictionary<string, Vector3> _lastPositions = new Dictionary<string, Vector3>();

        private ConnectionBootstrap _connection;
        private ThirdPersonController _local;
        private Animator _localAnimator;
        private RoomEcsBridge _bridge;
        private Camera _roomCamera;
        private readonly List<(int x, int z, bool alive)> _separationBodies = new List<(int, int, bool)>();
        private WorldData _world;

        private int _cursor;
        private string _playerId;
        private int _tickRate = 20;
        private float _sendInterval = 0.05f;
        private float _arenaHalfMeters = 20f;
        private int _attackRangeMm = 3000;
        private bool _prevAttack;
        private readonly Dictionary<string, Vector3> _remotePositions = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, bool> _remoteAlive = new Dictionary<string, bool>();
        private float _nextSend;
        private float _nextEnter;
        private bool _enabled;
        private bool _needsReenter;

        private Vector3 _center;
        private float _groundY;
        private string _lastEvent = "";
        private float _lastEventUntil;
        private GUIStyle _style;
        private GUIStyle _barStyle;

        // Client-side prediction: the local avatar simulates each tick with the
        // same integer step as the server, and reconciles against room.frame.
        private struct RoomCommand
        {
            public int DX;
            public int DZ;
            public bool Attack;
        }

        private readonly Dictionary<int, RoomCommand> _pending = new Dictionary<int, RoomCommand>();
        private readonly Dictionary<int, Vector3> _predicted = new Dictionary<int, Vector3>();
        private int _facingX;
        private int _facingZ;
        private float _speedPerTick = 0.25f;
        private float _diagPerTick = 0.177f;
        private float _nextPredictAt;
        private int _corrections;
        private float _lastError;

        private int _debugDX;
        private int _debugDZ;
        private bool _debugAttack;
        private float _debugUntil;
        private int _lastDX;
        private int _lastDZ;

        private GameObject _arenaRoot;
        private float _localHitUntil;
        private bool _wasDead;

        private struct DamagePopup
        {
            public Vector3 World;
            public string Text;
            public float Until;
        }

        private readonly List<DamagePopup> _popups = new List<DamagePopup>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInstall()
        {
            EnsureExists();
        }

        public static RoomClient EnsureExists()
        {
            if (Instance != null) return Instance;

            var existing = FindFirstObjectByType<RoomClient>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            var go = new GameObject("[AI003] Room");
            DontDestroyOnLoad(go);
            return go.AddComponent<RoomClient>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _bridge = RoomEcsBridge.EnsureExists();
            // Opt-in: the room drives the avatar, so tests and normal exploration
            // keep the third-person controller unless AI003_ROOM=1 is set.
            _enabled = System.Environment.GetEnvironmentVariable("AI003_ROOM") == "1";
        }

        private static ThirdPersonController FindPlayer()
        {
            var bootstrap = FindFirstObjectByType<WorldBootstrap>();
            if (bootstrap != null && bootstrap.Player != null) return bootstrap.Player;
            return FindFirstObjectByType<ThirdPersonController>();
        }
        private void OnDestroy()
        {
            LeaveLocal();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!_enabled) return;

            if (_connection == null)
            {
                _connection = ConnectionBootstrap.Instance ?? FindFirstObjectByType<ConnectionBootstrap>();
                if (_connection == null) return;
            }

            _connection.ReadFrames(ref _cursor, _inbox);
            for (int i = 0; i < _inbox.Count; i++)
            {
                HandleMessage(_inbox[i]);
            }
            _inbox.Clear();

            if (!_connection.IsConnected)
            {
                // Keep the arena state; the server holds it for 30 s and a
                // reconnect with the same clientId resumes the same fighter.
                if (InRoom) _needsReenter = true;
                return;
            }

            if (InRoom && _needsReenter)
            {
                if (Time.unscaledTime >= _nextEnter)
                {
                    _nextEnter = Time.unscaledTime + 1f;
                    SendEnter();
                }
                return;
            }

            if (!InRoom)
            {
                var bootstrap = FindFirstObjectByType<WorldBootstrap>();
                if (bootstrap == null || !bootstrap.IsReady) return;
                if (Time.unscaledTime >= _nextEnter)
                {
                    _nextEnter = Time.unscaledTime + 1f;
                    SendEnter();
                }
                return;
            }

            if (_local == null)
            {
                _local = FindPlayer();
                if (_local == null) return;
                _localAnimator = _local.GetComponentInChildren<Animator>();
                // Do not create the prediction entity before the room is entered:
                // the ECS would move the avatar to the default (0,0) prediction.
            }

            float now = Time.unscaledTime;
            _ = now;

            // The ECS predicts at 20 Hz in integer millimetres; this layer only
            // feeds the current command and ships the ticks the system produced.
            RoomCommand command = ReadCommand();
            _bridge.SetInput(command.DX, command.DZ, command.Attack);

            // Local hit feedback: swing and spark immediately, the server confirms
            // damage/corrections on the next frames.
            if (command.Attack && !_prevAttack)
            {
                _bridge.TriggerAttack(_playerId);
                TryLocalHit();
            }
            _prevAttack = command.Attack;

            while (_bridge.TryTakeStep(out int stepTick, out int dx, out int dz, out bool attack))
            {
                SendTickInput(stepTick, new RoomCommand { DX = dx, DZ = dz, Attack = attack });
            }
        }

        /// <summary>Range/cone check against the latest snapshot, purely for feedback.</summary>
        private void TryLocalHit()
        {
            _bridge.GetLocalFacing(out int facingX, out int facingZ);
            if (facingX == 0 && facingZ == 0) facingZ = 1;

            Vector3 self = _bridge.GetLocalPositionMeters();
            float range = _attackRangeMm / 1000f;

            foreach (var pair in _remotePositions)
            {
                if (_remoteAlive.TryGetValue(pair.Key, out bool alive) && !alive) continue;

                Vector3 delta = pair.Value - self;
                delta.y = 0f;
                float distance = delta.magnitude;
                if (distance > range || distance < 0.0001f) continue;

                // cos(60°) cone: dot(direction, facing) >= 0.5 * |direction| (facing is unit).
                float dot = delta.x * facingX + delta.z * facingZ;
                if (dot < 0.5f * distance) continue;

                _bridge.TriggerHit(pair.Key);
            }
        }

        private void SendEnter()
        {
            var body = new JObject
            {
                ["name"] = ClientName,
                ["clientId"] = ClientIdentity.Id
            };

            // Optional explicit room (demo scripts use a fresh room per run so
            // offline ghosts from earlier runs cannot satisfy the ready checks).
            string roomName = System.Environment.GetEnvironmentVariable("AI003_ROOM_NAME");
            if (!string.IsNullOrEmpty(roomName))
            {
                body["room"] = roomName;
            }

            _connection.Send(new Envelope { Type = "room.enter", Seq = _connection.NextSeq(), Body = body });
        }

        /// <summary>Test/debug hook: holds an input for a short window (prediction included).</summary>
        public void DebugSendInput(int dx, int dz, bool attack)
        {
            DebugHold(dx, dz, attack, 0.25f);
        }

        /// <summary>Test/debug hook: overrides the read input for a number of seconds.</summary>
        public void DebugHold(int dx, int dz, bool attack, float seconds)
        {
            _debugDX = dx;
            _debugDZ = dz;
            _debugAttack = attack;
            _debugUntil = Time.unscaledTime + Mathf.Max(0.05f, seconds);
        }

        private void HandleMessage(Envelope envelope)
        {
            switch (envelope.Type)
            {
                case "room.entered":
                    OnEntered(envelope.Body as JObject);
                    break;
                case "room.frame":
                    OnFrame(envelope.Body as JObject);
                    break;
                case "room.left":
                    LeaveLocal();
                    break;
                case "error":
                    Debug.LogWarning($"[AI003] room error: {envelope.Body}");
                    break;
            }
        }

        private void OnEntered(JObject body)
        {
            if (body == null) return;

            bool wasInRoom = InRoom;
            _playerId = body["playerId"]?.ToString();
            RoomId = body["roomId"]?.ToString() ?? "";
            _tickRate = body["tickRate"]?.Value<int>() ?? 20;
            _sendInterval = 1f / Mathf.Max(1, _tickRate);
            _arenaHalfMeters = (body["arenaHalfMM"]?.Value<float>() ?? 20000f) / 1000f;
            _attackRangeMm = body["attackRangeMM"]?.Value<int>() ?? 3000;
            MaxHP = body["maxHP"]?.Value<int>() ?? 100;
            var state = body["state"] as JObject;
            HP = state?["hp"]?.Value<int>() ?? MaxHP;
            Dead = state != null && !(state["alive"]?.Value<bool>() ?? true);

            _local = FindPlayer();
            if (_local != null && !wasInRoom)
            {
                _center = _local.transform.position;
                _groundY = _center.y;
                _local.enabled = false;
                _localAnimator = _local.GetComponentInChildren<Animator>();
                _bridge.Clear();
                _bridge.EnsureLocal(_local.transform, _localAnimator, _local.GetComponentInChildren<Renderer>());
            }

            _speedPerTick = (body["speedMMPerTick"]?.Value<float>() ?? 250f) / 1000f;
            _diagPerTick = (body["diagonalMMPerTick"]?.Value<float>() ?? 177f) / 1000f;

            if (state != null)
            {
                float xMeters = (state["x"]?.Value<float>() ?? 0f) / 1000f;
                float zMeters = (state["z"]?.Value<float>() ?? 0f) / 1000f;
                Vector3 spawn = ToWorld(xMeters, zMeters);
                _bridge.SetLocal(spawn, YawFromFacing(
                    state["facingX"]?.Value<int>() ?? 0,
                    state["facingZ"]?.Value<int>() ?? 1), 0f, true);
                _bridge.ConfigureLocal(
                    Mathf.RoundToInt(_center.x * 1000f),
                    Mathf.RoundToInt(_center.z * 1000f),
                    Mathf.RoundToInt(_arenaHalfMeters * 1000f),
                    Mathf.RoundToInt(_speedPerTick * 1000f),
                    Mathf.RoundToInt(_diagPerTick * 1000f));
                _bridge.SetServerSnapshot(
                    Mathf.RoundToInt(spawn.x * 1000f),
                    Mathf.RoundToInt(spawn.z * 1000f),
                    body["tick"]?.Value<int>() ?? 0);
            }

            if (!wasInRoom)
            {
                WorldSyncClient.EnsureExists().Suspend();
                BuildArena();
            }

            _needsReenter = false;
            InRoom = true;
            AnyActive = true;
            Tick = body["tick"]?.Value<int>() ?? 0;
            Debug.Log($"[AI003] room entered as {_playerId} (tick {Tick}, {_tickRate} Hz)");
        }

        private void OnFrame(JObject body)
        {
            if (!InRoom || body == null) return;

            Tick = body["tick"]?.Value<int>() ?? Tick;
            var players = body["players"] as JArray;
            if (players == null) return;

            Players = players.Count;
            _separationBodies.Clear();
            var seen = new HashSet<string>();
            foreach (var token in players)
            {
                string id = token["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                seen.Add(id);

                float xMeters = (token["x"]?.Value<float>() ?? 0f) / 1000f;
                float zMeters = (token["z"]?.Value<float>() ?? 0f) / 1000f;
                float yaw = YawFromFacing(
                    token["facingX"]?.Value<int>() ?? 0,
                    token["facingZ"]?.Value<int>() ?? 1);
                bool alive = token["alive"]?.Value<bool>() ?? true;
                Vector3 position = ToWorld(xMeters, zMeters);

                if (id == _playerId)
                {
                    HP = token["hp"]?.Value<int>() ?? HP;
                    Dead = !alive;
                    _bridge.SetServerSnapshot(
                        Mathf.RoundToInt(position.x * 1000f),
                        Mathf.RoundToInt(position.z * 1000f),
                        Tick);
                }
                else
                {
                    _remotePositions[id] = position;
                    _remoteAlive[id] = alive;
                    _separationBodies.Add((
                        Mathf.RoundToInt(position.x * 1000f),
                        Mathf.RoundToInt(position.z * 1000f),
                        alive));
                    UpdateRemote(id, position, yaw, alive);
                }
            }
            _bridge.SetSeparationBodies(_separationBodies);

            RemoveMissing(seen);

            var events = body["events"] as JArray;
            if (events != null)
            {
                foreach (var token in events)
                {
                    string type = token["type"]?.ToString();
                    string source = token["source"]?.ToString();
                    string target = token["target"]?.ToString();
                    int damage = token["damage"]?.Value<int>() ?? 0;

                    switch (type)
                    {
                        case "attack":
                            if (source == _playerId)
                            {
                                _bridge.TriggerAttack(_playerId);
                            }
                            else if (source != null && _remotes.ContainsKey(source))
                            {
                                _bridge.TriggerAttack(source);
                            }
                            break;

                        case "hit":
                            _lastEvent = "hit!";
                            if (target == _playerId)
                            {
                                _localHitUntil = Time.unscaledTime + 0.25f;
                                if (_local != null) AddPopup(_local.transform.position, "-" + damage);
                            }
                            else if (target != null && _remotes.ContainsKey(target))
                            {
                                _bridge.TriggerHit(target);
                                AddPopup(_remotes[target].transform.position, "-" + damage);
                            }
                            break;

                        case "death":
                            _lastEvent = "died - respawning";
                            if (target != _playerId && target != null && _remotes.ContainsKey(target))
                            {
                                _bridge.SetRemoteAlive(target, false);
                            }
                            break;

                        case "respawn":
                            _lastEvent = "respawned";
                            if (source != null && _remotes.ContainsKey(source))
                            {
                                _bridge.SetRemoteAlive(source, true);
                            }
                            break;
                    }
                    _lastEventUntil = Time.unscaledTime + 1.2f;
                }
            }
        }

        private void UpdateRemote(string id, Vector3 position, float yaw, bool alive)
        {
            if (!_remotes.TryGetValue(id, out RemoteAvatar avatar) || avatar == null)
            {
                var prefab = Resources.Load<GameObject>("Characters/KenneyProtagonists/Player");
                if (prefab == null) return;

                var instance = Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f));
                instance.name = "Room_" + id;
                avatar = instance.AddComponent<RemoteAvatar>();
                _remotes[id] = avatar;
            }

            string anim = "idle";
            if (alive && _lastPositions.TryGetValue(id, out Vector3 previous))
            {
                float speed = Vector3.Distance(previous, position) / Mathf.Max(0.001f, _sendInterval);
                anim = speed > 6.5f ? "run" : speed > 0.2f ? "walk" : "idle";
            }
            _lastPositions[id] = position;

            float animSpeed = anim == "run" ? 1f : anim == "walk" ? 0.35f : anim == "jump" ? 0.4f : anim == "swim" ? 0.3f : 0f;
            float playRate = anim == "walk" ? 0.7f : anim == "swim" ? 0.6f : 1f;
            _bridge.UpsertRemote(id, position, yaw, alive, animSpeed, playRate,
                avatar.transform, avatar.Animator, avatar.Renderer);
        }

        private void RemoveMissing(HashSet<string> seen)
        {
            var stale = new List<string>();
            foreach (string id in _remotes.Keys)
            {
                if (!seen.Contains(id)) stale.Add(id);
            }
            foreach (string id in stale)
            {
                if (_remotes.TryGetValue(id, out RemoteAvatar avatar) && avatar != null)
                {
                    Destroy(avatar.gameObject);
                }
                _bridge.RemoveRemote(id);
                _remotes.Remove(id);
                _lastPositions.Remove(id);
                _remotePositions.Remove(id);
                _remoteAlive.Remove(id);
            }
        }

        private RoomCommand ReadCommand()
        {
            bool attack = false;
            float ix = 0f, iz = 0f;

            if (Time.unscaledTime < _debugUntil)
            {
                ix = _debugDX;
                iz = _debugDZ;
                attack = _debugAttack;
            }
            else
            {
                var keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) ix += 1f;
                    if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) ix -= 1f;
                    if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) iz += 1f;
                    if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) iz -= 1f;
                    attack = keyboard.spaceKey.isPressed || keyboard.leftCtrlKey.isPressed;
                }
            }

            MapToWorld(ix, iz, out int dx, out int dz);
            _lastDX = dx;
            _lastDZ = dz;
            return new RoomCommand { DX = dx, DZ = dz, Attack = attack };
        }

        /// <summary>
        /// Maps stick input (right / forward) onto the eight protocol directions using
        /// the camera basis, so W always moves away from the camera (the open-world
        /// controller does the same); without this the room felt inverted.
        /// </summary>
        private void MapToWorld(float ix, float iz, out int dx, out int dz)
        {
            dx = 0;
            dz = 0;
            if (Mathf.Abs(ix) < 0.01f && Mathf.Abs(iz) < 0.01f) return;

            Vector3 forward = Vector3.forward;
            Vector3 right = Vector3.right;
            if (_roomCamera == null) _roomCamera = Camera.main;
            if (_roomCamera != null)
            {
                forward = Vector3.ProjectOnPlane(_roomCamera.transform.forward, Vector3.up).normalized;
                right = Vector3.ProjectOnPlane(_roomCamera.transform.right, Vector3.up).normalized;
            }

            Vector3 wish = forward * iz + right * ix;
            if (wish.sqrMagnitude < 0.0001f) return;

            float angle = Mathf.Atan2(wish.x, wish.z) * Mathf.Rad2Deg;
            float snapped = Mathf.Round(angle / 45f) * 45f * Mathf.Deg2Rad;
            dx = Mathf.RoundToInt(Mathf.Sin(snapped));
            dz = Mathf.RoundToInt(Mathf.Cos(snapped));
        }

        private void SendTickInput(int tick, RoomCommand command)
        {
            if (_connection == null) return;

            var body = new JObject
            {
                ["tick"] = tick,
                ["dx"] = command.DX,
                ["dz"] = command.DZ,
                ["buttons"] = command.Attack ? 1 : 0
            };
            _connection.Send(new Envelope { Type = "room.input", Seq = _connection.NextSeq(), Body = body });
        }

        private void AddPopup(Vector3 world, string text)
        {
            _popups.Add(new DamagePopup
            {
                World = world + Vector3.up * 2f,
                Text = text,
                Until = Time.unscaledTime + 1f
            });
        }

        private void BuildArena()
        {
            if (_arenaRoot != null) return;

            _arenaRoot = new GameObject("[AI003] Arena");
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", new Color(0.95f, 0.75f, 0.15f));
            material.SetFloat("_Smoothness", 0.25f);

            float half = _arenaHalfMeters;
            for (int i = 0; i < 4; i++)
            {
                bool horizontal = i < 2;
                float offset = i % 2 == 0 ? half : -half;
                GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = horizontal
                    ? (offset > 0 ? "wall_north" : "wall_south")
                    : (offset > 0 ? "wall_east" : "wall_west");
                wall.transform.SetParent(_arenaRoot.transform, true);
                wall.transform.position = new Vector3(
                    _center.x + (horizontal ? 0f : offset),
                    _center.y + 0.35f,
                    _center.z + (horizontal ? offset : 0f));
                wall.transform.localScale = horizontal
                    ? new Vector3(half * 2f + 0.4f, 0.7f, 0.2f)
                    : new Vector3(0.2f, 0.7f, half * 2f + 0.4f);
                Destroy(wall.GetComponent<Collider>());
                wall.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
        }

        private void DestroyArena()
        {
            if (_arenaRoot != null) Destroy(_arenaRoot);
            _arenaRoot = null;
        }

        private Vector3 ToWorld(float xMeters, float zMeters)
        {
            float x = _center.x + xMeters;
            float z = _center.z + zMeters;
            float y = _groundY;
            if (_world == null && WorldBootstrap.Instance != null) _world = WorldBootstrap.Instance.Data;
            if (_world != null)
            {
                y = _world.HeightAt(x, z);
            }
            return new Vector3(x, y, z);
        }

        private static float YawFromFacing(int facingX, int facingZ)
        {
            if (facingX == 0 && facingZ == 0) return 0f;
            return Mathf.Atan2(facingX, facingZ) * Mathf.Rad2Deg;
        }

        private void LeaveLocal()
        {
            if (!InRoom) return;
            InRoom = false;
            AnyActive = false;
            Dead = false;
            HP = MaxHP;

            foreach (var avatar in _remotes.Values)
            {
                if (avatar != null) Destroy(avatar.gameObject);
            }
            _remotes.Clear();
            _lastPositions.Clear();
            _remotePositions.Clear();
            _remoteAlive.Clear();
            _prevAttack = false;
            _popups.Clear();
            DestroyArena();
            _bridge.Clear();
            _localHitUntil = 0f;

            if (_local != null) _local.enabled = true;
            WorldSyncClient.EnsureExists().Resume();

            Debug.Log("[AI003] room left");
        }

        private void OnGUI()
        {
            if (!InRoom) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(14, Screen.height / 50) };
                _style.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
                _barStyle = new GUIStyle(GUI.skin.box);
            }

            float width = Mathf.Clamp(Screen.width * 0.18f, 160f, 320f);
            var rect = new Rect(16f, Screen.height - 96f, width, 22f);
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.Box(rect, GUIContent.none, _barStyle);
            GUI.color = Dead ? new Color(0.8f, 0.2f, 0.2f) : new Color(0.2f, 0.85f, 0.3f);
            float health = MaxHP > 0 ? Mathf.Clamp01((float)HP / MaxHP) : 0f;
            GUI.Box(new Rect(rect.x, rect.y, rect.width * health, rect.height), GUIContent.none, _barStyle);
            GUI.color = Color.white;

            string eventText = Time.unscaledTime < _lastEventUntil ? "   " + _lastEvent : "";
            GUI.Label(new Rect(16f, Screen.height - 68f, 760f, 24f),
                $"{RoomId}  hp {HP}/{MaxHP}  tick {Tick} / pred {PredictedTick}  fighters {Players}  corr {Corrections}{eventText}", _style);
            GUI.Label(new Rect(16f, Screen.height - 44f, 760f, 24f),
                $"WASD (camera relative)   Space attack   reconcile {_lastError * 1000f:F0} mm", _style);

            float now = Time.unscaledTime;

            // Red vignette when the local fighter takes a hit.
            if (now < _localHitUntil)
            {
                float alpha = (_localHitUntil - now) / 0.25f * 0.3f;
                GUI.color = new Color(1f, 0.1f, 0.1f, alpha);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }

            // Damage numbers projected from world space.
            var camera = Camera.main;
            if (camera != null)
            {
                for (int i = _popups.Count - 1; i >= 0; i--)
                {
                    DamagePopup popup = _popups[i];
                    if (now >= popup.Until)
                    {
                        _popups.RemoveAt(i);
                        continue;
                    }

                    float remaining = popup.Until - now;
                    Vector3 screen = camera.WorldToScreenPoint(popup.World + Vector3.up * (1f - remaining) * 0.8f);
                    if (screen.z <= 0f) continue;

                    GUI.color = new Color(1f, 0.85f, 0.2f, Mathf.Clamp01(remaining));
                    GUI.Label(new Rect(screen.x - 24f, Screen.height - screen.y - 12f, 96f, 24f), popup.Text, _style);
                    GUI.color = Color.white;
                }
            }

            if (Dead)
            {
                var big = new GUIStyle(_style)
                {
                    fontSize = Mathf.Max(20, Screen.height / 24),
                    alignment = TextAnchor.MiddleCenter
                };
                big.normal.textColor = new Color(1f, 0.35f, 0.3f, 0.95f);
                GUI.Label(new Rect(0f, Screen.height * 0.35f, Screen.width, 40f), "RESPAWNING...", big);
            }
        }
    }
}
