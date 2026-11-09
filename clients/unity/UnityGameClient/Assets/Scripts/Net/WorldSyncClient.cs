using System.Collections.Generic;
using AI003.Player;
using AI003.World;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AI003.Net
{
    /// <summary>
    /// Protocol v2 gameplay loop: joins the gateway as a player, reports the local
    /// avatar at <see cref="SendRateHz"/>, and mirrors every other player in the
    /// snapshots as a <see cref="AI003.World.RemoteAvatar"/>.
    ///
    /// Installed automatically before the first scene loads, next to
    /// <see cref="ConnectionBootstrap"/>; nothing has to be wired per scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldSyncClient : MonoBehaviour
    {
        public static WorldSyncClient Instance { get; private set; }

        private const string RemotePrefabPath = "Characters/KenneyProtagonists/Player";
        private const string ClientKind = "unity";

        [SerializeField] private float _sendRateHz = 10f;
        [SerializeField] private float _joinRetrySeconds = 1f;

        private readonly Dictionary<string, RemoteAvatar> _remotes = new Dictionary<string, RemoteAvatar>();
        private readonly List<string> _stale = new List<string>();
        private readonly List<Envelope> _inbox = new List<Envelope>();

        private ConnectionBootstrap _connection;
        private ThirdPersonController _local;
        private GameObject _remotePrefab;

        private string _playerId;
        private int _playersSeen;
        private int _cursor;
        private float _nextSendAt;
        private float _nextJoinAt;
        private GUIStyle _style;

        public bool Joined => !string.IsNullOrEmpty(_playerId);
        public string LocalPlayerId => _playerId;
        public int RemoteCount => _remotes.Count;
        public int PlayersSeen => _playersSeen;

        /// <summary>True while the combat room owns the avatar and hides world peers.</summary>
        public bool Suspended { get; private set; }

        public void Suspend()
        {
            Suspended = true;
            ClearRemotes();
        }

        public void Resume() => Suspended = false;

        private void ClearRemotes()
        {
            foreach (var avatar in _remotes.Values)
            {
                if (avatar != null) Destroy(avatar.gameObject);
            }
            _remotes.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInstall()
        {
            EnsureExists();
        }

        /// <summary>Finds the live sync component (or creates one).</summary>
        public static WorldSyncClient EnsureExists()
        {
            if (Instance != null) return Instance;

            var existing = FindFirstObjectByType<WorldSyncClient>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            var go = new GameObject("[AI003] Sync");
            DontDestroyOnLoad(go);
            return go.AddComponent<WorldSyncClient>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_connection == null)
            {
                _connection = ConnectionBootstrap.Instance;
                if (_connection == null)
                {
                    _connection = FindFirstObjectByType<ConnectionBootstrap>();
                }
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
                ResetSession();
                return;
            }

            if (_local == null)
            {
                _local = FindFirstObjectByType<ThirdPersonController>();
            }
            if (_local == null) return;

            if (!Joined)
            {
                if (Time.unscaledTime >= _nextJoinAt)
                {
                    _nextJoinAt = Time.unscaledTime + _joinRetrySeconds;
                    SendJoin();
                }
                return;
            }

            if (Suspended) return;

            if (Time.unscaledTime >= _nextSendAt)
            {
                _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, _sendRateHz);
                SendMove();
            }
        }

        private void SendJoin()
        {
            var body = new JObject
            {
                ["client"] = ClientKind,
                ["name"] = "UnityPlayer",
                ["clientId"] = ClientIdentity.Id
            };
            _connection.Send(new Envelope { Type = "join", Seq = _connection.NextSeq(), Body = body });
        }

        private void SendMove()
        {
            Vector3 position = _local.transform.position;
            var body = new JObject
            {
                ["pos"] = new JObject
                {
                    ["x"] = System.Math.Round(position.x, 3),
                    ["y"] = System.Math.Round(position.y, 3),
                    ["z"] = System.Math.Round(position.z, 3)
                },
                ["yaw"] = System.Math.Round(_local.transform.eulerAngles.y, 2),
                ["speed"] = System.Math.Round(_local.PlanarSpeed, 2),
                ["grounded"] = _local.IsGrounded,
                ["swimming"] = _local.IsSwimming
            };
            _connection.Send(new Envelope { Type = "move", Seq = _connection.NextSeq(), Body = body });
        }

        private void HandleMessage(Envelope envelope)
        {
            switch (envelope.Type)
            {
                case "joined":
                    _playerId = envelope.Body?["playerId"]?.ToString();
                    if (Joined) Debug.Log($"[AI003] joined as {_playerId} ({ClientKind})");
                    break;

                case "snapshot":
                    ApplySnapshot(envelope.Body as JObject);
                    break;

                case "error":
                    Debug.LogWarning($"[AI003] sync error: {envelope.Body}");
                    break;
            }
        }

        private void ApplySnapshot(JObject body)
        {
            if (Suspended) return;

            var players = body?["players"] as JArray;
            if (players == null) return;

            _playersSeen = players.Count;
            _stale.Clear();
            foreach (var entry in _remotes.Keys) _stale.Add(entry);

            foreach (var token in players)
            {
                string id = token["id"]?.ToString();
                if (string.IsNullOrEmpty(id) || id == _playerId) continue;

                var positionNode = token["pos"];
                var position = new Vector3(
                    positionNode?["x"]?.Value<float>() ?? 0f,
                    positionNode?["y"]?.Value<float>() ?? 0f,
                    positionNode?["z"]?.Value<float>() ?? 0f);
                float yaw = token["yaw"]?.Value<float>() ?? 0f;
                string anim = token["anim"]?.ToString();

                if (!_remotes.TryGetValue(id, out RemoteAvatar avatar) || avatar == null)
                {
                    avatar = SpawnRemote(id, position, yaw);
                    if (avatar == null) continue;
                    _remotes[id] = avatar;
                }

                avatar.Apply(position, yaw, anim);
                _stale.Remove(id);
            }

            foreach (string id in _stale)
            {
                if (_remotes.TryGetValue(id, out RemoteAvatar avatar) && avatar != null)
                {
                    Destroy(avatar.gameObject);
                }
                _remotes.Remove(id);
            }
        }

        private RemoteAvatar SpawnRemote(string id, Vector3 position, float yaw)
        {
            if (_remotePrefab == null)
            {
                _remotePrefab = Resources.Load<GameObject>(RemotePrefabPath);
                if (_remotePrefab == null)
                {
                    Debug.LogWarning($"[AI003] remote prefab missing at Resources/{RemotePrefabPath}");
                    return null;
                }
            }

            GameObject instance = Instantiate(_remotePrefab, position, Quaternion.Euler(0f, yaw, 0f));
            instance.name = "Remote_" + id;
            var avatar = instance.AddComponent<RemoteAvatar>();
            avatar.Apply(position, yaw, "idle");
            return avatar;
        }

        private void ResetSession()
        {
            if (_playerId == null && _remotes.Count == 0) return;
            _playerId = null;
            _playersSeen = 0;
            foreach (var avatar in _remotes.Values)
            {
                if (avatar != null) Destroy(avatar.gameObject);
            }
            _remotes.Clear();
        }

        private void OnGUI()
        {
            if (!Joined) return;
            if (RoomClient.AnyActive) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(12, Screen.height / 60),
                    normal = { textColor = new Color(1f, 1f, 1f, 0.85f) }
                };
            }

            double ping = ConnectionBootstrap.Instance != null ? ConnectionBootstrap.Instance.LastPingMs : 0;
            GUI.Label(
                new Rect(16f, Screen.height - 36f, 520f, 24f),
                $"sync  id {_playerId}   players {_playersSeen}   remote {_remotes.Count}   ping {ping:F0} ms",
                _style);
        }
    }
}
