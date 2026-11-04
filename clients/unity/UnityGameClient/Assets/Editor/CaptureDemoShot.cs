using System;
using System.IO;
using AI003.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AI003.EditorTools
{
    /// <summary>
    /// Deterministic demo frame for the Unity vs UE5 comparison. Same world,
    /// same character, fixed camera pose and fog as the UE `-AI003DemoShot`:
    ///
    ///   camera = spawn + (0, 3.2, -6), looking at spawn + (0, 1.2, 0), FOV 62
    ///   character at spawn, yaw 180 (faces the camera), idle pose
    ///
    /// Run in batch mode with graphics:
    ///   Unity.exe -batchmode -projectPath ... \
    ///     -executeMethod AI003.EditorTools.CaptureDemoShot.Run -quit
    /// </summary>
    public static class CaptureDemoShot
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const string CharacterPrefab = "Characters/KenneyProtagonists/Player";

        public static void Run()
        {
            try
            {
                var path = Render();
                Debug.Log($"[AI003][DEMO-SHOT] PASS {path}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][DEMO-SHOT] FAIL: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static string Render()
        {
            string artifacts = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "..", "artifacts", "demo"));
            Directory.CreateDirectory(artifacts);

            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));

            var root = new GameObject("[AI003][DEMO] World");
            var terrain = root.AddComponent<WorldTerrain>();
            terrain.Build(data);

            var sunObject = new GameObject("[AI003][DEMO] Sun");
            sunObject.transform.rotation = Quaternion.Euler(46f, -34f, 0f);
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 2.6f;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.64f, 0.69f, 0.76f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.70f, 0.77f, 0.85f);
            RenderSettings.fogStartDistance = 240f;
            RenderSettings.fogEndDistance = 1900f;

            // Procedural skybox so both engines show a blue gradient instead of
            // Unity's previous flat clear colour.
            var skybox = new Material(Shader.Find("Skybox/Procedural"));
            skybox.SetFloat("_SunDisk", 2f);
            skybox.SetFloat("_AtmosphereThickness", 1f);
            skybox.SetColor("_SkyTint", new Color(0.5f, 0.6f, 0.7f));
            skybox.SetColor("_GroundColor", new Color(0.35f, 0.4f, 0.45f));
            skybox.SetFloat("_Exposure", 1.7f);
            RenderSettings.skybox = skybox;

            // ACES tonemapping in a global volume so both engines use a filmic
            // curve instead of Unity's default linear-to-sRGB look.
            var volumeObject = new GameObject("[AI003][DEMO] Volume");
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            volume.profile = profile;
            var tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.ACES;
            var colorAdjustments = profile.Add<ColorAdjustments>(true);
            colorAdjustments.postExposure.overrideState = true;
            colorAdjustments.postExposure.value = 0f;

            GameObject prefab = Resources.Load<GameObject>(CharacterPrefab);
            if (prefab == null)
            {
                throw new InvalidOperationException($"missing Resources/{CharacterPrefab}");
            }
            GameObject player = UnityEngine.Object.Instantiate(prefab);
            player.name = "[AI003][DEMO] Player";
            player.transform.position = data.Spawn;
            player.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var animator = player.GetComponent<Animator>();
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Play(0, 0, 0.2f);
                animator.Update(0.2f);
            }

            var cameraObject = new GameObject("[AI003][DEMO] Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 62f;
            camera.nearClipPlane = 0.15f;
            camera.farClipPlane = 6000f;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.transform.position = data.Spawn + new Vector3(0f, 3.2f, -6f);
            camera.transform.LookAt(data.Spawn + new Vector3(0f, 1.2f, 0f));
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            string path = Path.Combine(artifacts, "unity.png");
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            camera.targetTexture = target;
            camera.Render();

            RenderTexture.active = target;
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());

            RenderTexture.active = null;
            camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(volumeObject);
            UnityEngine.Object.DestroyImmediate(player);
            UnityEngine.Object.DestroyImmediate(sunObject);
            UnityEngine.Object.DestroyImmediate(root);

            return path;
        }
    }
}

