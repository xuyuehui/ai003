using System.Collections.Generic;
using AI003.World;
using AI003.Player;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AI003.Net
{
    /// <summary>
    /// Quake III style arena client: sends usercmds, predicts locally, and
    /// reconciles against sequenced snapshots (delta-compressed by the server).
    /// Enable with AI003_ARENA=1 (the frame-synced RoomClient stays untouched).
    /// </summary>
    public sealed class ArenaClient : MonoBehaviour
    {
        private struct Cmd
        {
            public uint Seq;
            public int Forward;
            public int Side;
            public byte Buttons;
        }

        private sealed class Snapshot
        {
            public uint Seq;
            public uint Ack;
            public Dictionary<int, Vector3> Origins = new Dictionary<int, Vector3>();
            public Dictionary<int, int> Health = new Dictionary<int, int>();
        }

        private ConnectionBootstrap _connection;
        private ThirdPersonController _local;
        private string _playerId;
        private int _slot;
        private uint _seq;
        private readonly List<Cmd> _pending = new List<Cmd>();
        private readonly Dictionary<uint, Snapshot> _snapshots = new Dictionary<uint, Snapshot>();
        private Snapshot _latest;
        private Vector3 _predicted;
        private float _tickTimer;
        private readonly Dictionary<string, RemoteAvatar> _remotes = new Dictionary<string, RemoteAvatar>();

        private struct RemoteInterp
        {
            public Vector3 From;
            public Vector3 To;
            public float Start;
            public float Extrapolate;
        }

        private readonly Dictionary<int, RemoteInterp> _interp = new Dictionary<int, RemoteInterp>();
        private const float InterpDelay = 0.1f;
        private bool _inRoom;
        private bool _useUdp;
        private RudpTransport _udp;
        private uint _lastSnapshotSeq;

        public bool InRoom => _inRoom;
        public int RemoteCount => _interp.Count;
        private int _debugForward;
        private int _debugSide;
        private float _debugUntil;

        /// <summary>Test hook: hold a movement command for a number of seconds.</summary>
        public void DebugHold(int forward, int side, float seconds)
        {
            _debugForward = forward;
            _debugSide = side;
            _debugUntil = Time.unscaledTime + Mathf.Max(0.05f, seconds);
        }

        private void OnEnable()
        {
            _connection = ConnectionBootstrap.Instance ?? FindFirstObjectByType<ConnectionBootstrap>();
            if (_connection != null) _connection.MessageReceived += HandleMessage;
        }

        private void OnDisable()
        {
            if (_connection != null) _connection.MessageReceived -= HandleMessage;
        }

        private void Start()
        {
            if (System.Environment.GetEnvironmentVariable("AI003_ARENA") != "1")
            {
                enabled = false;
                return;
            }
            if (_connection == null || !_connection.IsConnected)
            {
                Debug.LogWarning("[AI003][ARENA] no connection");
                enabled = false;
                return;
            }
            var body = new
            {
                name = "ArenaPlayer",
                room = System.Environment.GetEnvironmentVariable("AI003_ROOM_NAME") ?? "",
                sync = "arena"
            };
            _connection.Send(new Envelope { Type = "room.enter", Seq = _connection.NextSeq(), Body = JObject.FromObject(body) });
        }

        private void SendRoom(Envelope envelope)
        {
            if (_useUdp && _udp != null && _udp.Active)
            {
                _udp.Send(envelope);
                return;
            }
            if (_connection != null) _connection.Send(envelope);
        }
        private void HandleMessage(Envelope envelope)
        {
            switch (envelope.Type)
            {
                case "room.entered":
                    OnEntered(envelope.Body as JObject);
                    break;
                case "room.arena.snap":
                case "room.arena.delta":
                    OnSnapshot(envelope.Body as JObject);
                    break;
                case "room.left":
                    _inRoom = false;
                    break;
                case "error":
                    Debug.LogWarning($"[AI003][ARENA] error: {envelope.Body}");
                    break;
            }
        }

        private void OnEntered(JObject body)
        {
            if (body == null) return;
            if (body["sync"]?.ToString() != "arena")
            {
                Debug.LogWarning("[AI003][ARENA] entered a non-arena room");
                enabled = false;
                return;
            }
            _playerId = body["playerId"]?.ToString();
            _slot = body["state"]?["slot"]?.Value<int>() ?? 0;
            _local = FindFirstObjectByType<ThirdPersonController>();
            if (_local != null)
            {
                _local.enabled = false;
                _predicted = _local.transform.position;
            }
            var udp = body["udp"] as JObject;
            if (udp != null && udp["addr"] != null)
            {
                _udp = GetComponent<RudpTransport>();
                if (_udp == null) _udp = gameObject.AddComponent<RudpTransport>();
                _udp.Message += HandleMessage;
                _udp.Connect(udp["addr"].ToString(), udp["token"]?.ToString() ?? "");
                _useUdp = true;
            }
            _inRoom = true;
            Debug.Log($"[AI003][ARENA] entered {body["roomId"]} slot={_slot} udp={_useUdp}");
        }

        private void Update()
        {
            if (!_inRoom || _connection == null) return;
            UpdateInterpolatedRemotes();
            _tickTimer += Time.deltaTime;
            if (_tickTimer < 0.05f) return;
            _tickTimer = 0f;

            Vector2 move = ReadMove();
            bool debug = Time.unscaledTime < _debugUntil;
            if (debug) move = new Vector2(_debugSide, _debugForward);
            Camera camera = Camera.main;
            Vector3 forward = camera != null ? Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized : Vector3.forward;
            Vector3 right = camera != null ? Vector3.ProjectOnPlane(camera.transform.right, Vector3.up).normalized : Vector3.right;
            Vector3 wish = forward * move.y + right * move.x;
            int side = Mathf.Abs(wish.x) > 0.5f ? (int)Mathf.Sign(wish.x) : 0;
            int fwd = Mathf.Abs(wish.z) > 0.5f ? (int)Mathf.Sign(wish.z) : 0;

            var keyboard = Keyboard.current;
            byte buttons = keyboard != null && keyboard.jKey.isPressed ? (byte)1 : (byte)0;

            _seq++;
            var cmd = new Cmd { Seq = _seq, Forward = fwd, Side = side, Buttons = buttons };
            _pending.Add(cmd);
            Predict(cmd);
            ApplyPredicted();

            SendRoom(new Envelope
            {
                Type = "room.arena.cmd",
                Seq = _connection.NextSeq(),
                Body = JObject.FromObject(new { seq = cmd.Seq, msec = 50, yaw = 0, pitch = 0, forward = cmd.Forward, side = cmd.Side, up = 0, buttons = cmd.Buttons })
            });
        }

        private static Vector2 ReadMove()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return Vector2.zero;
            float x = 0f, z = 0f;
            if (keyboard.wKey.isPressed) z += 1f;
            if (keyboard.sKey.isPressed) z -= 1f;
            if (keyboard.dKey.isPressed) x += 1f;
            if (keyboard.aKey.isPressed) x -= 1f;
            return new Vector2(x, z);
        }

        private void Predict(Cmd cmd)
        {
            float step = 0.25f;
            if (cmd.Forward != 0 && cmd.Side != 0) step = 0.177f;
            _predicted.x += cmd.Side * step;
            _predicted.z += cmd.Forward * step;
            var bootstrap = AI003.World.WorldBootstrap.Instance;
            if (bootstrap != null && bootstrap.Data != null)
            {
                float height = bootstrap.Data.HeightAt(_predicted.x, _predicted.z);
                if (_predicted.y < height) _predicted.y = height;
            }
        }

        private void ApplyPredicted()
        {
            if (_local != null) _local.transform.position = _predicted;
        }

        private void OnSnapshot(JObject body)
        {
            if (body == null) return;
            uint seq = body["seq"]?.Value<uint>() ?? 0;
            if (seq <= _lastSnapshotSeq) return;
            _lastSnapshotSeq = seq;
            uint ack = body["ack"]?.Value<uint>() ?? 0;
            uint baseSeq = body["base"]?.Value<uint>() ?? 0;

            Snapshot snap;
            if (baseSeq != 0 && _snapshots.TryGetValue(baseSeq, out var baseSnap))
            {
                snap = Clone(baseSnap);
            }
            else
            {
                snap = new Snapshot();
            }
            snap.Seq = seq;
            snap.Ack = ack;

            var players = body["players"] as JArray;
            if (players != null)
            {
                foreach (var entry in players)
                {
                    int id = entry["id"]?.Value<int>() ?? 0;
                    int mask = entry["mask"]?.Value<int>() ?? 0x1f;
                    var origin = entry["origin"] as JArray;
                    if (origin != null && origin.Count == 3 && (mask & 1) != 0)
                    {
                        snap.Origins[id] = new Vector3(origin[0].Value<int>() / 1000f, origin[1].Value<int>() / 1000f, origin[2].Value<int>() / 1000f);
                    }
                    if ((mask & 4) != 0) snap.Health[id] = entry["health"]?.Value<int>() ?? 0;
                }
            }
            var removed = body["removed"] as JArray;
            if (removed != null)
            {
                foreach (var id in removed)
                {
                    int value = id.Value<int>();
                    snap.Origins.Remove(value);
                    snap.Health.Remove(value);
                }
            }

            _snapshots[seq] = snap;
            if (_snapshots.Count > 64)
            {
                uint oldest = seq;
                foreach (var key in _snapshots.Keys) if (key < oldest) oldest = key;
                _snapshots.Remove(oldest);
            }
            _latest = snap;

            int localId = _slot + 1;
            if (snap.Origins.TryGetValue(localId, out Vector3 authoritative))
            {
                // Reconcile: restart from the authoritative position and replay unacked cmds.
                _predicted = authoritative;
                _pending.RemoveAll(cmd => cmd.Seq <= ack);
                foreach (var cmd in _pending) Predict(cmd);
                ApplyPredicted();
            }

            foreach (var pair in snap.Origins)
            {
                if (pair.Key == localId) continue;
                Vector3 previous = _interp.TryGetValue(pair.Key, out RemoteInterp old) ? old.To : pair.Value;
                _interp[pair.Key] = new RemoteInterp
                {
                    From = previous,
                    To = pair.Value,
                    Start = Time.time,
                    Extrapolate = 0f
                };
            }

            SendRoom(new Envelope { Type = "room.arena.ack", Seq = 0, Body = JObject.FromObject(new { seq }) });
        }

        private static Snapshot Clone(Snapshot source)
        {
            return new Snapshot
            {
                Seq = source.Seq,
                Ack = source.Ack,
                Origins = new Dictionary<int, Vector3>(source.Origins),
                Health = new Dictionary<int, int>(source.Health)
            };
        }

        private void UpdateRemote(int id, Vector3 position)
        {
            if (_remotes.TryGetValue(id.ToString(), out RemoteAvatar avatar) && avatar != null)
            {
                avatar.transform.position = position;
                return;
            }
            GameObject prefab = Resources.Load<GameObject>("Characters/KenneyProtagonists/Player");
            if (prefab == null) return;
            GameObject instance = Instantiate(prefab, position, Quaternion.identity);
            instance.name = "Arena_" + id;
            var created = instance.AddComponent<AI003.World.RemoteAvatar>();
            created.Apply(position, 0f, "idle");
            _remotes[id.ToString()] = created;
        }

        private void UpdateInterpolatedRemotes()
        {
            foreach (var pair in _interp)
            {
                RemoteInterp state = pair.Value;
                float elapsed = Time.time - state.Start;
                float t = Mathf.Clamp01(elapsed / InterpDelay);
                Vector3 position = Vector3.Lerp(state.From, state.To, t);
                if (elapsed > InterpDelay)
                {
                    // Extrapolate up to 200 ms using the last segment velocity.
                    Vector3 velocity = (state.To - state.From) / InterpDelay;
                    float extra = Mathf.Min(elapsed - InterpDelay, 0.2f);
                    position = state.To + velocity * extra;
                }
                UpdateRemote(pair.Key, position);
            }
        }
    }
}
