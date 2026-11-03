using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace AI003.Net
{
    /// <summary>
    /// Thread-safe WebSocket client for the AI003 gateway.
    ///
    /// Networking happens on background threads; incoming frames are queued so
    /// the Unity main thread can drain them with <see cref="TryDequeue"/>.
    /// No Unity API is touched from the receive loop except thread-safe logging.
    /// </summary>
    public sealed class GatewayClient : IDisposable
    {
        private readonly Uri _url;
        private readonly ConcurrentQueue<Envelope> _inbox = new ConcurrentQueue<Envelope>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private int _disposed;

        public GatewayClient(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url is empty", nameof(url));
            _url = new Uri(url);
        }

        public Uri Url => _url;

        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        /// <summary>Opens the socket. Safe to await from any thread.</summary>
        public async Task ConnectAsync(TimeSpan timeout)
        {
            ThrowIfDisposed();
            if (IsConnected) return;

            _cts = new CancellationTokenSource();
            _socket = new ClientWebSocket();
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeoutCts.CancelAfter(timeout);
                await _socket.ConnectAsync(_url, timeoutCts.Token).ConfigureAwait(false);
            }

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        /// <summary>Sends one envelope as a text frame.</summary>
        public async Task SendAsync(Envelope envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (!IsConnected) throw new InvalidOperationException("gateway is not connected");

            string json = JsonConvert.SerializeObject(envelope);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>True when a complete frame is available for the main thread.</summary>
        public bool TryDequeue(out Envelope envelope) => _inbox.TryDequeue(out envelope);

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder(8192);

            try
            {
                while (!ct.IsCancellationRequested && _socket != null && _socket.State == WebSocketState.Open)
                {
                    text.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                                .ConfigureAwait(false);
                            return;
                        }
                        text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    string raw = text.ToString();
                    try
                    {
                        var envelope = JsonConvert.DeserializeObject<Envelope>(raw);
                        if (envelope != null)
                        {
                            envelope.Raw = raw;
                            _inbox.Enqueue(envelope);
                        }
                    }
                    catch (JsonException e)
                    {
                        Debug.LogWarning($"[AI003] malformed frame from {_url}: {e.Message} :: {raw}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested)
                {
                    Debug.LogWarning($"[AI003] receive loop stopped: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _socket?.Abort(); } catch (ObjectDisposedException) { }
            try { _socket?.Dispose(); } catch (ObjectDisposedException) { }
            _socket = null;

            _cts?.Dispose();
            _cts = null;
            _sendLock.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(GatewayClient));
        }
    }
}
