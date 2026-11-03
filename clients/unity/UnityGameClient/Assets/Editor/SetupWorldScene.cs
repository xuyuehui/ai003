using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Creates the playable OpenWorld scene. Run once from the command line:
    ///   Unity.exe -batchmode -nographics -projectPath ... \
    ///     -executeMethod AI003.EditorTools.SetupWorldScene.Run -quit
    /// </summary>
    public static class SetupWorldScene
    {
        private const string ScenePath = "Assets/Scenes/OpenWorld.unity";

        public static void Run()
        {
            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bootstrap = new GameObject("[AI003] World");
            bootstrap.AddComponent<AI003.World.WorldBootstrap>();

            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved)
            {
                Debug.LogError("[AI003] failed to save OpenWorld scene");
                EditorApplication.Exit(1);
                return;
            }

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[AI003] created {ScenePath} with WorldBootstrap");
            EditorApplication.Exit(0);
        }
    }
}
