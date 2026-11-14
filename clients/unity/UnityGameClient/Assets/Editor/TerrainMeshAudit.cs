using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Read-only audit of the baked terrain meshes: NaN vertices, degenerate triangles
    /// and impossibly long edges. Written to find the black shards in the still frames
    /// without guessing (shadows and vegetation were already ruled out).
    /// </summary>
    public static class TerrainMeshAudit
    {
        private const string MeshDir = "Assets/Generated/Terrain";

        [MenuItem("AI003/Audit Terrain Meshes")]
        public static void Run()
        {
            var paths = Directory.GetFiles(MeshDir, "*.asset");
            int nanMeshes = 0;
            int degenerateMeshes = 0;
            int longEdgeMeshes = 0;
            int totalDegenerate = 0;
            float worstEdge = 0f;
            string worstEdgeMesh = "-";

            foreach (string path in paths)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null)
                {
                    continue;
                }

                var v = mesh.vertices;
                var t = mesh.triangles;

                int nan = 0;
                foreach (var p in v)
                {
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                        float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                    {
                        nan++;
                    }
                }

                int degenerate = 0;
                float maxEdge = 0f;
                int worstA = -1;
                int worstB = -1;
                int worstC = -1;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector3 a = v[t[i]];
                    Vector3 b = v[t[i + 1]];
                    Vector3 c = v[t[i + 2]];
                    float e = Mathf.Max((a - b).magnitude, Mathf.Max((b - c).magnitude, (c - a).magnitude));
                    if (e > maxEdge)
                    {
                        maxEdge = e;
                        worstA = t[i];
                        worstB = t[i + 1];
                        worstC = t[i + 2];
                    }
                    if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-8f)
                    {
                        degenerate++;
                    }
                }

                // The ocean plane is 163 km wide on purpose; only chunks are audited.
                bool isChunk = Path.GetFileName(path).StartsWith("Chunk_");
                bool longEdge = isChunk && maxEdge > 400f;
                if (nan > 0)
                {
                    nanMeshes++;
                }
                if (degenerate > 0)
                {
                    degenerateMeshes++;
                    totalDegenerate += degenerate;
                }
                if (longEdge)
                {
                    longEdgeMeshes++;
                }
                if (maxEdge > worstEdge)
                {
                    worstEdge = maxEdge;
                    worstEdgeMesh = Path.GetFileNameWithoutExtension(path);
                }

                if (nan > 0 || degenerate > 0 || longEdge)
                {
                    Debug.LogWarning($"[AI003][AUDIT] {Path.GetFileName(path)} verts={v.Length} " +
                                     $"nan={nan} degenerate={degenerate} maxEdge={maxEdge:F1} " +
                                     $"bounds={mesh.bounds.size}");
                    if (longEdge && worstA >= 0)
                    {
                        Debug.LogWarning($"[AI003][AUDIT]   worst tri idx=({worstA},{worstB},{worstC}) " +
                                         $"a={v[worstA]} b={v[worstB]} c={v[worstC]} " +
                                         $"subMeshes={mesh.subMeshCount}");
                    }
                }
            }

            Debug.Log($"[AI003][AUDIT] meshes={paths.Length} nanMeshes={nanMeshes} " +
                      $"degenerateMeshes={degenerateMeshes} totalDegenerate={totalDegenerate} " +
                      $"longEdgeMeshes={longEdgeMeshes} worstEdge={worstEdge:F1} ({worstEdgeMesh})");
        }
    }
}
