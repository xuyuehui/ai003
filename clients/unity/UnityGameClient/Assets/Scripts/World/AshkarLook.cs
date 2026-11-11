using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AI003.World
{
    /// <summary>
    /// Applies the Ashkar look at runtime (Play mode), so what you see in the
    /// editor is the same palette, sky, two suns and film post as the key frames.
    /// The offline still tool keeps its own per-shot tuning.
    /// </summary>
    public static class AshkarLook
    {
        public static readonly Color Zenith = Hex("5E6B78");
        public static readonly Color Horizon = Hex("E4BE93");
        /// <summary>Below-horizon sky == fog, so the map edge never shows as a slab.</summary>
        public static readonly Color Ground = Hex("C9BFAE");
        public static readonly Color SunColor = Hex("FFEFD6");
        public static readonly Color Sun2Color = Hex("E08A5C");

        /// <summary>Everything: used when the scene has no authored environment.</summary>
        public static void Apply(WorldBootstrap boot)
        {
            if (boot == null || boot.Data == null || boot.Terrain == null)
            {
                return;
            }

            ApplyScene(boot);
            ApplyMaterials(boot);
        }

        /// <summary>Camera, suns, sky, fog and post — skipped when the scene owns them.</summary>
        public static void ApplyScene(WorldBootstrap boot)
        {
            ConfigureCamera(boot);
            ConfigureLights(boot);
            ConfigureSky();
            ConfigureFog();
            ConfigureVolume();
        }

        /// <summary>Terrain and water materials — always applied, even for authored scenes.</summary>
        public static void ApplyMaterials(WorldBootstrap boot)
        {
            if (boot == null || boot.Data == null || boot.Terrain == null)
            {
                return;
            }
            ConfigureMaterials(boot);
        }

        private static void ConfigureCamera(WorldBootstrap boot)
        {
            if (boot.PlayerCamera != null)
            {
                boot.PlayerCamera.farClipPlane = 8000f;
                boot.PlayerCamera.clearFlags = CameraClearFlags.Skybox;
                boot.PlayerCamera.allowHDR = true;
                var data = boot.PlayerCamera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
            }
        }

        private static void ConfigureLights(WorldBootstrap boot)
        {
            Light sun = null;
            foreach (var light in boot.GetComponentsInChildren<Light>())
            {
                if (light.type == LightType.Directional)
                {
                    sun = light;
                    break;
                }
            }

            Vector3 sunDir = Direction(-62f, 21f);
            if (sun != null)
            {
                sun.transform.rotation = Quaternion.LookRotation(-sunDir, Vector3.up);
                sun.color = SunColor;
                sun.intensity = 1.7f;
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.9f;
            }

            // The second sun never casts shadows; it is a warm fill from the side.
            var second = new GameObject("[AI003] Sun2");
            second.transform.SetParent(boot.transform, false);
            Vector3 sun2Dir = Direction(-62f + 128f, 26f);
            second.transform.rotation = Quaternion.LookRotation(-sun2Dir, Vector3.up);
            var light2 = second.AddComponent<Light>();
            light2.type = LightType.Directional;
            light2.color = Sun2Color;
            light2.intensity = 0.22f;
            light2.shadows = LightShadows.None;

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp != null)
            {
                urp.shadowDistance = 320f;
            }
        }

        private static void ConfigureSky()
        {
            var shader = Shader.Find("AI003/AshkarSky");
            if (shader == null)
            {
                return;
            }
            var sky = new Material(shader) { name = "AshkarSky" };
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
            RenderSettings.skybox = sky;
        }

        private static void ConfigureFog()
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

        private static void ConfigureVolume()
        {
            var existing = Object.FindAnyObjectByType<Volume>();
            Volume volume = existing;
            if (volume == null)
            {
                var go = new GameObject("[AI003] Post");
                volume = go.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
            }

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            volume.profile = profile;

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
        }

        private static void ConfigureMaterials(WorldBootstrap boot)
        {
            var material = boot.Terrain.TerrainMaterial;
            if (material != null)
            {
                material.SetTexture("_DetailNormal", AshkarTextures.DetailNormal());
                material.SetFloat("_DetailScale1", 0.085f);
                material.SetFloat("_DetailScale2", 0.34f);
                material.SetFloat("_DetailStrength", 0.55f);
                material.SetFloat("_RippleStrength", 0.11f);
                material.SetFloat("_Speckle", 0.22f);
                material.SetFloat("_Smoothness", 0.22f);
                material.SetColor("_HeightFogColor", Color.Lerp(Horizon, Hex("D8D2C6"), 0.55f).linear);
                material.SetFloat("_HeightFogStrength", 0.22f);
                material.SetFloat("_HeightFogDistance", 3000f);
            }

            // The sea becomes the white salt pan.
            if (boot.Terrain.Water != null)
            {
                var salt = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "SaltFlat" };
                salt.SetColor("_BaseColor", new Color(0.95f, 0.93f, 0.88f));
                salt.SetFloat("_Smoothness", 0.55f);
                salt.SetFloat("_Metallic", 0f);
                boot.Terrain.Water.GetComponent<MeshRenderer>().sharedMaterial = salt;
            }
        }

        public static Vector3 Direction(float yawDeg, float elevationDeg)
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
