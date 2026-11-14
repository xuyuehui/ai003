using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.Tests
{
    /// <summary>
    /// PlayMode tests share one play session, so a scene loaded by one test is
    /// still there for the next one. That already produced a false failure:
    /// <c>WorldBuildsAndPlayerLandsAndRuns</c> asserted on a player that belonged
    /// to the baked OpenWorld scene loaded by <c>AuthoredSceneTests</c> and read
    /// y = -629 m (7.67 s of free fall at Gravity -24). See
    /// artifacts/20261114-629-verdict.md.
    ///
    /// Any PlayMode test that builds or inspects world state calls
    /// <see cref="CreateEmptyActiveScene"/> + <see cref="UnloadOthers"/> first, so
    /// it never inherits another test's world.
    /// </summary>
    public static class PlayModeSceneIsolation
    {
        private static int _counter;

        /// <summary>Creates and activates an empty scene (the runner object lives
        /// in a DontDestroyOnLoad scene, so it survives this).</summary>
        public static Scene CreateEmptyActiveScene(string owner)
        {
            _counter++;
            Scene scene = SceneManager.CreateScene($"{owner}-isolated-{_counter}");
            SceneManager.SetActiveScene(scene);
            return scene;
        }

        /// <summary>Unloads every loaded scene except the kept one, one at a time.</summary>
        public static IEnumerator UnloadOthers(Scene keep)
        {
            for (int guard = 0; guard < 32; guard++)
            {
                Scene victim = default;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene candidate = SceneManager.GetSceneAt(i);
                    if (candidate == keep || !candidate.isLoaded)
                    {
                        continue;
                    }
                    victim = candidate;
                    break;
                }
                if (!victim.IsValid())
                {
                    yield break;
                }
                AsyncOperation unload = SceneManager.UnloadSceneAsync(victim);
                if (unload == null)
                {
                    yield break;
                }
                while (!unload.isDone)
                {
                    yield return null;
                }
            }
        }

        /// <summary>Leaves the session in an empty scene instead of whatever the
        /// test loaded, so the next test starts from nothing.</summary>
        public static void LeaveEmpty(string owner)
        {
            CreateEmptyActiveScene(owner + "-teardown");
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene == SceneManager.GetActiveScene())
                {
                    continue;
                }
                SceneManager.UnloadSceneAsync(scene);
            }
        }
    }
}
