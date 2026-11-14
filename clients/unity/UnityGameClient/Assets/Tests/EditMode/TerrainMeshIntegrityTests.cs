using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using AI003.World;

namespace AI003.Tests.EditMode
{
    /// <summary>
    /// Integrity of the baked terrain meshes. The "black wedge" artefacts in the six
    /// stills survived disabling shadows and vegetation, so they live in the chunk
    /// geometry itself (degenerate triangles, broken height mapping or a seam where
    /// two chunks disagree). These tests look at it numerically instead of by eye.
    /// </summary>
    public class TerrainMeshIntegrityTests
    {
        private const string TerrainDir = "Assets/Generated/Terrain";
        private const int ChunksPerAxis = WorldTerrain.ChunksPerAxis;

        private static WorldData _world;

        private static WorldData World
        {
            get
            {
                if (_world == null)
                {
                    _world = WorldData.Load(System.IO.Path.Combine(Application.streamingAssetsPath, "world"));
                }
                return _world;
            }
        }

        private static int CellsPerChunk => (World.Resolution - 1) / ChunksPerAxis;

        private static List<Mesh> LoadBakedChunks()
        {
            var meshes = new List<Mesh>();
            for (int cz = 0; cz < ChunksPerAxis; cz++)
            {
                for (int cx = 0; cx < ChunksPerAxis; cx++)
                {
                    string path = $"{TerrainDir}/Chunk_{cx}_{cz}.asset";
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (mesh != null)
                    {
                        meshes.Add(mesh);
                    }
                }
            }
            return meshes;
        }

        [Test]
        public void BakedChunksCoverTheWholeGrid()
        {
            for (int cz = 0; cz < ChunksPerAxis; cz++)
            {
                for (int cx = 0; cx < ChunksPerAxis; cx++)
                {
                    string path = $"{TerrainDir}/Chunk_{cx}_{cz}.asset";
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    Assert.IsNotNull(mesh, $"missing baked chunk {path}; re-run the author menu");
                }
            }
        }

