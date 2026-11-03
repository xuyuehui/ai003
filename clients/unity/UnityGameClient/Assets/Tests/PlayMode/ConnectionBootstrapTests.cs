using System.Collections;
using AI003.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    /// <summary>
    /// Verifies the path a developer actually uses: press Play in any scene and
    /// ConnectionBootstrap installs itself, connects and exchanges pings.
    /// Requires the Go gateway to be running (scripts/smoke-unity.ps1 starts it).
    /// </summary>
    public class ConnectionBootstrapTests
    {
        private const float TimeoutSeconds = 25f;

        [UnityTest]
        public IEnumerator ConnectsAndReceivesPong()
        {
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;

            while (Time.realtimeSinceStartup < deadline && ConnectionBootstrap.Instance == null)
            {
                yield return null;
            }

            Assert.IsNotNull(ConnectionBootstrap.Instance, "ConnectionBootstrap was not auto-installed before scene load");

            while (Time.realtimeSinceStartup < deadline && ConnectionBootstrap.Instance.PongsReceived == 0)
            {
                yield return null;
            }

            Assert.GreaterOrEqual(
                ConnectionBootstrap.Instance.PongsReceived, 1,
                $"no pong from {ConnectionBootstrap.Instance.Endpoint} within {TimeoutSeconds}s");
            Assert.GreaterOrEqual(ConnectionBootstrap.Instance.LastPingMs, 0.0);
        }
    }
}
