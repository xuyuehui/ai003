using System.IO;
using AI003.World;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Builds 4x4 tiles (16 chunks each) as separate SubScenes and bakes each of
    /// them, so the runtime can stream whole tiles in and out with SceneSystem.
    /// </summary>
    public static class BakeTiledTerrain
    {
        private const int TilesPerAxis = 4;
        private const int ChunksPerTile = 4;

        private const string Root = "Assets/World/Baked";
        private const string TilesDir = Root + "/Tiles";
        private const string MeshDir = TilesDir + "/Meshes";
        private const string WorldScene = Root + "/StreamingWorld.unity";
        private const string MaterialPath = Root + "/TerrainBakedLit.mat";
        private const string CatalogPath = "Assets/Resources/TerrainTileCatalog.asset";

        [MenuItem("AI003/Bake Tiled Streaming Terrain")]
        public static void Run()
        {
            try
            {
                Build();
                Debug.Log("[AI003][TILE-BAKE] assets built; open/close each tile in the editor to bake");
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[AI003][TILE-BAKE] FAIL: {e}");
                EditorApplication.Exit(1);
            }
        }

        public static void RunBatch()
        {
            try
            {
                Build();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[AI003][TILE-BAKE] build failed: {e}");
                EditorApplication.Exit(1);
                return;
            }

            double phaseStart = EditorApplication.timeSinceStartup;
            int tile = 0;
            int phase = 0;
            Scene world = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);

            void Tick()
            {
                try
                {
                    double now = EditorApplication.timeSinceStartup;
                    if (phase == 0)
                    {
                        SubScene sub = FindTile(world, tile);
                        if (sub == null)
                        {
                            Debug.LogError($"[AI003][TILE-BAKE] missing SubScene for tile {tile}");
                            EditorApplication.update -= Tick;
                            EditorApplication.Exit(1);
                            return;
                        }
                        var openType = System.Type.GetType("Unity.Scenes.Editor.SubSceneUtility, Unity.Scenes.Editor");
                        openType.GetMethod("EditScene",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                            .Invoke(null, new object[] { new[] { sub } });
                        phase = 1;
                        phaseStart = now;
                        return;
                    }

                    if (phase == 1 && now - phaseStart > 1.0)
                    {
                        SubScene sub = FindTile(world, tile);
                        var closeType = System.Type.GetType("Unity.Scenes.Editor.SubSceneInspectorUtility, Unity.Scenes.Editor");
                        closeType.GetMethod("CloseSceneWithoutSaving",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                            .Invoke(null, new object[] { new[] { sub } });

                        string tileScene = TileScene(tile);
                        var importerType = System.Type.GetType("Unity.Scenes.Editor.SubSceneImporter, Unity.Scenes.Editor");
                        var overrideMethod = typeof(AssetDatabase).GetMethod(
                            "SetImporterOverrideInternal",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        overrideMethod?.Invoke(null, new object[] { tileScene, importerType });
                        AssetDatabase.ImportAsset(tileScene, ImportAssetOptions.ForceUpdate);

                        Debug.Log($"[AI003][TILE-BAKE] baked tile {tile} ({tileScene})");
                        tile++;
                        phase = 0;
                        phaseStart = now;
                        if (tile >= TilesPerAxis * TilesPerAxis)
                        {
                            EditorSceneManager.SaveScene(world);
                            EditorApplication.update -= Tick;
                            Debug.Log("[AI003][TILE-BAKE] PASS all tiles baked");
                            EditorApplication.Exit(0);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[AI003][TILE-BAKE] failed: {e}");
                    EditorApplication.update -= Tick;
                    EditorApplication.Exit(1);
                }
            }

            EditorApplication.update += Tick;
        }

        private static SubScene FindTile(Scene world, int tile)
        {
            string name = $"Tile_{tile % TilesPerAxis}_{tile / TilesPerAxis}";
            foreach (GameObject go in world.GetRootGameObjects())
            {
                if (go.name == name)
                {
                    return go.GetComponent<SubScene>();
                }
            }
            return null;
        }

        private static string TileScene(int tile)
            => $"{TilesDir}/Tile_{tile % TilesPerAxis}_{tile / TilesPerAxis}.unity";

        private static void Build()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "World/Baked/Tiles/Meshes"));
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Resources"));
            WorldData data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                throw new System.InvalidOperationException($"missing {MaterialPath}; run AI003/Bake Terrain SubScene first");
            }

            float half = data.SizeMeters * 0.5f;
            float chunkSize = WorldTerrain.CellsPerChunk * data.CellSize;

            int tileCount = TilesPerAxis * TilesPerAxis;
            var guids = new string[tileCount];
            var coords = new Vector2Int[tileCount];

            for (int ty = 0; ty < TilesPerAxis; ty++)
            {
                for (int tx = 0; tx < TilesPerAxis; tx++)
                {
                    int tile = ty * TilesPerAxis + tx;
                    Scene content = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    var root = new GameObject($"Tile_{tx}_{ty}");
                    for (int cz = 0; cz < ChunksPerTile; cz++)
                    {
                        for (int cx = 0; cx < ChunksPerTile; cx++)
                        {
                            int chunkX = tx * ChunksPerTile + cx;
                            int chunkZ = ty * ChunksPerTile + cz;
                            Mesh mesh = WorldTerrain.BuildChunkMesh(data, chunkX, chunkZ);
                            mesh.name = $"tile_{tx}_{ty}_chunk_{chunkX}_{chunkZ}";
                            string meshPath = $"{MeshDir}/tile_{tx}_{ty}_chunk_{chunkX}_{chunkZ}.asset";
                            AssetDatabase.DeleteAsset(meshPath);
                            AssetDatabase.CreateAsset(mesh, meshPath);

                            var chunk = new GameObject($"Chunk_{chunkX}_{chunkZ}");
                            chunk.transform.SetParent(root.transform, false);
                            chunk.transform.position = new Vector3(
                                -half + chunkX * chunkSize, 0f, -half + chunkZ * chunkSize);
                            chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
                            var renderer = chunk.AddComponent<MeshRenderer>();
                            renderer.sharedMaterial = material;
                            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        }
                    }
                    AssetDatabase.SaveAssets();
                    EditorSceneManager.SaveScene(content, TileScene(tile));
                    guids[tile] = AssetDatabase.AssetPathToGUID(TileScene(tile));
                    coords[tile] = new Vector2Int(tx, ty);
                }
            }

            Scene world = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("[AI003] World").AddComponent<WorldBootstrap>();
            for (int tile = 0; tile < tileCount; tile++)
            {
                int tx = tile % TilesPerAxis;
                int ty = tile / TilesPerAxis;
                var subObject = new GameObject($"Tile_{tx}_{ty}");
                var sub = subObject.AddComponent<SubScene>();
                sub.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(TileScene(tile));
                sub.AutoLoadScene = false;
                var serialized = new SerializedObject(sub);
                serialized.Update();
                var guid = serialized.FindProperty("_SceneGUID");
                guid.hash128Value = UnityEngine.Hash128.Parse(guids[tile]);
                serialized.ApplyModifiedProperties();
            }

            EditorSceneManager.SaveScene(world, WorldScene);

            var catalog = AssetDatabase.LoadAssetAtPath<TerrainTileCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<TerrainTileCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.TilesPerAxis = TilesPerAxis;
            catalog.TileSizeMeters = chunkSize * ChunksPerTile;
            catalog.Guids = guids;
            catalog.Coords = coords;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AI003][TILE-BAKE] built {tileCount} tiles -> {WorldScene}");
        }
    }
}
