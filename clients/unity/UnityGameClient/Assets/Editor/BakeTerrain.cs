using System.IO;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Bakes the terrain into the OPEN scene as real meshes (no runtime generation).
    /// Each chunk mesh becomes an asset under Assets/Generated/Terrain and the
    /// bootstrap is switched to "baked" mode, so Play only spawns the player.
    ///
    /// Re-running after the world data changes updates the existing objects and
    /// mesh assets in place; it never destroys and recreates the terrain root,
    /// because that renumbers every object and makes the scene diff unreviewable.
    /// </summary>
    public static class BakeTerrain
    {
        private const string RootName = "[AI003] Terrain";
        private const string MeshDir = "Assets/Generated/Terrain";
        private const string MaterialPath = "Assets/Settings/AshkarTerrain.mat";
        private const string DetailNormalPath = "Assets/Settings/Textures/AshkarDetailNormal.png";
        private const string OceanMaterialPath = "Assets/Settings/AshkarOcean.mat";

        [MenuItem("AI003/Bake Terrain (static)")]
        public static void Run()
        {
            Directory.CreateDirectory(MeshDir);

            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            var theme = WorldTheme.CreateAshkar();
            var colors = theme.BuildColorGrid(data);

            var existing = GameObject.Find(RootName);
            bool created = existing == null;
            var root = created ? new GameObject(RootName) : existing;
            var terrain = root.GetComponent<WorldTerrain>();
            if (terrain == null)
            {
                terrain = root.AddComponent<WorldTerrain>();
            }

            if (created)
            {
                // First bake: the runtime path is the reference implementation.
                terrain.Build(data, colors, "AI003/AshkarTerrain");
            }
            else
            {
                // Re-bake: rebuild the geometry into the objects already in the scene.
                RebuildChunks(root, data, colors);
                MoveWater(root, data);
            }

            PersistChunkMeshes(root);
            ApplyMaterialAssets(terrain, root);
            MarkStatic(root);

            var boot = Object.FindAnyObjectByType<WorldBootstrap>();
            if (boot != null)
            {
                SetBool(boot, "_buildTerrain", false);
                SetBool(boot, "_autoBuild", true);
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            int chunks = root.GetComponentsInChildren<MeshFilter>(true).Length;
            Debug.Log($"[AI003][BAKE] terrain baked: {chunks} meshes under {MeshDir} — save the scene");
        }

        /// <summary>
        /// Rebuilds every chunk mesh into the GameObject that already carries it.
        /// The chunk set and its transforms come from WorldTerrain so the baked
        /// layout and the runtime layout cannot drift.
        /// </summary>
        private static void RebuildChunks(GameObject root, WorldData data, Color32[] colors)
        {
            int cellsPerChunk = (data.Resolution - 1) / WorldTerrain.ChunksPerAxis;
            float half = data.SizeMeters * 0.5f;
            int reused = 0;
            int created = 0;

            for (int cz = 0; cz < WorldTerrain.ChunksPerAxis; cz++)
            {
                for (int cx = 0; cx < WorldTerrain.ChunksPerAxis; cx++)
                {
                    string chunkName = $"Chunk_{cx}_{cz}";
                    var child = FindChunk(root.transform, chunkName);
                    var chunk = child != null ? child.gameObject : new GameObject(chunkName);
                    if (child == null)
                    {
                        chunk.transform.SetParent(root.transform, false);
                        created++;
                        Debug.LogWarning($"[AI003][BAKE] chunk {chunkName} was missing and has been created");
                    }
                    else
                    {
                        reused++;
                    }

                    chunk.transform.localPosition = new Vector3(
                        -half + cx * cellsPerChunk * data.CellSize,
                        0f,
                        -half + cz * cellsPerChunk * data.CellSize);

                    var filter = chunk.GetComponent<MeshFilter>();
                    if (filter == null)
                    {
                        filter = chunk.AddComponent<MeshFilter>();
                    }

                    var renderer = chunk.GetComponent<MeshRenderer>();
                    if (renderer == null)
                    {
                        renderer = chunk.AddComponent<MeshRenderer>();
                    }
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = true;

                    var collider = chunk.GetComponent<MeshCollider>();
                    if (collider == null)
                    {
                        collider = chunk.AddComponent<MeshCollider>();
                    }

                    Mesh mesh = WorldTerrain.BuildChunkMesh(data, cx, cz, cellsPerChunk, colors);
                    filter.sharedMesh = mesh;
                    collider.sharedMesh = mesh;
                }
            }

            Debug.Log($"[AI003][BAKE] chunks reused={reused} created={created} " +
                      "(re-using keeps the fileIDs stable across bakes)");
        }

        /// <summary>Name lookup that also sees nested or reparented chunks.</summary>
        private static Transform FindChunk(Transform root, string name)
        {
            var direct = root.Find(name);
            if (direct != null)
            {
                return direct;
            }
            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate != root && candidate.name == name)
                {
                    return candidate;
                }
            }
            return null;
        }

        private static void MoveWater(GameObject root, WorldData data)
        {
            var water = root.transform.Find("Ocean");
            if (water != null)
            {
                water.localPosition = new Vector3(0f, data.SeaLevel, 0f);
            }
        }

        /// <summary>
        /// Points the chunks and the sea at versioned material assets instead of the
        /// runtime materials WorldTerrain.Build created (those would be embedded in the
        /// scene and could not be tweaked or reviewed). The tuning itself comes from
        /// AshkarLook.ConfigureTerrainMaterial so runtime and bake cannot drift.
        /// </summary>
        private static void ApplyMaterialAssets(WorldTerrain terrain, GameObject root)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            bool created = material == null;
            if (created)
            {
                Material source = terrain.TerrainMaterial;
                material = source != null
                    ? new Material(source)
                    : new Material(Shader.Find("AI003/AshkarTerrain"));
                material.name = "AshkarTerrain";
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            AshkarLook.ConfigureTerrainMaterial(
                material, AssetDatabase.LoadAssetAtPath<Texture2D>(DetailNormalPath));
            EditorUtility.SetDirty(material);

            int chunks = 0;
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.name.StartsWith("Chunk_"))
                {
                    continue;
                }
                renderer.sharedMaterial = material;
                chunks++;
            }

            int seas = 0;
            var water = root.transform.Find("Ocean");
            if (water != null)
            {
                var ocean = AssetDatabase.LoadAssetAtPath<Material>(OceanMaterialPath);
                if (ocean == null)
                {
                    ocean = AshkarLook.CreateSaltMaterial();
                    AssetDatabase.CreateAsset(ocean, OceanMaterialPath);
                }
                water.GetComponent<MeshRenderer>().sharedMaterial = ocean;
                EditorUtility.SetDirty(ocean);
                seas = 1;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[AI003][BAKE] materials: {chunks} chunks -> {MaterialPath}" +
                      $"{(created ? " (created)" : "")}, sea -> {OceanMaterialPath} (x{seas})");
        }

        /// <summary>
        /// One Mesh asset per chunk. The asset keeps its GUID across re-bakes, so
        /// references written into the scene stay valid. Both the renderer and the
        /// collider must point at that asset: a collider left on the fresh runtime
        /// mesh serialises ~11k lines of mesh data into OpenWorld.unity.
        /// </summary>
        private static void PersistChunkMeshes(GameObject root)
        {
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.gameObject.name.StartsWith("Chunk_"))
                {
                    continue;
                }

                Mesh built = filter.sharedMesh;
                if (built == null)
                {
                    continue;
                }

                string path = $"{MeshDir}/{Sanitize(filter.gameObject.name)}.asset";
                Mesh asset = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (asset == null)
                {
                    AssetDatabase.CreateAsset(built, path);
                    asset = built;
                }
                else if (asset != built)
                {
                    CopyMeshData(built, asset);
                }

                filter.sharedMesh = asset;
                var collider = filter.GetComponent<MeshCollider>();
                if (collider != null)
                {
                    collider.sharedMesh = asset;
                }

                if (asset != built)
                {
                    Object.DestroyImmediate(built);
                }
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>Overwrites an existing mesh asset without changing its GUID.</summary>
        private static void CopyMeshData(Mesh source, Mesh target)
        {
            target.Clear();
            // 66,049 vertices per chunk: UInt16 indices wrap at 65,535 and stretch the
            // chunk into long black triangles.
            target.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            target.vertices = source.vertices;
            target.normals = source.normals;
            target.colors32 = source.colors32;
            target.uv = source.uv;
            target.triangles = source.triangles;
            target.RecalculateBounds();
            EditorUtility.SetDirty(target);
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
