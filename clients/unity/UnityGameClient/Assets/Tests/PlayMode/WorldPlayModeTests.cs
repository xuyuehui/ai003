using System.Collections;
using AI003.Player;
using AI003.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    /// <summary>
    /// Verifies the milestone acceptance criteria: the world builds, the player
    /// falls onto the terrain (physics + collision) and can run forward.
    /// </summary>
    public class WorldPlayModeTests
    {
        [UnityTest]
        public IEnumerator WorldBuildsAndPlayerLandsAndRuns()
        {
            float deadline = Time.realtimeSinceStartup + 20f;

            while (Time.realtimeSinceStartup < deadline && WorldBootstrap.Instance == null)
            {
                yield return null;
            }
            Assert.IsNotNull(WorldBootstrap.Instance, "WorldBootstrap was not auto-installed");

            while (Time.realtimeSinceStartup < deadline && !WorldBootstrap.Instance.IsReady)
            {
                yield return null;
            }
            Assert.IsTrue(WorldBootstrap.Instance.IsReady, "world did not finish building within 20s");

            WorldBootstrap bootstrap = WorldBootstrap.Instance;
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
    }
}
