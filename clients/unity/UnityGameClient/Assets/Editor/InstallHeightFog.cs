using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AI003.EditorTools
{
    /// <summary>
    /// Installs the fullscreen height-fog pass into the PC renderer (idempotent).
    /// URP 17.6 has no built-in volumetric fog, so the pass is a
    /// FullScreenPassRendererFeature driven by AI003/HeightFog.
    /// </summary>
    public static class InstallHeightFog
    {
        private const string MaterialPath = "Assets/Settings/HeightFog.mat";

        [MenuItem("AI003/Install Height Fog")]
        public static void Run()
        {
            Shader shader = Shader.Find("AI003/HeightFog");
            if (shader == null)
            {
                Debug.LogError("[AI003][FOG] shader AI003/HeightFog not found");
                return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "HeightFog" };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            else
            {
                material.shader = shader;
            }

            material.SetColor("_FogColor", new Color(0.66f, 0.52f, 0.37f));
            material.SetFloat("_Density", 0.010f);
            material.SetFloat("_Falloff", 0.020f);
            material.SetFloat("_StartDistance", 80f);
            material.SetFloat("_BaseHeight", 0f);
            EditorUtility.SetDirty(material);

            ScriptableRendererData rendererData = FindRendererData();
            if (rendererData == null)
            {
                Debug.LogError("[AI003][FOG] renderer data not found");
                return;
            }

            rendererData.rendererFeatures.RemoveAll(f => f != null && f.name == "HeightFog");

            var feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
            feature.name = "HeightFog";
            feature.passMaterial = material;
            feature.passIndex = 0;
            feature.fetchColorBuffer = true;
            feature.requirements = ScriptableRenderPassInput.Depth;
            feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingTransparents;

            rendererData.rendererFeatures.Add(feature);
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AI003][FOG] installed into {rendererData.name} " +
                      $"(features={rendererData.rendererFeatures.Count})");
        }

        private static ScriptableRendererData FindRendererData()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                return null;
            }

            var field = typeof(UniversalRenderPipelineAsset).GetField(
                "m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                return null;
            }

            var list = field.GetValue(pipeline) as ScriptableRendererData[];
            return list != null && list.Length > 0 ? list[0] : null;
        }
    }
}
