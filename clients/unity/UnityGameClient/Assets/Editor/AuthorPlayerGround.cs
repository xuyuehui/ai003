using System.IO;
using AI003.Player;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Keeps the authored player on the terrain surface. Only the vertical position is
    /// touched: x/z and rotation stay whatever the scene has, so a hand-placed spot
    /// survives a re-run.
    ///
    /// Why this exists: the OpenWorld player instance was authored from world.json's spawn
    /// when that data still resolved to a lower height, so the baked scene held the player
    /// ~5 m under the ground at (38, -88). In Play the character fell straight through the
    /// terrain (see artifacts/20261114-629-verdict.md). Lifting it once is not enough —
    /// a fresh clone re-running the authoring menus must land on the same spot, so Run() is
    /// idempotent and part of AuthorOpenWorld.
    ///
    /// Height comes from the baked terrain collider when there is one (that is what Play
    /// collides with) and falls back to WorldData.HeightAt.
    /// </summary>
    public static class AuthorPlayerGround
    {
        /// <summary>Matches the clearance AuthorPlayer uses above WorldData.Spawn.</summary>
        public const float Clearance = 1.2f;

        private const string ScenePath = "Assets/Scenes/OpenWorld.unity";
        private const string TerrainRootName = "[AI003] Terrain";

        /// <summary>Corrects only the height, leaving x/z wherever the scene put the player.</summary>
        [MenuItem("AI003/Author Player Ground")]
        public static void Run()
        {
            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            var player = FindPlayer();
            if (player == null)
            {
                return;
            }

            Vector3 position = player.transform.position;
            Place(data, player, position.x, position.z, "ground");
        }

        /// <summary>
        /// Puts the player back on world.json's spawn footprint (x/z from the data, y from the
        /// baked terrain +1.2 m). The vertical value is never typed by hand — it comes from the
        /// same sampler Run() uses. Use it when the scene drifts away from the spawn point.
        /// </summary>
        [MenuItem("AI003/Author Player To Spawn")]
        public static void SnapToSpawn()
        {
            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            var player = FindPlayer();
            if (player == null)
            {
                return;
            }

            Vector3 spawn = data.Spawn;
            Place(data, player, spawn.x, spawn.z, "spawn");
        }

        /// <summary>Batch entry: opens the scene, grounds the player, saves, exits.</summary>
        public static void RunBatch()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Run();
            Save();
            Debug.Log("[AI003][AUTHOR] player-ground batch done");
            EditorApplication.Exit(0);
        }

        /// <summary>Batch entry: opens the scene, moves the player to the spawn footprint, saves, exits.</summary>
        public static void SnapToSpawnBatch()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SnapToSpawn();
            Save();
            Debug.Log("[AI003][AUTHOR] player-to-spawn batch done");
            EditorApplication.Exit(0);
        }

        private static ThirdPersonController FindPlayer()
        {
            var player = Object.FindAnyObjectByType<ThirdPersonController>(FindObjectsInactive.Include);
            if (player == null)
            {
                Debug.LogWarning("[AI003][AUTHOR] no authored player in the open scene — nothing to place");
            }
            return player;
        }

        private static void Place(WorldData data, ThirdPersonController player, float x, float z, string reason)
        {
            float terrainHeight = SampleTerrain(data, x, z);
            var target = new Vector3(x, terrainHeight + Clearance, z);
            Vector3 before = player.transform.position;

            bool moved = (target - before).sqrMagnitude >= 0.000001f;
            if (moved)
            {
                player.transform.position = target;
                if (PrefabUtility.IsPartOfPrefabInstance(player.gameObject))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(player.transform);
                }
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            }

            Debug.Log($"[AI003][AUTHOR] player placed ({reason}) at ({target.x:0.##}, {target.y:0.###}, {target.z:0.##}) " +
                      $"— was ({before.x:0.##}, {before.y:0.###}, {before.z:0.##}), terrain y={terrainHeight:0.###} " +
                      $"+ {Clearance} m, changed={moved}");
        }

        private static void Save()
        {
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Top-most baked terrain hit under the point. Play collides with that mesh, so it wins
        /// over the height field whenever the two disagree; HeightAt is the fallback.
        /// </summary>
        private static float SampleTerrain(WorldData data, float x, float z)
        {
            Physics.SyncTransforms();

            RaycastHit[] hits = Physics.RaycastAll(new Vector3(x, 3000f, z),
                Vector3.down, 6000f, ~0, QueryTriggerInteraction.Ignore);

            float best = float.NegativeInfinity;
            foreach (RaycastHit hit in hits)
            {
                Transform parent = hit.collider.transform.parent;
                if (parent != null && parent.name == TerrainRootName)
                {
                    best = Mathf.Max(best, hit.point.y);
                }
            }

            float height = best > float.NegativeInfinity ? best : data.HeightAt(x, z);
            float field = data.HeightAt(x, z);
            if (!Mathf.Approximately(height, field))
            {
                Debug.LogWarning($"[AI003][AUTHOR] baked terrain y={height:0.###} differs from height field " +
                                 $"y={field:0.###} at ({x:0.##}, {z:0.##}); using the collider");
            }
            return height;
        }
    }
}
