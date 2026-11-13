using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Authors the Ashkar environment — sky material, both suns, fog/ambient and
    /// the film post volume — into the OPEN scene as real assets and objects.
    /// After this, Play mode does not create or override any of it.
    ///
    /// Run once, tweak by hand, then save the scene.
    /// </summary>
    public static class AuthorAshkarEnvironment
    {
        private const string RootName = "[AI003] Environment";
        private const string Dir = "Assets/Generated/Environment";
        private const string PostPath = "Assets/Settings/AshkarPost.asset";

        private static readonly Color Zenith = Hex("5E6B78");
        private static readonly Color Horizon = Hex("E4BE93");
        private static readonly Color Ground = Hex("C9BFAE");
        private static readonly Color SunColor = Hex("FFEFD6");
        private static readonly Color Sun2Color = Hex("E08A5C");

        [MenuItem("AI003/Author Ashkar Environment")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);

            var existing = GameObject.Find(RootName);
            var root = existing != null ? existing : new GameObject(RootName);

            ApplySky();
            CreateSuns(root.transform);
            CreatePost(root.transform);
            ApplyFogAndAmbient();

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[AI003][AUTHOR] environment placed under '{RootName}' — tweak it, then save the scene");
        }

        private static void ApplySky()
        {
            var shader = Shader.Find("AI003/AshkarSky");
            if (shader == null)
            {
                Debug.LogError("[AI003][AUTHOR] AI003/AshkarSky shader missing");
                return;
            }

            string path = $"{Dir}/AshkarSky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (sky == null)
            {
                sky = new Material(shader);
                AssetDatabase.CreateAsset(sky, path);
            }
            else
            {
                sky.shader = shader;
            }

            Vector3 sunDir = Direction(-62f, 21f);
            Vector3 sun2Dir = Direction(-62f + 128f, 26f);
            sky.SetColor("_Zenith", Zenith.linear);
            sky.SetColor("_Horizon", Horizon.linear);
            sky.SetColor("_Ground", Ground.linear);
            sky.SetVector("_SunDir", sunDir);
            sky.SetColor("_SunColor", SunColor.linear);
            sky.SetFloat("_SunSize", Mathf.Cos(1.15f * Mathf.Deg2Rad));
            sky.SetVector("_Sun2Dir", sun2Dir);
            sky.SetColor("_Sun2Color", Sun2Color.linear);
            sky.SetFloat("_Sun2Size", Mathf.Cos(0.75f * Mathf.Deg2Rad));
            sky.SetFloat("_Haze", 0.45f);
            sky.SetFloat("_DustBand", 0.25f);
            sky.SetFloat("_Stars", 0f);
            sky.SetColor("_FogColor", Horizon.linear);
            sky.SetFloat("_HorizonBand", 0.55f);

            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;
        }

        private static void CreateSuns(Transform parent)
        {
            var sunObject = EnsureChild(parent, "Sun (Hes)");
            sunObject.transform.rotation = Quaternion.LookRotation(-Direction(-62f, 21f), Vector3.up);
            var sun = EnsureBehaviour<Light>(sunObject);
            sun.type = LightType.Directional;
            sun.color = SunColor;
            sun.intensity = 1.7f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.9f;

            var sun2Object = EnsureChild(parent, "Sun2 (Vyr)");
            sun2Object.transform.rotation = Quaternion.LookRotation(-Direction(-62f + 128f, 26f), Vector3.up);
            var sun2 = EnsureBehaviour<Light>(sun2Object);
            sun2.type = LightType.Directional;
            sun2.color = Sun2Color;
            sun2.intensity = 0.22f;
            sun2.shadows = LightShadows.None;
        }

        /// <summary>Direct child by name, created in place of a destroy/rebuild.</summary>
        private static GameObject EnsureChild(Transform parent, string name)
        {
            var child = parent.Find(name);
            if (child != null)
            {
                return child.gameObject;
            }

            var created = new GameObject(name);
            created.transform.SetParent(parent, false);
            return created;
        }

        private static T EnsureBehaviour<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            return component != null ? component : go.AddComponent<T>();
        }

        /// <summary>
        /// Rebuilds the film look as a real, committed VolumeProfile. The values are the
        /// calibrated ones (postExposure 1.25 measured against the L0 brightness), so
        /// re-running this menu reproduces the shipped look instead of a stale draft.
        /// </summary>
        private static void CreatePost(Transform parent)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostPath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, PostPath);
            }

            // Overrides are updated in place. Recreating them would give each
            // sub-asset a new id and churn AshkarPost.asset on every re-run.
            var tone = EnsureVolume<Tonemapping>(profile);
            tone.mode.overrideState = true;
            tone.mode.value = TonemappingMode.ACES;

            var color = EnsureVolume<ColorAdjustments>(profile);
            color.postExposure.overrideState = true;
            color.postExposure.value = 1.25f;
            color.contrast.overrideState = true;
            color.contrast.value = 14f;
            color.saturation.overrideState = true;
            color.saturation.value = -14f;
            color.colorFilter.overrideState = true;
            color.colorFilter.value = new Color(1f, 0.97f, 0.92f);

            var balance = EnsureVolume<WhiteBalance>(profile);
            balance.temperature.overrideState = true;
            balance.temperature.value = 8f;

            var split = EnsureVolume<SplitToning>(profile);
            split.shadows.overrideState = true;
            split.shadows.value = new Color(0.32f, 0.40f, 0.48f);
            split.highlights.overrideState = true;
            split.highlights.value = new Color(1f, 0.87f, 0.70f);
            split.balance.overrideState = true;
            split.balance.value = -5f;

            var bloom = EnsureVolume<Bloom>(profile);
            bloom.threshold.overrideState = true;
            bloom.threshold.value = 1.60f;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = 0.30f;
            bloom.scatter.overrideState = true;
            bloom.scatter.value = 0.72f;

            var vignette = EnsureVolume<Vignette>(profile);
            vignette.intensity.overrideState = true;
            vignette.intensity.value = 0.30f;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.55f;

            var grain = EnsureVolume<FilmGrain>(profile);
            grain.intensity.overrideState = true;
            grain.intensity.value = 0.18f;
            grain.response.overrideState = true;
            grain.response.value = 0.8f;

            var chromatic = EnsureVolume<ChromaticAberration>(profile);
            chromatic.intensity.overrideState = true;
            chromatic.intensity.value = 0.10f;

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var volumeObject = EnsureChild(parent, "Post Volume");
            var volume = EnsureBehaviour<Volume>(volumeObject);
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.sharedProfile = profile;
        }

        /// <summary>
        /// Existing override or a new sub-asset. A VolumeComponent created by
        /// VolumeProfile.Add() is a plain object until it is added to the asset,
        /// otherwise it saves as a null reference.
        /// </summary>
        private static T EnsureVolume<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet(out T existing))
            {
                return existing;
            }

            var created = profile.Add<T>(true);
            created.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
            AssetDatabase.AddObjectToAsset(created, profile);
            return created;
        }

        private static void ApplyFogAndAmbient()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Color.Lerp(Horizon, Hex("D8D2C6"), 0.55f);
            RenderSettings.fogStartDistance = 700f;
            RenderSettings.fogEndDistance = 7000f;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Hex("8FA3B2") * 0.50f;
            RenderSettings.ambientEquatorColor = Hex("C0A98C") * 0.32f;
            RenderSettings.ambientGroundColor = Hex("3A2E26") * 0.40f;
            RenderSettings.reflectionIntensity = 0.4f;
        }

        private static Vector3 Direction(float yawDeg, float elevationDeg)
        {
            float yaw = yawDeg * Mathf.Deg2Rad;
            float el = elevationDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(yaw) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(yaw) * Mathf.Cos(el));
        }

        private static Color Hex(string hex)
        {
            return ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.magenta;
        }
    }
}
