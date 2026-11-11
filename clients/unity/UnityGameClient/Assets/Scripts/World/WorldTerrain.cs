using UnityEngine;
using UnityEngine.Rendering;

namespace AI003.World
{
    /// <summary>
    /// Builds the terrain as a grid of mesh chunks with MeshColliders.
    ///
    /// Chunking keeps each mesh small (65x65 vertices) and gives the physics
    /// engine a reasonable number of colliders. Vertex data comes straight from
    /// the shared world files so the Unreal client matches this one.
    /// </summary>
    public sealed class WorldTerrain : MonoBehaviour
    {
        public const int ChunksPerAxis = 8;

        public WorldData Data { get; private set; }
        public int ChunkCount => ChunksPerAxis * ChunksPerAxis;
        public Material TerrainMaterial { get; private set; }
        public Material WaterMaterial { get; private set; }
        public GameObject Water { get; private set; }

        public void Build(WorldData data)
        {
            Build(data, null, null);
        }

        /// <summary>
        /// Build with an optional colour override (a WorldTheme) and terrain shader.
        /// The shared world files are never rewritten: this only changes this client's look.
        /// </summary>
        public void Build(WorldData data, Color32[] colorOverride, string terrainShaderName)
        {
            Data = data;
            TerrainMaterial = CreateTerrainMaterial(terrainShaderName);
            WaterMaterial = CreateWaterMaterial();

            int cellsPerChunk = (data.Resolution - 1) / ChunksPerAxis;
            if (cellsPerChunk * ChunksPerAxis != data.Resolution - 1)
            {
                throw new System.InvalidOperationException(
                    $"resolution {data.Resolution} is not divisible into {ChunksPerAxis} chunks");
            }

            float half = data.SizeMeters * 0.5f;
            for (int cz = 0; cz < ChunksPerAxis; cz++)
            {
                for (int cx = 0; cx < ChunksPerAxis; cx++)
                {
                    var chunkObject = new GameObject($"Chunk_{cx}_{cz}");
                    chunkObject.transform.SetParent(transform, false);
                    chunkObject.transform.localPosition = new Vector3(
                        -half + cx * cellsPerChunk * data.CellSize,
                        0f,
                        -half + cz * cellsPerChunk * data.CellSize);

                    Mesh mesh = BuildChunkMesh(data, cx, cz, cellsPerChunk, colorOverride);

                    chunkObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = chunkObject.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = TerrainMaterial;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = true;

                    var collider = chunkObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = mesh;
                }
            }

            BuildWater(data);
        }

        public static Mesh BuildChunkMesh(WorldData data, int chunkX, int chunkZ, int cellsPerChunk)
        {
            return BuildChunkMesh(data, chunkX, chunkZ, cellsPerChunk, null);
        }

        public static Mesh BuildChunkMesh(WorldData data, int chunkX, int chunkZ, int cellsPerChunk,
            Color32[] colorOverride)
        {
            int res = data.Resolution;
            int vertsPerAxis = cellsPerChunk + 1;
            int vertCount = vertsPerAxis * vertsPerAxis;

            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var colors = new Color32[vertCount];
            var uvs = new Vector2[vertCount];
            var triangles = new int[cellsPerChunk * cellsPerChunk * 6];

            int baseX = chunkX * cellsPerChunk;
            int baseZ = chunkZ * cellsPerChunk;
            float cell = data.CellSize;

            for (int lz = 0; lz < vertsPerAxis; lz++)
            {
                int gz = baseZ + lz;
                for (int lx = 0; lx < vertsPerAxis; lx++)
                {
                    int gx = baseX + lx;
                    int vi = lz * vertsPerAxis + lx;
                    int gi = gz * res + gx;

                    vertices[vi] = new Vector3(lx * cell, data.Heights[gi], lz * cell);
                    normals[vi] = data.NormalAt(gx, gz);
                    colors[vi] = colorOverride != null ? colorOverride[gi] : data.Colors[gi];
                    uvs[vi] = new Vector2((float)gx / (res - 1), (float)gz / (res - 1));
                }
            }

            int t = 0;
            for (int lz = 0; lz < cellsPerChunk; lz++)
            {
                for (int lx = 0; lx < cellsPerChunk; lx++)
                {
                    int i0 = lz * vertsPerAxis + lx;
                    int i1 = i0 + 1;
                    int i2 = i0 + vertsPerAxis;
                    int i3 = i2 + 1;

                    triangles[t++] = i0;
                    triangles[t++] = i2;
                    triangles[t++] = i1;
                    triangles[t++] = i1;
                    triangles[t++] = i2;
                    triangles[t++] = i3;
                }
            }

            var mesh = new Mesh { name = $"Terrain_{chunkX}_{chunkZ}" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors32 = colors;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private void BuildWater(WorldData data)
        {
            Water = new GameObject("Ocean");
            Water.transform.SetParent(transform, false);
            Water.transform.localPosition = new Vector3(0f, data.SeaLevel, 0f);

            // Extend well past the map so the horizon is water, not a plane edge.
            float extent = data.SizeMeters * 4f;
            var mesh = new Mesh { name = "OceanPlane" };
            mesh.vertices = new[]
            {
                new Vector3(-extent, 0f, -extent),
                new Vector3(-extent, 0f, extent),
                new Vector3(extent, 0f, extent),
                new Vector3(extent, 0f, -extent),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.RecalculateBounds();

            Water.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = Water.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = WaterMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static Material CreateTerrainMaterial(string shaderName)
        {
            string wanted = string.IsNullOrEmpty(shaderName) ? "AI003/TerrainVertexColor" : shaderName;
            Shader shader = Shader.Find(wanted);
            if (shader == null)
            {
                throw new MissingReferenceException($"shader {wanted} was not found");
            }
            var material = new Material(shader) { name = shader.name };
            material.SetFloat("_Smoothness", 0.15f);
            return material;
        }

        private static Material CreateWaterMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = new Material(shader) { name = "Ocean" };
            material.SetColor("_BaseColor", new Color(0.035f, 0.20f, 0.27f, 0.78f));
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.93f);
            material.SetFloat("_Surface", 1f); // transparent
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
            return material;
        }
    }
}
