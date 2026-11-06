using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AI003.Net
{
    /// <summary>
    /// Reliable ordered UDP transport (the RUDP protocol used by the Go gateway).
    /// Room messages can be routed through this instead of the websocket.
    /// </summary>
    public sealed class RudpTransport : MonoBehaviour
    {
        private const uint Magic = 0x41335255;
        private const byte Version = 1;
        private const byte TypeData = 1;
        private const byte TypeAck = 2;
        private const byte TypeHello = 3;
        private const int HeaderSize = 28;
        private const int MaxPayload = 1200;
        private const int MaxTries = 12;

        public static RudpTransport Instance { get; private set; }
        public bool Active { get; private set; }
        public event Action<Envelope> Message;

        private UdpClient _socket;
        private IPEndPoint _remote;
        private uint _session;
        private Thread _reader;
        private volatile bool _running;
        private readonly object _gate = new object();

        private uint _sendSeq;
        private uint _recvNext = 1;
        private uint _lastAck;
        private readonly Dictionary<uint, byte[]> _unacked = new Dictionary<uint, byte[]>();
        private readonly Dictionary<uint, DateTime> _sentAt = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, int> _tries = new Dictionary<uint, int>();
        private readonly Dictionary<uint, byte[]> _reorder = new Dictionary<uint, byte[]>();
        private readonly Dictionary<ushort, List<byte[]>> _frags = new Dictionary<ushort, List<byte[]>>();
        private readonly ConcurrentQueue<Envelope> _incoming = new ConcurrentQueue<Envelope>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void Connect(string addr, string token)
        {
            if (Active) return;
            string[] parts = addr.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            {
                Debug.LogWarning($"[AI003][RUDP] bad address {addr}");
                return;
            }
            _remote = new IPEndPoint(IPAddress.Parse(parts[0]), port);
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            _socket = new UdpClient(0);
            _socket.Connect(_remote);
            Active = true;
            _running = true;

            byte[] hello = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { token }));
            SendPacket(TypeHello, hello);

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "AI003-RUDP" };
            _reader.Start();
            Debug.Log($"[AI003][RUDP] connecting {addr} session={_session}");
        }

        public void Send(Envelope envelope)
        {
            if (!Active) return;
            byte[] json = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(envelope));
            lock (_gate)
            {
                int chunk = MaxPayload - HeaderSize - 4;
                int count = Math.Max(1, (json.Length + chunk - 1) / chunk);
                if (count > 255) return;
                ushort msgId = (ushort)_sendSeq;
                for (int i = 0; i < count; i++)
                {
                    int start = i * chunk;
                    int len = Math.Min(chunk, json.Length - start);
                    byte[] payload = new byte[4 + len];
                    payload[0] = (byte)(msgId & 0xFF);
                    payload[1] = (byte)(msgId >> 8);
                    payload[2] = (byte)i;
                    payload[3] = (byte)count;
                    Array.Copy(json, start, payload, 4, len);
                    SendPacket(TypeData, payload);
                }
            }
        }

        private void SendPacket(byte type, byte[] payload)
        {
            _sendSeq++;
            byte[] pkt = new byte[HeaderSize + payload.Length];
            WriteU32(pkt, 0, Magic);
            pkt[4] = Version;
            pkt[5] = type;
            WriteU32(pkt, 6, _session);
            WriteU32(pkt, 10, _sendSeq);
            WriteU32(pkt, 14, _lastAck);
            WriteU32(pkt, 18, 0);
            pkt[22] = (byte)(payload.Length & 0xFF);
            pkt[23] = (byte)(payload.Length >> 8);
            Array.Copy(payload, 0, pkt, 28, payload.Length);
            WriteU32(pkt, 24, Crc32(pkt, 0, 24));
            try { _socket.Send(pkt, pkt.Length); } catch (Exception e) { Debug.LogWarning($"[AI003][RUDP] send: {e.Message}"); }
            _unacked[_sendSeq] = pkt;
            _sentAt[_sendSeq] = DateTime.UtcNow;
            _tries[_sendSeq] = 0;
        }

        private void ReadLoop()
        {
            IPEndPoint remote = null;
            while (_running)
            {
                try
                {
                    byte[] pkt = _socket.Receive(ref remote);
                    HandlePacket(pkt);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void HandlePacket(byte[] pkt)
        {
            if (pkt.Length < HeaderSize || ReadU32(pkt, 0) != Magic || pkt[4] != Version || ReadU32(pkt, 6) != _session) return;
            if (ReadU32(pkt, 24) != Crc32(pkt, 0, 24)) return;
            byte type = pkt[5];
            uint seq = ReadU32(pkt, 10);
            uint ack = ReadU32(pkt, 14);
            int len = pkt[22] | (pkt[23] << 8);
            lock (_gate)
            {
                if (ack != 0)
                {
                    _unacked.Remove(ack);
                    _sentAt.Remove(ack);
                    _tries.Remove(ack);
                }
                if (type == TypeAck) return;
                _lastAck = seq;
                if (seq == _recvNext)
                {
                    Accept(pkt, len);
                    while (_reorder.TryGetValue(_recvNext, out byte[] next))
                    {
                        _reorder.Remove(_recvNext);
                        Accept(next, next.Length);
                    }
                }
                else if (seq > _recvNext)
                {
                    _reorder[seq] = pkt;
                }
                SendPacket(TypeAck, Array.Empty<byte>());
            }
        }

        private void Accept(byte[] pkt, int len)
        {
            _recvNext++;
            byte[] payload = new byte[len];
            Array.Copy(pkt, HeaderSize, payload, 0, len);
            if (payload.Length < 4) return;
            ushort msgId = (ushort)(payload[0] | (payload[1] << 8));
            int index = payload[2], count = payload[3];
            int bodyLen = payload.Length - 4;
            if (count <= 1)
            {
                Deliver(payload, 4, bodyLen);
                return;
            }
            if (!_frags.TryGetValue(msgId, out List<byte[]> parts))
            {
                parts = new List<byte[]>(new byte[count][]);
                _frags[msgId] = parts;
            }
            if (index >= count || parts[index] != null) return;
            byte[] body = new byte[bodyLen];
            Array.Copy(payload, 4, body, 0, bodyLen);
            parts[index] = body;
            foreach (byte[] p in parts) if (p == null) return;
            int total = 0;
            foreach (byte[] p in parts) total += p.Length;
            byte[] joined = new byte[total];
            int offset = 0;
            foreach (byte[] p in parts) { Array.Copy(p, 0, joined, offset, p.Length); offset += p.Length; }
            _frags.Remove(msgId);
            Deliver(joined, 0, joined.Length);
        }

        private void Deliver(byte[] data, int offset, int length)
        {
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(data, offset, length);
                Envelope envelope = JsonConvert.DeserializeObject<Envelope>(json);
                if (envelope != null) _incoming.Enqueue(envelope);
            }
            catch (Exception e) { Debug.LogWarning($"[AI003][RUDP] decode: {e.Message}"); }
        }

        private void Update()
        {
            if (!Active) return;
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                foreach (var pair in _unacked)
                {
                    if (!_sentAt.TryGetValue(pair.Key, out DateTime sent)) continue;
                    int tries = _tries[pair.Key];
                    double wait = 0.12 * (tries + 1);
                    if ((now - sent).TotalSeconds < wait) continue;
                    if (tries >= MaxTries) { Active = false; return; }
                    _tries[pair.Key] = tries + 1;
                    _sentAt[pair.Key] = now;
                    try { _socket.Send(pair.Value, pair.Value.Length); } catch { }
                }
            }
            while (_incoming.TryDequeue(out Envelope envelope))
            {
                Message?.Invoke(envelope);
            }
        }

        private void OnDestroy()
        {
            _running = false;
            try { _socket?.Close(); } catch { }
            if (Instance == this) Instance = null;
        }

        private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        private static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
        private static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
