using AI003.World;
using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Read-only check: are the baked vegetation vertices actually near the frame sites?
    /// The forest still shows no trees, so this separates "nothing was placed here" from
    /// "placed but not drawn".
    /// </summary>
    public static class VegetationAudit
    {
        private const string MeshDir = "Assets/Generated/Vegetation";

        [MenuItem("AI003/Audit Vegetation")]
        public static void Run()
        {
            string[] species = { "conifer", "dead_tree", "acacia_small", "acacia", "snow_rock", "snow_rock_b" };
            var sites = new[]
            {
                new Vector2(-116f, 728f), // forest frame
                new Vector2(392f, 260f),  // steppe frame
                new Vector2(336f, 176f),  // acacia anchor
            };

            foreach (string slug in species)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{MeshDir}/{slug}.asset");
                if (mesh == null)
                {
                    Debug.LogWarning($"[AI003][VEGAUDIT] {slug}: no mesh asset");
                    continue;
                }

                var v = mesh.vertices;
                var line = new System.Text.StringBuilder();
                line.Append($"[AI003][VEGAUDIT] {slug} verts={v.Length} bounds={mesh.bounds.size}");
                foreach (var site in sites)
                {
                    int within = 0;
                    float nearest = float.MaxValue;
                    foreach (var p in v)
                    {
                        float d = new Vector2(p.x - site.x, p.z - site.y).magnitude;
                        if (d < nearest)
                        {
                            nearest = d;
                        }
                        if (d <= 150f)
                        {
                            within++;
                        }
                    }
                    line.Append($" | site({site.x},{site.y}) within150={within} nearest={nearest:F0}");
                }
                Debug.Log(line.ToString());
            }

            // Chief's check: are the trees near the forest frame buried underground?
            var forestSite = new Vector2(-237f, 727f);
            var conifer = AssetDatabase.LoadAssetAtPath<Mesh>($"{MeshDir}/conifer.asset");
            if (conifer != null)
            {
                float yMin = float.MaxValue;
                float yMax = float.MinValue;
                double ySum = 0;
                int n = 0;
                foreach (var p in conifer.vertices)
                {
                    if (new Vector2(p.x - forestSite.x, p.z - forestSite.y).magnitude > 150f)
                    {
                        continue;
                    }
                    yMin = Mathf.Min(yMin, p.y);
                    yMax = Mathf.Max(yMax, p.y);
                    ySum += p.y;
                    n++;
                }
                Debug.Log($"[AI003][VEGAUDIT] conifer within150 of forest site: n={n} " +
                          $"yMin={yMin:F2} yMax={yMax:F2} yMean={(n > 0 ? ySum / n : 0):F2}");
            }

            var world = WorldData.Load(System.IO.Path.Combine(Application.streamingAssetsPath, "world"));
            Debug.Log($"[AI003][VEGAUDIT] terrain height at forest site = {world.HeightAt(forestSite.x, forestSite.y):F2}");

            // Source model size: an FBX imported at the wrong unit scale would place
            // centimetre-tall trees that no camera can see.
            foreach (string slug in species)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Art/Models/{slug}/{slug}_LOD0.fbx");
                var filter = model != null ? model.GetComponentInChildren<MeshFilter>() : null;
                Debug.Log($"[AI003][VEGAUDIT] source {slug} model={(model != null)} " +
                          $"meshBoundsSize={(filter != null ? filter.sharedMesh.bounds.size : Vector3.zero)} " +
                          $"modelScale={(model != null ? model.transform.localScale : Vector3.zero)}");
            }

            // Chief's check: NaN vertices / NaN world bounds make a renderer invisible to
            // every camera while its shadow pass still draws.
            foreach (string slug in species)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{MeshDir}/{slug}.asset");
                if (mesh == null)
                {
                    continue;
                }
                int nanVerts = 0;
                foreach (var p in mesh.vertices)
                {
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                        float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                    {
                        nanVerts++;
                    }
                }
                var b = mesh.bounds;
                Debug.Log($"[AI003][VEGAUDIT] {slug} nanVerts={nanVerts} " +
                          $"meshBoundsCenter={b.center} meshBoundsSize={b.size} " +
                          $"boundsFinite={IsFinite(b.center) && IsFinite(b.size)}");
            }

            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/Scenes/OpenWorld.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            var rootObject = GameObject.Find("[AI003] Vegetation");
            if (rootObject != null)
            {
                foreach (var r in rootObject.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var wb = r.bounds;
                    Debug.Log($"[AI003][VEGAUDIT] world {r.name} worldCenter={wb.center} worldSize={wb.size} " +
                              $"finite={IsFinite(wb.center) && IsFinite(wb.size)}");
                }
            }
            else
            {
                Debug.LogWarning("[AI003][VEGAUDIT] scene has no [AI003] Vegetation root");
            }
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                   !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }
    }
}
