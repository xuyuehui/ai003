using System.Collections.Generic;
using System.IO;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Authors the Ashkar set dressing directly into the OPEN scene as ordinary
    /// GameObjects (meshes saved as assets), so the composition is edited by hand
    /// in the editor instead of being spawned by code at Play.
    ///
    /// Run once, then move/rotate/delete anything you like and save the scene.
    /// </summary>
    public static class AuthorAshkarScene
    {
        private const string RootName = "[AI003] Dressing";
        private const string MeshDir = "Assets/Generated/Dressing";

        [MenuItem("AI003/Author Ashkar Scene")]
        public static void Run()
        {
            Directory.CreateDirectory(MeshDir);

            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            var spawn = data.Spawn;
            Vector3 forward = new Vector3(0.72f, 0f, 0.69f);
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);

            var existing = GameObject.Find(RootName);
            if (existing != null)
            {
                Object.DestroyImmediate(existing);
            }

            var root = new GameObject(RootName);

            var brick = AshkarProps.Material("SaltBrick", new Color(0.40f, 0.38f, 0.34f), 0.18f);
            var wood = AshkarProps.Material("WeatheredWood", new Color(0.20f, 0.16f, 0.12f), 0.12f);
            var bell = AshkarProps.Material("BellBronze", new Color(0.35f, 0.28f, 0.16f), 0.55f, 0.7f);
            var body = AshkarProps.Material("BasaltBody", new Color(0.12f, 0.10f, 0.09f), 0.25f);
            var trim = AshkarProps.Material("Brass", new Color(0.42f, 0.31f, 0.16f), 0.45f, 0.6f);
            var robe = AshkarProps.Material("Robes", new Color(0.09f, 0.07f, 0.06f), 0.1f);
            var sail = AshkarProps.Material("SailCloth", new Color(0.62f, 0.48f, 0.34f), 0.15f);

            // Far enough ahead that the player does not spawn inside the ring wall.
            Vector3 plaza = Ground(data, spawn + forward * 52f);
            AshkarProps.WellOne(plaza, 20f, brick, wood, bell).transform.SetParent(root.transform, true);

            Vector3 spire = Ground(data, spawn + forward * 95f + right * 55f);
            AshkarProps.Wellspire(spire, 80f, body, trim).transform.SetParent(root.transform, true);

            Vector3 sled = Ground(data, spawn - forward * 6f + right * 7f);
            AshkarProps.SailSled(sled, 35f, body, sail, trim).transform.SetParent(root.transform, true);

            AshkarProps.Figure(Ground(data, spawn + forward * 8f + right * 9f), 200f, 1.8f, robe)
                .transform.SetParent(root.transform, true);
            AshkarProps.Figure(Ground(data, spawn + forward * 11f + right * 12f), 210f, 1.75f, robe)
                .transform.SetParent(root.transform, true);

            PersistMeshes(root);
            MarkStatic(root);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[AI003][AUTHOR] dressing placed under '{RootName}' — move it, then save the scene");
        }

        private static void PersistMeshes(GameObject root)
        {
            var seen = new HashSet<Mesh>();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || !seen.Add(mesh))
                {
                    continue;
                }

                string path = $"{MeshDir}/{Sanitize(mesh.name)}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null)
                {
                    filter.sharedMesh = existing;
                    continue;
                }
                AssetDatabase.CreateAsset(mesh, path);
                filter.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
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

        private static Vector3 Ground(WorldData data, Vector3 at)
        {
            at.y = data.HeightAt(at.x, at.z);
            return at;
        }
    }
}