        [Test]
        public void BakedChunksHaveTheExpectedTopology()
        {
            int cells = CellsPerChunk;
            int expectedVerts = (cells + 1) * (cells + 1);
            int expectedIndices = cells * cells * 6;

            foreach (Mesh mesh in LoadBakedChunks())
            {
                Assert.AreEqual(expectedVerts, mesh.vertexCount, $"{mesh.name} vertex count");
                Assert.AreEqual(expectedIndices, mesh.triangles.Length, $"{mesh.name} index count");
                Assert.AreEqual(UnityEngine.Rendering.IndexFormat.UInt32, mesh.indexFormat,
                    $"{mesh.name}: a UInt16 chunk wraps at 65,535 indices and grows long stretched " +
                    "triangles across the chunk (the black wedges); re-bake with IndexFormat.UInt32");

                var indices = mesh.triangles;
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] < 0 || indices[i] >= mesh.vertexCount)
                    {
                        Assert.Fail($"{mesh.name}: index[{i}] = {indices[i]} is outside 0..{mesh.vertexCount - 1}");
                    }
                }
            }
        }

        [Test]
        public void BakedChunksHaveNoDegenerateOrNonFiniteTriangles()
        {
            int cells = CellsPerChunk;
            var failures = new List<string>();

            foreach (Mesh mesh in LoadBakedChunks())
            {
                Vector3[] verts = mesh.vertices;
                var indices = mesh.triangles;

                for (int v = 0; v < verts.Length; v++)
                {
                    Vector3 p = verts[v];
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                        float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                    {
                        failures.Add($"{mesh.name}: vertex[{v}] = {p} is not finite");
                        break;
                    }
                }

                int degenerate = 0;
                int firstDegenerate = -1;
                for (int t = 0; t < indices.Length; t += 3)
                {
                    int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                    if (a == b || b == c || a == c)
                    {
                        degenerate++;
                        if (firstDegenerate < 0) firstDegenerate = t;
                        continue;
                    }

                    Vector3 ab = verts[b] - verts[a];
                    Vector3 ac = verts[c] - verts[a];
                    float area2 = Vector3.Cross(ab, ac).sqrMagnitude; // 4x squared area
                    if (area2 < 1e-8f)
                    {
                        degenerate++;
                        if (firstDegenerate < 0) firstDegenerate = t;
                    }
                }

                if (degenerate > 0)
                {
                    int cell = firstDegenerate / 6;
                    int cx = cell % cells;
                    int cz = cell / cells;
                    failures.Add($"{mesh.name}: {degenerate}/{cells * cells} triangles are degenerate " +
                                 $"(first at cell {cx},{cz}); that is the black-wedge signature");
                }
            }

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        /// <summary>
        /// The world grid is 1 m, so a triangle edge may only span one cell horizontally.
        /// The check is on the XZ projection: a wrapped/broken index points at a vertex
        /// hundreds of metres away (the black wedges). The 3D length is not bounded here
        /// because this world has adjacent height steps up to 12.46 m, which is legal
        /// relief; vertex heights are validated against the heightmap separately.
        /// </summary>
        [Test]
        public void BakedChunkEdgesStayWithinTheGridSpacing()
        {
            const float maxHorizontalEdge = 2.0f;   // one cell diagonal, plus float noise
            var failures = new List<string>();

            foreach (Mesh mesh in LoadBakedChunks())
            {
                Vector3[] verts = mesh.vertices;
                var indices = mesh.triangles;
                float worstHorizontal = 0f;

                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                    Consider(verts[a], verts[b]);
                    Consider(verts[b], verts[c]);
                    Consider(verts[c], verts[a]);

                    void Consider(Vector3 from, Vector3 to)
                    {
                        float dx = to.x - from.x;
                        float dz = to.z - from.z;
                        worstHorizontal = Mathf.Max(worstHorizontal, Mathf.Sqrt(dx * dx + dz * dz));
                    }
                }

                if (worstHorizontal > maxHorizontalEdge)
                {
                    failures.Add($"{mesh.name}: edge spans {worstHorizontal:F1} m horizontally " +
                                 $"(limit {maxHorizontalEdge} m); broken/wrapped index");
                }
            }

            Assert.IsEmpty(failures,
                "chunks with an edge longer than the 1 m grid spacing:\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// Every baked vertex must carry the height of the signed heightmap at its grid
        /// cell. This is the contract that stops a stale or mismatched bake from shipping:
        /// the six terrain palettes are authored against these exact heights.
        /// </summary>
        [Test]
        public void BakedChunkHeightsMatchTheHeightmap()
        {
            int cells = CellsPerChunk;
            int res = World.Resolution;
            float cell = World.CellSize;
            var failures = new List<string>();

            for (int cz = 0; cz < ChunksPerAxis; cz++)
            {
                for (int cx = 0; cx < ChunksPerAxis; cx++)
                {
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{TerrainDir}/Chunk_{cx}_{cz}.asset");
                    if (mesh == null)
                    {
                        continue; // covered by BakedChunksCoverTheWholeGrid
                    }

                    Vector3[] verts = mesh.vertices;
                    int wrong = 0;
                    string first = null;
                    for (int lz = 0; lz <= cells; lz++)
                    {
                        for (int lx = 0; lx <= cells; lx++)
                        {
                            Vector3 v = verts[lz * (cells + 1) + lx];
                            float expectedY = World.Heights[(cz * cells + lz) * res + (cx * cells + lx)];

                            if (Mathf.Abs(v.y - expectedY) > 0.01f ||
                                Mathf.Abs(v.x - lx * cell) > 0.01f ||
                                Mathf.Abs(v.z - lz * cell) > 0.01f)
                            {
                                wrong++;
                                if (first == null)
                                {
                                    first = $"vertex ({lx},{lz}) at ({v.x:F2},{v.y:F2},{v.z:F2}), " +
                                            $"heightmap says y={expectedY:F2}";
                                }
                            }
                        }
                    }

                    if (wrong > 0)
                    {
                        failures.Add($"{mesh.name}: {wrong} vertices disagree with the heightmap; first {first}");
                    }
                }
            }

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void BakedChunkBoundsStayInsideTheChunkFootprint()
        {
            int cells = CellsPerChunk;
            float span = cells * World.CellSize;

            foreach (Mesh mesh in LoadBakedChunks())
            {
                Bounds b = mesh.bounds;
                Assert.IsFalse(float.IsNaN(b.center.x) || float.IsNaN(b.center.y) || float.IsNaN(b.center.z),
                    $"{mesh.name} bounds centre is NaN");
                Assert.LessOrEqual(b.size.x, span + 1e-2f, $"{mesh.name} x span");
                Assert.LessOrEqual(b.size.z, span + 1e-2f, $"{mesh.name} z span");
                Assert.GreaterOrEqual(b.min.x, -1e-2f, $"{mesh.name} min x");
                Assert.GreaterOrEqual(b.min.z, -1e-2f, $"{mesh.name} min z");
            }
        }

        [Test]
        public void ChunkBuilderMapsVerticesToTheHeightmap()
        {
            int cells = CellsPerChunk;
            Mesh mesh = WorldTerrain.BuildChunkMesh(World, 0, 0, cells);
            try
            {
                Vector3[] verts = mesh.vertices;
                Assert.AreEqual((cells + 1) * (cells + 1), verts.Length, "vertex count");

                // Local x/z run over the chunk footprint; y must be the heightmap sample.
                float cell = World.CellSize;
                for (int lz = 0; lz <= cells; lz += 32)
                {
                    for (int lx = 0; lx <= cells; lx += 32)
                    {
                        Vector3 v = verts[lz * (cells + 1) + lx];
                        Assert.AreEqual(lx * cell, v.x, 1e-2f, $"vertex ({lx},{lz}) x");
                        Assert.AreEqual(lz * cell, v.z, 1e-2f, $"vertex ({lx},{lz}) z");

                        float expected = World.Heights[lz * World.Resolution + lx];
                        Assert.AreEqual(expected, v.y, 0.05f, $"vertex ({lx},{lz}) height");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void AdjacentChunksShareTheirSeamHeights()
        {
            int cells = CellsPerChunk;
            Mesh left = WorldTerrain.BuildChunkMesh(World, 0, 0, cells);
            Mesh right = WorldTerrain.BuildChunkMesh(World, 1, 0, cells);
            try
            {
                Vector3[] a = left.vertices;
                Vector3[] b = right.vertices;
                int row = cells;
                for (int lz = 0; lz <= cells; lz++)
                {
                    float leftHeight = a[lz * (cells + 1) + cells].y; // right edge of chunk 0
                    float rightHeight = b[lz * (cells + 1) + 0].y;    // left edge of chunk 1
                    Assert.AreEqual(leftHeight, rightHeight, 1e-3f,
                        $"seam row {lz}: chunk (0,0) right edge and chunk (1,0) left edge disagree");
                }
            }
            finally
            {
                Object.DestroyImmediate(left);
                Object.DestroyImmediate(right);
            }
        }
    }
}
