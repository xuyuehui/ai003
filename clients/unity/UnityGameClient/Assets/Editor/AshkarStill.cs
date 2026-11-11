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
    /// Renders the "S2 · 长眠海的暮尘" key frame from the real world data with the
    /// Ashkar theme: two suns, dust sky, sand shader, haze and film post.
    ///
    ///   Unity.exe -batchmode -projectPath ... \
    ///     -executeMethod AI003.EditorTools.AshkarStill.Run -quit
    ///
    /// Output: artifacts/ashkar/s2_*.png (1920x804, 2.39:1).
    /// </summary>
    public static class AshkarStill
    {
        private const int Width = 1920;
        private const int Height = 804;

        private static readonly Color SkyZenith = FromHex("31343A");
        private static readonly Color SkyHorizon = FromHex("E4BE93");
        private static readonly Color SkyGround = FromHex("6B4F36");
        private static readonly Color SunColor = FromHex("FFEFD6");
        private static readonly Color Sun2Color = FromHex("E08A5C");

        [MenuItem("AI003/Render Ashkar Still")]
        public static void Run()
        {
            try
            {
                var paths = RenderAll();
                foreach (string p in paths)
                {
                    Debug.Log($"[AI003][ASHKAR] PASS {p}");
                }
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][ASHKAR] FAIL: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static string[] RenderAll()
        {
            string outDir = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "..", "artifacts", "ashkar"));
            Directory.CreateDirectory(outDir);

            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            var theme = WorldTheme.CreateAshkar();
            var colors = theme.BuildColorGrid(data);

            var root = new GameObject("[AI003][ASHKAR] World");
            var terrain = root.AddComponent<WorldTerrain>();
            terrain.Build(data, colors, "AI003/AshkarTerrain");
            terrain.TerrainMaterial.SetFloat("_Smoothness", 0.22f);
            terrain.TerrainMaterial.SetFloat("_RippleStrength", 0.11f);
            terrain.TerrainMaterial.SetFloat("_Speckle", 0.22f);

            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.name.StartsWith("Chunk_"))
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }

            ApplySaltMaterial(terrain);
            if (terrain.Water != null)
            {
                // Push the salt-sea plane past the horizon so its edge never shows.
                terrain.Water.transform.localScale = new Vector3(24f, 1f, 24f);
            }

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp != null)
            {
                urp.shadowDistance = 700f;
            }

            Vector3 viewpoint = FindViewpoint(data, out Vector3 lookDir, out float crestElevation);
            Debug.Log($"[AI003][ASHKAR] viewpoint={viewpoint} dir={lookDir} crestElev={crestElevation:F1} " +
                      $"h={data.HeightAt(viewpoint.x, viewpoint.z):F1}");

            float yaw = Mathf.Atan2(lookDir.x, lookDir.z) * Mathf.Rad2Deg;
            float pitch = -7f;
            float sunElevation = 4.5f;

            var results = new[]
            {
                Shot(data, outDir, "s2_a", viewpoint, yaw + 120f, -5f, 5f, 4.5f, 1.7f, 0.18f, 34f),
                Shot(data, outDir, "s2_b", viewpoint, yaw + 150f, -5f, 9f, 6f, 1.7f, 0.18f, 34f),
                Shot(data, outDir, "s2_c", viewpoint, yaw + 120f, -3f, 14f, 8f, 1.8f, 0.18f, 36f),
                Shot(data, outDir, "s2_d", viewpoint, yaw + 90f, -6f, 3f, 3f, 1.6f, 0.18f, 30f),
            };

            UnityEngine.Object.DestroyImmediate(root);
            return results;
        }

        private static string Shot(WorldData data, string dir, string name, Vector3 viewpoint, float yaw,
            float pitch, float camHeight, float sunElevation, float sunIntensity, float secondSun, float fov)
        {
            var spawned = new System.Collections.Generic.List<GameObject>();

            var sunDir = Direction(yaw, sunElevation);
            var secondDir = Direction(yaw + 128f, 22f);

            var sunObject = new GameObject("Sun");
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = SunColor;
            sun.intensity = sunIntensity;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.9f;
            sunObject.transform.rotation = Quaternion.LookRotation(-sunDir, Vector3.up);
            spawned.Add(sunObject);

            var sun2Object = new GameObject("Sun2");
            var sun2 = sun2Object.AddComponent<Light>();
            sun2.type = LightType.Directional;
            sun2.color = Sun2Color;
            sun2.intensity = secondSun;
            sun2.shadows = LightShadows.None;
            sun2Object.transform.rotation = Quaternion.LookRotation(-secondDir, Vector3.up);
            spawned.Add(sun2Object);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = FromHex("8FA3B2") * 0.50f;
            RenderSettings.ambientEquatorColor = FromHex("C0A98C") * 0.32f;
            RenderSettings.ambientGroundColor = FromHex("3A2E26") * 0.40f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Color.Lerp(SkyHorizon, FromHex("D8D2C6"), 0.55f);
            RenderSettings.fogStartDistance = 900f;
            RenderSettings.fogEndDistance = 7000f;

            var skyMat = new Material(Shader.Find("AI003/AshkarSky")) { name = "AshkarSky" };
            skyMat.SetColor("_Zenith", SkyZenith.linear);
            skyMat.SetColor("_Horizon", SkyHorizon.linear);
            skyMat.SetColor("_Ground", SkyGround.linear);
            skyMat.SetVector("_SunDir", sunDir);
            skyMat.SetColor("_SunColor", SunColor.linear);
            skyMat.SetFloat("_SunSize", Mathf.Cos(1.15f * Mathf.Deg2Rad));
            skyMat.SetVector("_Sun2Dir", secondDir);
            skyMat.SetColor("_Sun2Color", Sun2Color.linear);
            skyMat.SetFloat("_Sun2Size", Mathf.Cos(0.75f * Mathf.Deg2Rad));
            skyMat.SetFloat("_Haze", 0.45f);
            skyMat.SetFloat("_DustBand", 0.25f);
            RenderSettings.skybox = skyMat;

            var volumeObject = new GameObject("Volume");
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.profile = BuildProfile();
            spawned.Add(volumeObject);

            var cameraObject = new GameObject("Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = fov;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 9000f;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.allowHDR = true;
            camera.transform.position = new Vector3(viewpoint.x, viewpoint.y + camHeight, viewpoint.z);
            camera.transform.rotation = Quaternion.Euler(-pitch, yaw, 0f);
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            spawned.Add(cameraObject);

            string path = Path.Combine(dir, name + ".png");
            RenderTo(camera, path);

            foreach (var go in spawned)
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
            UnityEngine.Object.DestroyImmediate(skyMat);
            return path;
        }

        private static VolumeProfile BuildProfile()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.overrideState = true;
            tone.mode.value = TonemappingMode.ACES;

            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.overrideState = true;
            color.postExposure.value = -0.35f;
            color.contrast.overrideState = true;
            color.contrast.value = 14f;
            color.saturation.overrideState = true;
            color.saturation.value = -14f;
            color.colorFilter.overrideState = true;
            color.colorFilter.value = new Color(1f, 0.97f, 0.92f);

            var balance = profile.Add<WhiteBalance>(true);
            balance.temperature.overrideState = true;
            balance.temperature.value = 8f;

            var split = profile.Add<SplitToning>(true);
            split.shadows.overrideState = true;
            split.shadows.value = new Color(0.32f, 0.40f, 0.48f);
            split.highlights.overrideState = true;
            split.highlights.value = new Color(1f, 0.87f, 0.70f);
            split.balance.overrideState = true;
            split.balance.value = -5f;

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.overrideState = true;
            bloom.threshold.value = 1.60f;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = 0.42f;
            bloom.scatter.overrideState = true;
            bloom.scatter.value = 0.72f;

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.overrideState = true;
            vignette.intensity.value = 0.30f;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.55f;

            var grain = profile.Add<FilmGrain>(true);
            grain.intensity.overrideState = true;
            grain.intensity.value = 0.18f;
            grain.response.overrideState = true;
            grain.response.value = 0.8f;

            var ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.overrideState = true;
            ca.intensity.value = 0.10f;

            return profile;
        }

        private static void RenderTo(Camera camera, string path)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4,
                name = "AshkarStillRT",
            };
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
        }

        private static void ApplySaltMaterial(WorldTerrain terrain)
        {
            if (terrain.Water == null)
            {
                return;
            }
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var salt = new Material(shader) { name = "SaltFlat" };
            salt.SetColor("_BaseColor", new Color(0.95f, 0.93f, 0.88f));
            salt.SetFloat("_Smoothness", 0.62f);
            salt.SetFloat("_Metallic", 0f);
            // A flat salt pan under a 4° sun gets almost no diffuse light; a little
            // ambient emission keeps it reading as a bright crust instead of mud.
            salt.EnableKeyword("_EMISSION");
            salt.SetColor("_EmissionColor", new Color(0.30f, 0.27f, 0.22f));
            terrain.Water.GetComponent<MeshRenderer>().sharedMaterial = salt;
        }

        private static Vector3 Direction(float yawDeg, float elevationDeg)
        {
            float yaw = yawDeg * Mathf.Deg2Rad;
            float el = elevationDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(yaw) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(yaw) * Mathf.Cos(el));
        }

        /// <summary>
        /// Finds a camera spot where a dune crest crosses the view 30–90 m ahead and
        /// the ground falls away behind it, so the crest reads as a silhouette
        /// against the sky. Returns the crest's angular height for sun placement.
        /// </summary>
        private static Vector3 FindViewpoint(WorldData data, out Vector3 lookDir, out float crestElevationDeg)
        {
            float half = data.SizeMeters * 0.5f;
            float best = float.NegativeInfinity;
            Vector3 bestPoint = Vector3.zero;
            Vector3 bestDir = Vector3.forward;
            float bestCrest = 0f;

            for (float z = -half + 200f; z < half - 200f; z += 12f)
            {
                for (float x = -half + 200f; x < half - 200f; x += 12f)
                {
                    float h = data.HeightAt(x, z);
                    if (h < 30f || h > 130f)
                    {
                        continue;
                    }

                    int ix = Mathf.Clamp(Mathf.RoundToInt((x + half) / data.CellSize), 0, data.Resolution - 1);
                    int iz = Mathf.Clamp(Mathf.RoundToInt((z + half) / data.CellSize), 0, data.Resolution - 1);
                    byte biome = data.Biomes[iz * data.Resolution + ix];
                    if (biome != 2 && biome != 3)
                    {
                        continue;
                    }

                    // Stand on a knoll: the ground must fall away on most sides.
                    float lowest = float.MaxValue;
                    float lowestAngle = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        float ak = k * Mathf.PI / 4f;
                        float hk = data.HeightAt(x + Mathf.Cos(ak) * 55f, z + Mathf.Sin(ak) * 55f);
                        if (hk < lowest)
                        {
                            lowest = hk;
                            lowestAngle = ak;
                        }
                    }
                    float prominence = h - lowest;
                    if (prominence < 5f)
                    {
                        continue;
                    }

                    for (int b = 0; b < 8; b++)
                    {
                        float a = b * Mathf.PI / 4f;
                        float dx = Mathf.Cos(a);
                        float dz = Mathf.Sin(a);

                        float far = 0f;
                        int n = 0;
                        for (float d = 180f; d <= 620f; d += 70f)
                        {
                            far += data.HeightAt(x + dx * d, z + dz * d);
                            n++;
                        }
                        far /= n;

                        // Stay inland: the far view must be dunes, not the salt sea.
                        if (data.HeightAt(x + dx * 420f, z + dz * 420f) < 8f ||
                            data.HeightAt(x + dx * 900f, z + dz * 900f) < 4f)
                        {
                            continue;
                        }

                        // Score: a high knoll, an open drop ahead, and prefer the
                        // steepest fall line so the near dune face fills the frame.
                        float drop = h - far;
                        if (drop < 4f)
                        {
                            continue;
                        }
                        float score = drop + prominence * 1.3f
                                      + Mathf.Cos(a - lowestAngle) * 6f;
                        if (score > best)
                        {
                            best = score;
                            bestPoint = new Vector3(x, h, z);
                            bestDir = new Vector3(dx, 0f, dz);
                            bestCrest = 0f;
                        }
                    }
                }
            }

            lookDir = bestDir;
            crestElevationDeg = bestCrest;
            return bestPoint;
        }

        private static Color FromHex(string hex)
        {
            return ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.magenta;
        }
    }
}
