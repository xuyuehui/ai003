using System.IO;
using System.Linq;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Authors the player into the OPEN scene as a prefab instance placed at the
    /// spawn point. Play then reuses it instead of spawning a character.
    /// </summary>
    public static class AuthorPlayer
    {
        private const string PrefabDir = "Assets/Prefabs";
        private const string PrefabPath = PrefabDir + "/AshkarPlayer.prefab";
        private const string PlayerName = "[AI003] Player";

        /// <summary>Batch entry: opens the scene, authors the player, saves, exits.</summary>
        public static void RunBatch()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/OpenWorld.unity");
            Run();
            EditorSceneManager.SaveOpenScenes();

            // The PlayMode test loads the scene by name, so it has to be in the list.
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(
                EditorBuildSettings.scenes);
            if (!scenes.Any(s => s.path == "Assets/Scenes/OpenWorld.unity"))
            {
                scenes.Insert(0, new EditorBuildSettingsScene("Assets/Scenes/OpenWorld.unity", true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }

            Debug.Log("[AI003][AUTHOR] player batch done");
            EditorApplication.Exit(0);
        }

        [MenuItem("AI003/Author Player")]
        public static void Run()
        {
            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            Vector3 position = data.Spawn + Vector3.up * 1.2f;

            // The instance is named after the prefab asset, so look it up by
            // component instead of by name.
            var existing = Object.FindAnyObjectByType<AI003.Player.ThirdPersonController>(
                FindObjectsInactive.Include);
            if (existing != null)
            {
                Object.DestroyImmediate(existing.gameObject);
            }

            Directory.CreateDirectory(PrefabDir);
            AssetDatabase.Refresh();

            // Build one through the runtime factory so Play and this tool agree, then
            // store it as a prefab and instance it at the spawn point.
            WorldBootstrap.CreatePlayerObject(data, position, out var template);
            template.name = PlayerName;

            var prefab = PrefabUtility.SaveAsPrefabAsset(template, PrefabPath);
            Object.DestroyImmediate(template);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = position;

            Selection.activeGameObject = instance;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[AI003][AUTHOR] player instanced at {position} from {PrefabPath} — save the scene");
        }
    }
}
