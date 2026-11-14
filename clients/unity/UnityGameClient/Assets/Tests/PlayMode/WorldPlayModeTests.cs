using System.Collections;
using AI003.Player;
using AI003.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    /// <summary>
    /// Verifies the milestone acceptance criteria: the world builds, the player
    /// falls onto the terrain (physics + collision) and can run forward.
    ///
    /// PlayMode tests share one play session, so this test takes care of its own
    /// scene state: it never asserts on a world another test built. That matters:
    /// before the isolation was added it passed alone and failed when run after
    /// <c>AuthoredSceneTests</c> (player at -629 m = 7.67 s of free fall on the
    /// baked OpenWorld scene). See artifacts/20261114-629-verdict.md.
    /// </summary>
    public class WorldPlayModeTests
    {
        private GameObject _host;

        [UnityTest]
        public IEnumerator WorldBuildsAndPlayerLandsAndRuns()
        {
            Scene isolated = PlayModeSceneIsolation.CreateEmptyActiveScene("WorldPlayModeTests");
            yield return PlayModeSceneIsolation.UnloadOthers(isolated);

            _host = new GameObject("[test] Isolated World");
            WorldBootstrap bootstrap = _host.AddComponent<WorldBootstrap>();
            Assert.AreSame(bootstrap, WorldBootstrap.Instance,
                "WorldBootstrap.Instance is not the instance this test created — the test would " +
                "be asserting on a world inherited from another test's scene");

            // Cold cache builds the 2049x2049 world in ~18 s (measured 21:00 in
            // artifacts/20261114-isolation-fix.log); warm it is ~1.4 s. 20 s was too
            // tight, so the wait is 60 s — but IsReady still has to become true.
            float buildStart = Time.realtimeSinceStartup;
            float deadline = buildStart + 60f;

            while (Time.realtimeSinceStartup < deadline && !bootstrap.IsReady)
            {
                yield return null;
            }
            Assert.IsTrue(bootstrap.IsReady,
                $"world did not finish building within 60s (waited {Time.realtimeSinceStartup - buildStart:0.00}s)");
            Debug.Log($"[WORLDTEST] world build took {Time.realtimeSinceStartup - buildStart:0.00}s");

            ThirdPersonController player = bootstrap.Player;
            Assert.IsNotNull(player, "player was not spawned");

            Vector3 start = player.transform.position;
            Assert.Greater(start.y, bootstrap.Data.SeaLevel, "player spawned below sea level");

            // Gravity + terrain collider must settle the character.
            float groundDeadline = Time.realtimeSinceStartup + 8f;
            while (Time.realtimeSinceStartup < groundDeadline && !player.IsGrounded)
            {
                yield return null;
            }
            Assert.IsTrue(player.IsGrounded, "player never became grounded");

            float terrainHeight = bootstrap.Data.HeightAt(start.x, start.z);
            Assert.Less(Mathf.Abs(player.transform.position.y - terrainHeight), 2.5f,
                $"player height {player.transform.position.y:0.0} vs terrain {terrainHeight:0.0}");

            // Run forward for a bit; the camera-relative wish direction is forward.
            player.UseExternalInput = true;
            player.ExternalInput = new Vector2(0f, 1f);
            yield return new WaitForSeconds(1.2f);
            player.UseExternalInput = false;

            Vector3 end = player.transform.position;
            float travelled = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(end.x, end.z));
            Assert.Greater(travelled, 4f, $"player only moved {travelled:0.0} m in 1.2 s");
        }

        [TearDown]
        public void TearDown()
        {
            // Destroy only what this test created, then leave an empty scene so the
            // next PlayMode test cannot inherit this world.
            if (_host != null)
            {
                WorldBootstrap bootstrap = _host.GetComponent<WorldBootstrap>();
                if (bootstrap != null)
                {
                    if (bootstrap.Player != null)
                    {
                        Object.Destroy(bootstrap.Player.gameObject);
                    }
                    if (bootstrap.PlayerCamera != null)
                    {
                        Object.Destroy(bootstrap.PlayerCamera.gameObject);
                    }
                }
                Object.Destroy(_host);
            }

            PlayModeSceneIsolation.LeaveEmpty("WorldPlayModeTests");
        }
    }
}
