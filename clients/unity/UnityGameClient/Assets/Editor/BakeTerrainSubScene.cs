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
        private const int ChunkStartX = 8;
        private const int ChunkStartZ = 6;
        private const int ChunkCount = 2;

        private const string Root = "Assets/World/Baked";
        private const string ContentScene = Root + "/TerrainContent.unity";
        private const string WorldScene = Root + "/BakedWorld.unity";
        private const string MaterialPath = Root + "/TerrainBaked.mat";
        private const string MeshDir = Root + "/Meshes";

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

        public static string Bake()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "World/Baked/Meshes"));
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "World/Baked"));

            WorldData data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));

            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                Shader shader = Shader.Find("AI003/TerrainVertexColor");
                if (shader == null) throw new System.InvalidOperationException("terrain shader missing");
                material = new Material(shader) { name = "TerrainBaked", enableInstancing = true };
                material.SetFloat("_Smoothness", 0.15f);
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
                    string meshPath = $"{MeshDir}/chunk_{cx}_{cz}.asset";
                    AssetDatabase.DeleteAsset(meshPath);
                    AssetDatabase.CreateAsset(mesh, meshPath);

                    var chunk = new GameObject($"Chunk_{cx}_{cz}");
                    chunk.transform.SetParent(root.transform, false);
                    chunk.transform.position = new Vector3(
                        -half + cx * chunkSize, 0f, -half + cz * chunkSize);
                    chunk.AddComponent<MeshFilter>().sharedMesh =
                        AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    var renderer = chunk.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }

            EditorSceneManager.SaveScene(content, ContentScene);

            // World scene: a SubScene pointing at the content scene.
            Scene world = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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

            // Force the subscene importer to bake (content scene is closed now).
            AssetDatabase.ImportAsset(ContentScene, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            return WorldScene;
        }
    }
}
