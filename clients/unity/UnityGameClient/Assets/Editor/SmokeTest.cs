using System;
using System.Diagnostics;
using System.Threading;
using AI003.Net;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AI003.EditorTools
{
    /// <summary>
    /// Headless connectivity check used by scripts/smoke-unity.ps1.
    ///
    /// Unity.exe -batchmode -nographics -projectPath clients/unity/UnityGameClient \
    ///   -executeMethod AI003.EditorTools.SmokeTest.Run -quit
    ///
    /// Connects to the Go gateway, waits for the welcome frame, then sends three
    /// pings and requires three matching pongs. Exits non-zero on failure.
    /// </summary>
    public static class SmokeTest
    {
        private const int PingCount = 3;
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(15);

        public static void Run()
        {
            bool ok;
            try
            {
                ok = RunBlocking();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][SMOKE] exception: {e}");
                ok = false;
            }

            Debug.Log(ok ? "[AI003][SMOKE] PASS" : "[AI003][SMOKE] FAIL");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        private static bool RunBlocking()
        {
            string url = Environment.GetEnvironmentVariable("AI003_WS_URL");
            if (string.IsNullOrWhiteSpace(url)) url = "ws://127.0.0.1:8080/ws";
            url = url.Trim();

            Debug.Log($"[AI003][SMOKE] connecting to {url}");

            using var client = new GatewayClient(url);
            client.ConnectAsync(ConnectTimeout).GetAwaiter().GetResult();
            Debug.Log("[AI003][SMOKE] socket open");

            if (!WaitForWelcome(client, WelcomeTimeout))
            {
                Debug.LogError("[AI003][SMOKE] no welcome frame received");
                return false;
            }

            for (int i = 1; i <= PingCount; i++)
            {
                var body = new JObject
                {
                    ["client"] = "unity-editor",
                    ["sentAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var stopwatch = Stopwatch.StartNew();
                client.SendAsync(new Envelope { Type = "ping", Seq = i, Body = body }).GetAwaiter().GetResult();

                long? serverTime = WaitForPong(client, i, PongTimeout);
                if (serverTime == null)
                {
                    Debug.LogError($"[AI003][SMOKE] ping seq={i} timed out after {PongTimeout.TotalSeconds:F0}s");
                    return false;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Debug.Log($"[AI003][SMOKE] pong seq={i} rtt={(now - serverTime.Value)}ms wall={stopwatch.ElapsedMilliseconds}ms");
            }

            Debug.Log($"[AI003][SMOKE] received {PingCount}/{PingCount} pongs from {url}");
            return true;
        }

        private static bool WaitForWelcome(GatewayClient client, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                while (client.TryDequeue(out Envelope envelope))
                {
                    if (envelope.Type == "welcome")
                    {
                        Debug.Log($"[AI003][SMOKE] welcome {envelope.Body}");
                        return true;
                    }
                }
                Thread.Sleep(10);
            }
            return false;
        }

        private static long? WaitForPong(GatewayClient client, long seq, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                while (client.TryDequeue(out Envelope envelope))
                {
                    if (envelope.Type == "pong" && envelope.Seq == seq)
                    {
                        if (envelope.Body != null && envelope.Body.TryGetValue("serverTime", out JToken token))
                        {
                            return token.Value<long>();
                        }
                        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }

                    if (envelope.Type == "error")
                    {
                        Debug.LogWarning($"[AI003][SMOKE] gateway error seq={envelope.Seq}: {envelope.Body}");
                    }
                }
                Thread.Sleep(10);
            }
            return null;
        }
    }
}
