using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// One command that reproduces the authored OpenWorld scene: open the scene,
    /// re-run the three authoring menus in their dependency order, save.
    ///
    /// This is the entry a fresh clone or a batch job should use — re-running the
    /// individual menus by hand in a different order can author the environment
    /// after the camera that depends on it.
    /// </summary>
    public static class AuthorOpenWorld
    {
        private const string ScenePath = "Assets/Scenes/OpenWorld.unity";

        [MenuItem("AI003/Author Open World (all)")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            AuthorAshkarEnvironment.Run();
            AuthorMainCamera.Run();
            BakeTerrain.Run();
            AuthorVegetation.Run();

            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[AI003][AUTHOR] OpenWorld re-authored and saved");
        }
    }
}
