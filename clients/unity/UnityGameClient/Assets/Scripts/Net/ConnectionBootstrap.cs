using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AI003.Net
{
    /// <summary>
    /// Keeps one gateway connection alive for the whole session.
    ///
    /// Installed automatically before the first scene loads, so any scene can be
    /// played without wiring anything up. Override the endpoint with the
    /// <c>AI003_WS_URL</c> environment variable or by editing the inspector.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConnectionBootstrap : MonoBehaviour
    {
        public static ConnectionBootstrap Instance { get; private set; }

        private const string DefaultUrl = "ws://127.0.0.1:8080/ws";

        [SerializeField] private string _url = DefaultUrl;
        [SerializeField] private float _pingIntervalSeconds = 2f;
        [SerializeField] private float _reconnectDelaySeconds = 3f;

        private GatewayClient _client;
        private long _seq;
        private long _lastPingSentAtMs;
        private float _nextPingAt;
        private float _nextReconnectAt;
        private bool _connecting;

        // Every received frame is also appended to a ring buffer that gameplay
        // systems read with independent cursors. A shared queue would let one
        // consumer (world sync) swallow frames meant for another (room sync);
        // the ring keeps every consumer independent and survives domain reloads.
        private readonly object _ringLock = new object();
        private readonly Envelope[] _ring = new Envelope[2048];
        private int _ringWritten;

        /// <summary>Copies every frame newer than <paramref name="cursor"/>.</summary>
        public void ReadFrames(ref int cursor, List<Envelope> into)
        {
            if (into == null) return;
            lock (_ringLock)
            {
                int oldest = Mathf.Max(0, _ringWritten - _ring.Length);
                if (cursor < oldest) cursor = oldest;
                for (; cursor < _ringWritten; cursor++)
                {
                    into.Add(_ring[cursor % _ring.Length]);
                }
            }
        }

        /// <summary>Last pong round-trip in milliseconds, for HUD/debug output.</summary>
        public double LastPingMs { get; private set; }

        /// <summary>Total pongs received since start.</summary>
        public int PongsReceived { get; private set; }

        public string Endpoint => _url;

        /// <summary>Raised on the main thread for every frame the gateway sends.</summary>
        public event Action<Envelope> MessageReceived;

        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>Next envelope sequence number, shared with gameplay code.</summary>
        public long NextSeq() => ++_seq;

        /// <summary>Fire-and-forget send; failures are logged, not thrown.</summary>
        public async void Send(Envelope envelope)
        {
            var client = _client;
            if (client == null || !client.IsConnected || envelope == null) return;
            try
            {
                await client.SendAsync(envelope);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AI003] send {envelope.Type} {envelope.Seq} failed: {e.GetType().Name}: {e.Message}");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInstall()
        {
            if (Instance != null) return;
            var go = new GameObject("[AI003] Connection");
            DontDestroyOnLoad(go);
            go.AddComponent<ConnectionBootstrap>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            string env = Environment.GetEnvironmentVariable("AI003_WS_URL");
            if (!string.IsNullOrWhiteSpace(env)) _url = env.Trim();
        }

        private void Start()
        {
            TryConnect();
        }

        private void Update()
        {
            // A script reload wipes statics while the DontDestroyOnLoad object
            // survives; re-register so gameplay code can always find us.
            if (Instance == null) Instance = this;
            if (WorldSyncClient.Instance == null) WorldSyncClient.EnsureExists();

            if (_client == null)
            {
                if (Time.unscaledTime >= _nextReconnectAt) TryConnect();
                return;
            }

            while (_client.TryDequeue(out Envelope envelope))
            {
                Handle(envelope);
            }

            if (!_client.IsConnected)
            {
                if (Time.unscaledTime >= _nextReconnectAt)
                {
                    Debug.LogWarning($"[AI003] connection lost, reconnecting to {_url}");
                    DisposeClient();
                    _nextReconnectAt = Time.unscaledTime + _reconnectDelaySeconds;
                }
                return;
            }

            if (Time.unscaledTime >= _nextPingAt)
            {
                _nextPingAt = Time.unscaledTime + _pingIntervalSeconds;
                SendPing();
            }
        }

        private async void TryConnect()
        {
            if (_connecting) return;
            _connecting = true;
            _nextReconnectAt = Time.unscaledTime + _reconnectDelaySeconds;

            var client = new GatewayClient(_url);
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(10));
                _client = client;
                _nextPingAt = Time.unscaledTime + 0.1f;
                Debug.Log($"[AI003] connected to {_url}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AI003] connect to {_url} failed: {e.GetType().Name}: {e.Message}");
                client.Dispose();
            }
            finally
            {
                _connecting = false;
            }
        }

        private async void SendPing()
        {
            var client = _client;
            if (client == null || !client.IsConnected) return;

            long seq = ++_seq;
            _lastPingSentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var body = new JObject
            {
                ["client"] = "unity",
                ["sentAt"] = _lastPingSentAtMs
            };

            try
            {
                await client.SendAsync(new Envelope { Type = "ping", Seq = seq, Body = body });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AI003] send ping {seq} failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private void Handle(Envelope envelope)
        {
            MessageReceived?.Invoke(envelope);

            // Bound the ring implicitly (it overwrites) and let every consumer
            // read independently; see ReadFrames.
            lock (_ringLock)
            {
                _ring[_ringWritten % _ring.Length] = envelope;
                _ringWritten++;
            }

            switch (envelope.Type)
            {
                case "welcome":
                    Debug.Log($"[AI003] welcome: {envelope.Body}");
                    break;

                case "pong":
                {
                    PongsReceived++;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_lastPingSentAtMs > 0) LastPingMs = Math.Max(0, now - _lastPingSentAtMs);
                    Debug.Log($"[AI003] pong seq={envelope.Seq} rtt={LastPingMs:F0}ms");
                    break;
                }

                case "error":
                    Debug.LogWarning($"[AI003] gateway error for seq={envelope.Seq}: {envelope.Body}");
                    break;

                case "snapshot":
                    // 10 Hz stream; gameplay code consumes it via MessageReceived.
                    break;

                default:
                    Debug.Log($"[AI003] message type={envelope.Type} seq={envelope.Seq} body={envelope.Body}");
                    break;
            }
        }

        private void DisposeClient()
        {
            _client?.Dispose();
            _client = null;
        }

        private void OnDestroy()
        {
            DisposeClient();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit()
        {
            DisposeClient();
        }
    }
}
