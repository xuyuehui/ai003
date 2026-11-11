using System.IO;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Bakes the terrain into the OPEN scene as real meshes (no runtime generation).
    /// Each chunk mesh becomes an asset under Assets/Generated/Terrain and the
    /// bootstrap is switched to "baked" mode, so Play only spawns the player.
    ///
    /// Run once after changing the world data, then save the scene.
    /// </summary>
    public static class BakeTerrain
    {
        private const string RootName = "[AI003] Terrain";
        private const string MeshDir = "Assets/Generated/Terrain";

        [MenuItem("AI003/Bake Terrain (static)")]
        public static void Run()
        {
            Directory.CreateDirectory(MeshDir);

            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            var theme = WorldTheme.CreateAshkar();
            var colors = theme.BuildColorGrid(data);

            var existing = GameObject.Find(RootName);
            if (existing != null)
            {
                Object.DestroyImmediate(existing);
            }

            var root = new GameObject(RootName);
            var terrain = root.AddComponent<WorldTerrain>();
            terrain.Build(data, colors, "AI003/AshkarTerrain");

            PersistChunkMeshes(root);
            MarkStatic(root);

            var boot = Object.FindAnyObjectByType<WorldBootstrap>();
            if (boot != null)
            {
                SetBool(boot, "_buildTerrain", false);
                SetBool(boot, "_autoBuild", true);
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            int chunks = root.GetComponentsInChildren<MeshFilter>(true).Length;
            Debug.Log($"[AI003][BAKE] terrain baked: {chunks} meshes under {MeshDir} — save the scene");
        }

        private static void PersistChunkMeshes(GameObject root)
        {
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                string assetName = filter.gameObject.name;
                string path = $"{MeshDir}/{Sanitize(assetName)}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing == null)
                {
                    AssetDatabase.CreateAsset(mesh, path);
                    existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                }
                filter.sharedMesh = existing;
            }
            AssetDatabase.SaveAssets();
        }

        private static void MarkStatic(GameObject root)
        {
            var flags = StaticEditorFlags.ContributeGI
                        | StaticEditorFlags.OccluderStatic
                        | StaticEditorFlags.BatchingStatic
                        | StaticEditorFlags.ReflectionProbeStatic;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.SetStaticEditorFlags(child.gameObject, flags);
            }
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static void SetBool(WorldBootstrap boot, string field, bool value)
        {
            var so = new SerializedObject(boot);
            var property = so.FindProperty(field);
            if (property != null)
            {
                property.boolValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
