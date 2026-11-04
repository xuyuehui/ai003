using System.Collections.Generic;
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

        public int PredictedTick => _predictedTick;
        public int ServerTick => _serverTick;
        public int Corrections => _corrections;
        public float LastError => _lastError;
        public Vector3 PredictedPosition => _predictedPos;
        public int LastCommandDX => _lastDX;
        public int LastCommandDZ => _lastDZ;

        private readonly List<Envelope> _inbox = new List<Envelope>();
        private readonly Dictionary<string, RemoteAvatar> _remotes = new Dictionary<string, RemoteAvatar>();
        private readonly Dictionary<string, Vector3> _lastPositions = new Dictionary<string, Vector3>();

        private ConnectionBootstrap _connection;
        private ThirdPersonController _local;
        private Animator _localAnimator;
        private WorldData _world;

        private int _cursor;
        private string _playerId;
        private int _tickRate = 20;
        private float _sendInterval = 0.05f;
        private float _arenaHalfMeters = 20f;
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
        private Vector3 _predictedPos;
        private Vector3 _lastAppliedPos;
        private int _predictedTick;
        private int _serverTick;
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
        private float _localAttackUntil;
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
            // Opt-in: the room drives the avatar, so tests and normal exploration
            // keep the third-person controller unless AI003_ROOM=1 is set.
            _enabled = System.Environment.GetEnvironmentVariable("AI003_ROOM") == "1";
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
                if (Time.unscaledTime >= _nextEnter)
                {
                    _nextEnter = Time.unscaledTime + 1f;
                    SendEnter();
                }
                return;
            }

            if (_local == null)
            {
                _local = FindFirstObjectByType<ThirdPersonController>();
                if (_local == null) return;
                _localAnimator = _local.GetComponentInChildren<Animator>();
            }

            float now = Time.unscaledTime;

            // Keep the local simulation a few ticks ahead of the server; if a
            // frame was long (editor hitch) catch up instead of drifting behind.
            const int predictionAhead = 3;
            int desired = _serverTick + predictionAhead;
            int guard = 0;
            while (_predictedTick < desired && guard++ < 8)
            {
                PredictTick();
            }

            if (now >= _nextPredictAt)
            {
                _nextPredictAt = Mathf.Max(now + _sendInterval, _nextPredictAt + _sendInterval);
                PredictTick();
            }
            ApplyLocalPrediction();
        }

        private void SendEnter()
        {
            var body = new JObject
            {
                ["name"] = ClientName,
                ["clientId"] = ClientIdentity.Id
            };
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
            MaxHP = body["maxHP"]?.Value<int>() ?? 100;
            var state = body["state"] as JObject;
            HP = state?["hp"]?.Value<int>() ?? MaxHP;
            Dead = state != null && !(state["alive"]?.Value<bool>() ?? true);

            _local = FindFirstObjectByType<ThirdPersonController>();
            if (_local != null && !wasInRoom)
            {
                _center = _local.transform.position;
                _groundY = _center.y;
                _local.enabled = false;
                _localAnimator = _local.GetComponentInChildren<Animator>();
            }

            _speedPerTick = (body["speedMMPerTick"]?.Value<float>() ?? 250f) / 1000f;
            _diagPerTick = (body["diagonalMMPerTick"]?.Value<float>() ?? 177f) / 1000f;

            if (!wasInRoom)
            {
                _predictedTick = body["tick"]?.Value<int>() ?? 0;
                _nextPredictAt = Time.unscaledTime + _sendInterval;
            }

            if (state != null)
            {
                _predictedPos = ToWorld(
                    (state["x"]?.Value<float>() ?? 0f) / 1000f,
                    (state["z"]?.Value<float>() ?? 0f) / 1000f);
                _facingX = state["facingX"]?.Value<int>() ?? 0;
                _facingZ = state["facingZ"]?.Value<int>() ?? 1;
                _lastAppliedPos = _predictedPos;
                _pending.Clear();
                _predicted.Clear();
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
                    Reconcile(position, Tick);
                }
                else
                {
                    UpdateRemote(id, position, yaw, alive);
                }
            }

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
                                _localAttackUntil = Time.unscaledTime + 0.18f;
                            }
                            else if (source != null && _remotes.TryGetValue(source, out RemoteAvatar attacker) && attacker != null)
                            {
                                attacker.TriggerAttack();
                            }
                            break;

                        case "hit":
                            _lastEvent = "hit!";
                            if (target == _playerId)
                            {
                                _localHitUntil = Time.unscaledTime + 0.25f;
                                if (_local != null) AddPopup(_local.transform.position, "-" + damage);
                            }
                            else if (target != null && _remotes.TryGetValue(target, out RemoteAvatar victim) && victim != null)
                            {
                                victim.TriggerHit();
                                AddPopup(victim.TargetPosition, "-" + damage);
                            }
                            break;

                        case "death":
                            _lastEvent = "died - respawning";
                            if (target != _playerId && target != null &&
                                _remotes.TryGetValue(target, out RemoteAvatar dead) && dead != null)
                            {
                                dead.SetAlive(false);
                            }
                            break;

                        case "respawn":
                            _lastEvent = "respawned";
                            if (source != null && _remotes.TryGetValue(source, out RemoteAvatar reborn) && reborn != null)
                            {
                                reborn.SetAlive(true);
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
            avatar.Apply(position, yaw, alive ? anim : "idle");
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
                _remotes.Remove(id);
                _lastPositions.Remove(id);
            }
        }

        private void PredictTick()
        {
            if (!InRoom || _local == null) return;

            RoomCommand command = ReadCommand();
            int tick = ++_predictedTick;
            _pending[tick] = command;

            StepPrediction(ref _predictedPos, ref _facingX, ref _facingZ, command);
            _predicted[tick] = _predictedPos;

            SendTickInput(tick, command);
        }

        /// <summary>Applies the predicted state to the avatar immediately (no 20 Hz lag).</summary>
        private void ApplyLocalPrediction()
        {
            if (_local == null) return;

            float moved = Vector3.Distance(_predictedPos, _lastAppliedPos);
            _lastAppliedPos = _predictedPos;

            _local.transform.position = _predictedPos;
            float yaw = YawFromFacing(_facingX, _facingZ);
            _local.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            // Attack lunge: a short forward nudge on top of the predicted position.
            if (Time.unscaledTime < _localAttackUntil)
            {
                float progress = 1f - (_localAttackUntil - Time.unscaledTime) / 0.18f;
                _local.transform.position += Quaternion.Euler(0f, yaw, 0f) * Vector3.forward
                                             * (Mathf.Sin(progress * Mathf.PI) * 0.35f);
            }

            if (_localAnimator != null)
            {
                bool moving = moved > 0.01f;
                _localAnimator.SetFloat("Speed", moving ? 0.6f : 0f);
                _localAnimator.SetBool("Grounded", true);
                _localAnimator.speed = moving ? 1f : 1f;
            }
        }

        /// <summary>Compares the authoritative frame with the prediction and replays if needed.</summary>
        private void Reconcile(Vector3 serverPosition, int serverTick)
        {
            _serverTick = serverTick;

            bool replay = false;
            if (_predicted.TryGetValue(serverTick, out Vector3 predicted))
            {
                _lastError = Vector3.Distance(predicted, serverPosition);
                if (_lastError > 0.05f)
                {
                    _predictedPos = serverPosition;
                    _corrections++;
                    replay = true;
                }
            }
            else
            {
                // Joined mid-stream or lost a frame: snap to the server state.
                _predictedPos = serverPosition;
                replay = true;
            }

            if (replay)
            {
                for (int tick = serverTick + 1; tick <= _predictedTick; tick++)
                {
                    if (!_pending.TryGetValue(tick, out RoomCommand command)) break;
                    StepPrediction(ref _predictedPos, ref _facingX, ref _facingZ, command);
                    _predicted[tick] = _predictedPos;
                }
                _lastAppliedPos = _predictedPos;
            }

            Prune(serverTick);
        }

        private void StepPrediction(ref Vector3 position, ref int facingX, ref int facingZ, RoomCommand command)
        {
            if (command.DX == 0 && command.DZ == 0) return;

            float step = command.DX != 0 && command.DZ != 0 ? _diagPerTick : _speedPerTick;
            position.x += command.DX * step;
            position.z += command.DZ * step;
            position.x = Mathf.Clamp(position.x - _center.x, -_arenaHalfMeters, _arenaHalfMeters) + _center.x;
            position.z = Mathf.Clamp(position.z - _center.z, -_arenaHalfMeters, _arenaHalfMeters) + _center.z;
            facingX = command.DX;
            facingZ = command.DZ;
        }

        private void Prune(int serverTick)
        {
            var stale = new List<int>();
            foreach (int tick in _predicted.Keys)
            {
                if (tick <= serverTick) stale.Add(tick);
            }
            foreach (int tick in stale) _predicted.Remove(tick);

            stale.Clear();
            foreach (int tick in _pending.Keys)
            {
                if (tick <= serverTick) stale.Add(tick);
            }
            foreach (int tick in stale) _pending.Remove(tick);
        }

        private RoomCommand ReadCommand()
        {
            if (Time.unscaledTime < _debugUntil)
            {
                _lastDX = _debugDX;
                _lastDZ = _debugDZ;
                return new RoomCommand { DX = _debugDX, DZ = _debugDZ, Attack = _debugAttack };
            }

            int dx = 0, dz = 0;
            bool attack = false;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) dx += 1;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) dx -= 1;
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) dz += 1;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) dz -= 1;
                attack = keyboard.spaceKey.isPressed || keyboard.leftCtrlKey.isPressed;
            }
            _lastDX = dx;
            _lastDZ = dz;
            return new RoomCommand { DX = dx, DZ = dz, Attack = attack };
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
            _popups.Clear();
            DestroyArena();
            _localAttackUntil = 0f;
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
                $"{RoomId}  hp {HP}/{MaxHP}  tick {Tick} / pred {_predictedTick}  fighters {Players}  corr {_corrections}{eventText}", _style);
            GUI.Label(new Rect(16f, Screen.height - 44f, 760f, 24f),
                $"WASD / arrows move   Space attack   reconcile {_lastError * 1000f:F0} mm", _style);

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
