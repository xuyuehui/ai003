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
            if (existing != null)
            {
                Object.DestroyImmediate(existing);
            }

            var root = new GameObject(RootName);

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
            var sunObject = new GameObject("Sun (Hes)");
            sunObject.transform.SetParent(parent, false);
            sunObject.transform.rotation = Quaternion.LookRotation(-Direction(-62f, 21f), Vector3.up);
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = SunColor;
            sun.intensity = 1.7f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.9f;

            var sun2Object = new GameObject("Sun2 (Vyr)");
            sun2Object.transform.SetParent(parent, false);
            sun2Object.transform.rotation = Quaternion.LookRotation(-Direction(-62f + 128f, 26f), Vector3.up);
            var sun2 = sun2Object.AddComponent<Light>();
            sun2.type = LightType.Directional;
            sun2.color = Sun2Color;
            sun2.intensity = 0.22f;
            sun2.shadows = LightShadows.None;
        }

        private static void CreatePost(Transform parent)
        {
            string path = $"{Dir}/AshkarPost.asset";
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            else
            {
                profile.components.Clear();
            }

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.overrideState = true;
            tone.mode.value = TonemappingMode.ACES;

            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.overrideState = true;
            color.postExposure.value = -0.15f;
            color.contrast.overrideState = true;
            color.contrast.value = 12f;
            color.saturation.overrideState = true;
            color.saturation.value = -12f;
            color.colorFilter.overrideState = true;
            color.colorFilter.value = new Color(1f, 0.97f, 0.92f);

            var split = profile.Add<SplitToning>(true);
            split.shadows.overrideState = true;
            split.shadows.value = new Color(0.32f, 0.40f, 0.48f);
            split.highlights.overrideState = true;
            split.highlights.value = new Color(1f, 0.87f, 0.70f);

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.overrideState = true;
            bloom.threshold.value = 1.5f;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = 0.35f;

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.overrideState = true;
            vignette.intensity.value = 0.22f;

            var grain = profile.Add<FilmGrain>(true);
            grain.intensity.overrideState = true;
            grain.intensity.value = 0.12f;

            EditorUtility.SetDirty(profile);

            var volumeObject = new GameObject("Post Volume");
            volumeObject.transform.SetParent(parent, false);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.profile = profile;
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
