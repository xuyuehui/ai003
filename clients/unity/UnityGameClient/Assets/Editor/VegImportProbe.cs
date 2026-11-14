using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Decides where the two-material tree is lost: in the FBX import or in Combine().
    /// Prints the imported model's mesh/submesh normals, then the baked asset's, in
    /// one run so the numbers come from the same editor session.
    /// </summary>
    public static class VegImportProbe
    {
        private static readonly string[] Species = { "conifer", "acacia_small", "snow_rock" };

        [MenuItem("AI003/Probe Vegetation Import")]
        public static void Run()
        {
            foreach (string sp in Species)
            {
                string path = $"Assets/Art/Models/{sp}/{sp}_LOD0.fbx";
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            AssetDatabase.Refresh();

            foreach (string sp in Species)
            {
                string path = $"Assets/Art/Models/{sp}/{sp}_LOD0.fbx";
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null)
                {
                    Debug.Log($"[AI003][PROBE] {sp}: model NOT loaded");
                    continue;
                }

                var filters = model.GetComponentsInChildren<MeshFilter>(true);
                Debug.Log($"[AI003][PROBE] {sp}: childMeshFilters={filters.Length}");
                for (int i = 0; i < filters.Length; i++)
                {
                    Mesh m = filters[i].sharedMesh;
                    var r = filters[i].GetComponent<MeshRenderer>();
                    int slots = r != null ? r.sharedMaterials.Length : -1;
                    Debug.Log($"[AI003][PROBE] {sp} mesh[{i}] name={(m != null ? m.name : "null")} " +
                              $"subMeshes={(m != null ? m.subMeshCount : -1)} verts={(m != null ? m.vertexCount : -1)} " +
                              $"normals={(m != null && m.normals != null ? m.normals.Length : -1)} " +
                              $"uv2={(m != null && m.uv2 != null && m.uv2.Length > 0)} materialSlots={slots}");
                }
            }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/OpenWorld.unity", OpenSceneMode.Single);
            AuthorVegetation.Run();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            foreach (string sp in Species)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"Assets/Generated/Vegetation/{sp}.asset");
                var root = GameObject.Find("[AI003] Vegetation");
                var child = root != null ? root.transform.Find(sp) : null;
                var renderer = child != null ? child.GetComponent<MeshRenderer>() : null;
                string maps = "-";
                if (renderer != null && renderer.sharedMaterials != null)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var mat in renderer.sharedMaterials)
                    {
                        var tex = mat != null ? mat.GetTexture("_BaseMap") : null;
                        parts.Add(mat == null ? "null" : (tex != null ? tex.name : mat.name + ":noBaseMap"));
                    }
                    maps = string.Join(",", parts.ToArray());
                }
                Debug.Log($"[AI003][PROBE] {sp} baked subMeshes={(mesh != null ? mesh.subMeshCount : -1)} " +
                          $"verts={(mesh != null ? mesh.vertexCount : -1)} " +
                          $"normals={(mesh != null && mesh.normals != null ? mesh.normals.Length : -1)} " +
                          $"materialSlots={(renderer != null ? renderer.sharedMaterials.Length : -1)} baseMaps=[{maps}]");
            }

            Debug.Log("[AI003][PROBE] done");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }
    }
}
