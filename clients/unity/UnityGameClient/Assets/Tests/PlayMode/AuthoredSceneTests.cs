using System.Collections;
using System.Linq;
using AI003.Player;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    /// <summary>
    /// The authored scene must play as-is: one player, one camera, no runtime
    /// world generation. Guards the "everything lives in the scene" contract.
    /// </summary>
    public sealed class AuthoredSceneTests
    {
        [UnityTest]
        public IEnumerator AuthoredSceneSpawnsNoDuplicates()
        {
            SceneManager.LoadScene("OpenWorld");
            yield return null;
            yield return null;
            yield return null;

            var players = Object.FindObjectsByType<ThirdPersonController>(FindObjectsSortMode.None);
            var cameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
                .Where(c => c.gameObject.activeInHierarchy).ToArray();
            var bootstraps = Object.FindObjectsByType<AI003.World.WorldBootstrap>(FindObjectsSortMode.None);

            Assert.AreEqual(1, bootstraps.Length, "one world bootstrap expected");
            Assert.AreEqual(1, players.Length, "the authored player must be reused, not duplicated");
            Assert.AreEqual(1, cameras.Length, "the authored camera must be reused, not duplicated");
            Assert.IsNotNull(players[0].World, "bootstrap must hand the world data to the player");
        }
    }
}
