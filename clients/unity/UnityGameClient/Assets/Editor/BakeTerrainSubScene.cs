using System.IO;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Bakes a small terrain patch into a SubScene so Entities Graphics renders it
    /// from baked entities (the supported path), instead of runtime-created ones.
    ///
    /// Batch: Unity.exe -batchmode -executeMethod AI003.EditorTools.BakeTerrainSubScene.Run -quit
    /// </summary>
    public static class BakeTerrainSubScene
    {
        private const int ChunkStartX = 0;
        private const int ChunkStartZ = 0;
        private const int ChunkCount = 16;

        private const string Root = "Assets/World/Baked";
        private const string ContentScene = Root + "/TerrainContent.unity";
        private const string WorldScene = Root + "/BakedWorld.unity";
        private const string MaterialPath = Root + "/TerrainBakedLit.mat";
        private const string ColorTexturePath = Root + "/TerrainColor.asset";
        private const string MeshDir = Root + "/Meshes";

        [UnityEditor.MenuItem("AI003/Bake Terrain SubScene")]
        public static void Run()
        {
            try
            {
                string path = Bake();
                Debug.Log($"[AI003][BAKE] PASS {path}");
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[AI003][BAKE] FAIL: {e}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Batch-mode entry point. Unity's batch editor does not process the SubScene
        /// open/close synchronously, so the bake is driven from EditorApplication.update
        /// after the assets have been regenerated. Run without -quit.
        /// </summary>
        public static void RunBatch()
        {
            try
            {
                string worldScene = Bake();
                Debug.Log($"[AI003][BAKE-BATCH] assets rebuilt {worldScene}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[AI003][BAKE-BATCH] asset rebuild failed: {e}");
                EditorApplication.Exit(1);
                return;
            }

            double started = EditorApplication.timeSinceStartup;
            int step = 0;
            void Tick()
            {
                double elapsed = EditorApplication.timeSinceStartup - started;
                try
                {
                    if (step == 0 && elapsed > 1.0)
                    {
                        var scene = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);
                        var sub = UnityEngine.Object.FindFirstObjectByType<Unity.Scenes.SubScene>(UnityEngine.FindObjectsInactive.Include);
                        var openType = System.Type.GetType("Unity.Scenes.Editor.SubSceneUtility, Unity.Scenes.Editor");
                        openType.GetMethod("EditScene",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                            .Invoke(null, new object[] { new Unity.Scenes.SubScene[] { sub } });
                        Debug.Log("[AI003][BAKE-BATCH] subscene opened");
                        step = 1;
                    }
                    else if (step == 1 && elapsed > 3.0)
                    {
                        var sub = UnityEngine.Object.FindFirstObjectByType<Unity.Scenes.SubScene>(UnityEngine.FindObjectsInactive.Include);
                        var closeType = System.Type.GetType("Unity.Scenes.Editor.SubSceneInspectorUtility, Unity.Scenes.Editor");
                        closeType.GetMethod("CloseSceneWithoutSaving",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                            .Invoke(null, new object[] { new Unity.Scenes.SubScene[] { sub } });
                        AssetDatabase.DeleteAsset("Assets/SceneDependencyCache");
                        AssetDatabase.ImportAsset(ContentScene, ImportAssetOptions.ForceUpdate);
                        AssetDatabase.Refresh();
                        Debug.Log("[AI003][BAKE-BATCH] subscene closed, reimport requested");
                        step = 2;
                    }
                    else if (step == 2 && elapsed > 10.0)
                    {
                        // Make sure the demo scene stays playable after a re-bake.
                        var worldScene = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);
                        if (UnityEngine.Object.FindFirstObjectByType<AI003.World.WorldBootstrap>() == null)
                        {
                            new GameObject("[AI003] World").AddComponent<AI003.World.WorldBootstrap>();
                        }
                        EditorSceneManager.SaveScene(worldScene);

                        bool cached = System.IO.Directory.Exists("Assets/SceneDependencyCache");
                        Debug.Log($"[AI003][BAKE-BATCH] bake cache={cached} -> {(cached ? "PASS" : "FAIL")}");
                        EditorApplication.update -= Tick;
                        EditorApplication.Exit(cached ? 0 : 1);
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[AI003][BAKE-BATCH] failed: {e}");
                    EditorApplication.update -= Tick;
                    EditorApplication.Exit(1);
                }
            }

            EditorApplication.update += Tick;
        }

        public static string Bake()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "World/Baked/Meshes"));
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "World/Baked"));

            WorldData data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));

            // URP Lit is properly DOTS-instanced, so it renders correctly at any entity
            // count (the custom vertex-colour shader broke past a handful of chunks).
            // The per-vertex world colours become a single world-space colour texture;
            // chunk UVs are already normalised world coordinates.
            Texture2D colorTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(ColorTexturePath);
            if (colorTexture == null || colorTexture.width != data.Resolution)
            {
                AssetDatabase.DeleteAsset(ColorTexturePath);
                colorTexture = new Texture2D(data.Resolution, data.Resolution, TextureFormat.RGBA32, false, true)
                {
                    name = "TerrainColor"
                };
                colorTexture.SetPixels32(data.Colors);
                colorTexture.Apply(false, false);
                AssetDatabase.CreateAsset(colorTexture, ColorTexturePath);
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) throw new System.InvalidOperationException("URP Lit shader missing");
                material = new Material(shader) { name = "TerrainBakedLit", enableInstancing = true };
                material.SetColor("_BaseColor", Color.white);
                material.SetTexture("_BaseMap", colorTexture);
                material.SetFloat("_Smoothness", 0.15f);
                material.SetFloat("_Metallic", 0f);
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            // Content scene: one MeshRenderer per chunk, meshes saved as assets.
            Scene content = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("BakedTerrain");
            float half = data.SizeMeters * 0.5f;
            float chunkSize = WorldTerrain.CellsPerChunk * data.CellSize;

            for (int z = 0; z < ChunkCount; z++)
            {
                for (int x = 0; x < ChunkCount; x++)
                {
                    int cx = ChunkStartX + x;
                    int cz = ChunkStartZ + z;
                    Mesh mesh = WorldTerrain.BuildChunkMesh(data, cx, cz);
                    mesh.name = $"chunk_{cx}_{cz}";
                    string meshPath = $"{MeshDir}/chunk_{cx}_{cz}.asset";
                    AssetDatabase.DeleteAsset(meshPath);
                    AssetDatabase.CreateAsset(mesh, meshPath);

                    var chunk = new GameObject($"Chunk_{cx}_{cz}");
                    chunk.transform.SetParent(root.transform, false);
                    chunk.transform.position = new Vector3(
                        -half + cx * chunkSize, 0f, -half + cz * chunkSize);
                    // Use the object returned by CreateAsset directly: LoadAssetAtPath
                    // can return a stale/other mesh until the asset is imported, which
                    // made every chunk reference the same mesh.
                    chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = chunk.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(content, ContentScene);

            // World scene: a SubScene pointing at the content scene.
            Scene world = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Keep the scene directly playable: camera/player/GO fallback terrain.
            var demoRoot = new GameObject("[AI003] World");
            demoRoot.AddComponent<AI003.World.WorldBootstrap>();
            var subSceneObject = new GameObject("TerrainSubScene");
            var subScene = subSceneObject.AddComponent<Unity.Scenes.SubScene>();
            EditorSceneManager.SaveScene(world, WorldScene);
            subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ContentScene);
            // OnValidate only writes the GUID when the owning scene already has a path,
            // so set the serialized field explicitly as well.
            var serialized = new SerializedObject(subScene);
            serialized.Update();
            var guidProperty = serialized.FindProperty("_SceneGUID");
            Debug.Log($"[AI003][BAKE] guidProperty={guidProperty != null} contentGuid={AssetDatabase.AssetPathToGUID(ContentScene)}");
            if (guidProperty != null)
            {
                guidProperty.hash128Value = Hash128.Parse(AssetDatabase.AssetPathToGUID(ContentScene));
                serialized.ApplyModifiedProperties();
                Debug.Log($"[AI003][BAKE] guidAfter={guidProperty.hash128Value}");
            }
            EditorSceneManager.SaveScene(world, WorldScene);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Select SubSceneImporter for the content scene (internal API).
            var importerType = System.Type.GetType("Unity.Scenes.Editor.SubSceneImporter, Unity.Scenes.Editor");
            var overrideMethod = typeof(AssetDatabase).GetMethod(
                "SetImporterOverrideInternal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Debug.Log($"[AI003][BAKE] override importer={importerType} method={overrideMethod != null}");
            if (overrideMethod != null && importerType != null)
            {
                overrideMethod.Invoke(null, new object[] { ContentScene, importerType });
            }

            // Opening and closing the SubScene is what makes the importer produce the
            // baked entity scene; a plain reimport silently falls back to DefaultImporter.
            var openType = System.Type.GetType("Unity.Scenes.Editor.SubSceneUtility, Unity.Scenes.Editor");
            var closeType = System.Type.GetType("Unity.Scenes.Editor.SubSceneInspectorUtility, Unity.Scenes.Editor");
            var subScenes = new Unity.Scenes.SubScene[] { subScene };
            openType?.GetMethod("EditScene",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, new object[] { subScenes });
            closeType?.GetMethod("CloseSceneWithoutSaving",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, new object[] { subScenes });

            // Force the cache regenerates instead of reusing a stale artifact.
            AssetDatabase.DeleteAsset("Assets/SceneDependencyCache");
            AssetDatabase.ImportAsset(ContentScene, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            return WorldScene;
        }
    }
}
