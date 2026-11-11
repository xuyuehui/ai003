using System.Collections.Generic;
using System.IO;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>Bakes the Ashkar set into a scene (work in progress).</summary>
    public static class BakeStaticScene
    {
        private const string ScenePath = "Assets/Scenes/AshkarStatic.unity";
        private const string GeneratedDir = "Assets/Generated/Ashkar";

        [MenuItem("AI003/Bake Static Scene")]
        public static void Run()
        {
            Directory.CreateDirectory(GeneratedDir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("[AI003] World");
            var boot = root.AddComponent<WorldBootstrap>();
            SetBool(boot, "_autoBuild", false);
            SetBool(boot, "_buildTerrain", true);
            SetBool(boot, "_ashkarLook", true);

            boot.Build();

            DestroyByName("[AI003] Player");
            DestroyByName("Main Camera");

            MarkStatic(root);
            SetBool(boot, "_buildTerrain", false);
            SetBool(boot, "_autoBuild", true);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);

            Debug.Log($"[AI003][BAKE] wrote {ScenePath}");
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

        private static void DestroyByName(string name)
        {
            var go = GameObject.Find(name);
            if (go != null)
            {
                Object.DestroyImmediate(go);
            }
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

        private static void AddToBuildSettings(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var existing in scenes)
            {
                if (existing.path == path)
                {
                    return;
                }
            }
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
