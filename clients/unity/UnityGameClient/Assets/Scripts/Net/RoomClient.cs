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

        /// <summary>True while any client is in the arena; world HUDs use it to hide.</summary>
        public static bool AnyActive { get; private set; }
        public int HP { get; private set; } = 100;
        public int MaxHP { get; private set; } = 100;
        public int Tick { get; private set; }
        public int Players { get; private set; }
        public bool Dead { get; private set; }

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

        private Vector3 _center;
        private float _groundY;
        private Vector3 _targetPosition;
        private float _targetYaw;
        private string _lastEvent = "";
        private float _lastEventUntil;
        private GUIStyle _style;
        private GUIStyle _barStyle;

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
                if (InRoom) LeaveLocal();
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

            SmoothLocal();

            if (Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + _sendInterval;
                SendInput();
            }
        }

        private void SendEnter()
        {
            var body = new JObject { ["name"] = ClientName };
            _connection.Send(new Envelope { Type = "room.enter", Seq = _connection.NextSeq(), Body = body });
        }

        private void SendInput()
        {
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

            DebugSendInput(dx, dz, attack);
        }

        /// <summary>Test/debug hook: sends one room.input frame.</summary>
        public void DebugSendInput(int dx, int dz, bool attack)
        {
            if (!InRoom || _connection == null) return;

            var body = new JObject
            {
                ["dx"] = dx,
                ["dz"] = dz,
                ["buttons"] = attack ? 1 : 0
            };
            _connection.Send(new Envelope { Type = "room.input", Seq = _connection.NextSeq(), Body = body });
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

            _playerId = body["playerId"]?.ToString();
            _tickRate = body["tickRate"]?.Value<int>() ?? 20;
            _sendInterval = 1f / Mathf.Max(1, _tickRate);
            _arenaHalfMeters = (body["arenaHalfMM"]?.Value<float>() ?? 20000f) / 1000f;
            MaxHP = body["maxHP"]?.Value<int>() ?? 100;
            HP = MaxHP;

            _local = FindFirstObjectByType<ThirdPersonController>();
            if (_local != null)
            {
                _center = _local.transform.position;
                _groundY = _center.y;
                _targetPosition = _center;
                _local.enabled = false;
                _localAnimator = _local.GetComponentInChildren<Animator>();
            }

            WorldSyncClient.EnsureExists().Suspend();

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
                    _targetPosition = position;
                    _targetYaw = yaw;
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
                    if (type == "hit")
                    {
                        _lastEvent = "hit!";
                    }
                    else if (type == "death")
                    {
                        _lastEvent = "died - respawning";
                    }
                    else if (type == "respawn")
                    {
                        _lastEvent = "respawned";
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

        private void SmoothLocal()
        {
            if (_local == null) return;

            float t = 1f - Mathf.Exp(-20f * Time.deltaTime);
            _local.transform.position = Vector3.Lerp(_local.transform.position, _targetPosition, t);
            _local.transform.rotation = Quaternion.Slerp(
                _local.transform.rotation, Quaternion.Euler(0f, _targetYaw, 0f), t);

            if (_localAnimator != null)
            {
                float moved = Vector3.Distance(_local.transform.position, _lastPositions.TryGetValue("self", out Vector3 previous) ? previous : _local.transform.position);
                _lastPositions["self"] = _local.transform.position;
                float speed = moved / Mathf.Max(0.001f, Time.deltaTime);
                _localAnimator.SetFloat("Speed", Mathf.Clamp01(speed / 10f));
                _localAnimator.SetBool("Grounded", true);
                _localAnimator.speed = speed > 0.2f ? Mathf.Clamp(speed / 6f, 0.6f, 1.5f) : 1f;
            }
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
            GUI.Label(new Rect(16f, Screen.height - 68f, 560f, 24f),
                $"arena  hp {HP}/{MaxHP}  tick {Tick}  fighters {Players}{eventText}", _style);
            GUI.Label(new Rect(16f, Screen.height - 44f, 700f, 24f),
                "WASD / arrows move   Space attack", _style);
        }
    }
}
