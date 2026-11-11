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
    /// Offscreen key frames for the Ashkar theme, rendered from the real world data:
    ///   S1 · 第一井的清晨   — sunken plaza, wellspire, dawn haze
    ///   S2 · 长眠海的暮尘   — dune sea, two suns, figures for scale
    ///   S4 · 霜夜的冷火     — salt mirror, coldfire crystals, moons
    ///
    ///   Unity.exe -batchmode -projectPath ... -executeMethod AI003.EditorTools.AshkarStill.Run -quit
    ///
    /// Output: artifacts/ashkar/*.png (1920x804, 2.39:1).
    /// </summary>
    public static class AshkarStill
    {
        private const int Width = 1920;
        private const int Height = 804;

        private static readonly Color SunColor = FromHex("FFEFD6");
        private static readonly Color Sun2Color = FromHex("E08A5C");

        private struct Look
        {
            public Color zenith;
            public Color horizon;
            public Color ground;
            public Color sunColor;
            public Color sun2Color;
            public Color fog;
            public float sunElevation;
            public float sun2Elevation;
            public float sunYawOffset;
            public float sunIntensity;
            public float sun2Intensity;
            public float fogStart;
            public float fogEnd;
            public float exposure;
            public float stars;
            public float bloom;
        }

        /// <summary>Batch entry point: render, log, quit. Do not call from the GUI.</summary>
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

        /// <summary>Menu / MCP entry point: renders in place, leaves the editor open.</summary>
        [MenuItem("AI003/Render Ashkar Still")]
        public static void RenderFromMenu()
        {
            var paths = RenderAll();
            foreach (string p in paths)
            {
                Debug.Log($"[AI003][ASHKAR] PASS {p}");
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
            var detail = AshkarTextures.DetailNormal();
            terrain.TerrainMaterial.SetTexture("_DetailNormal", detail);
            terrain.TerrainMaterial.SetFloat("_DetailScale1", 0.085f);
            terrain.TerrainMaterial.SetFloat("_DetailScale2", 0.34f);
            terrain.TerrainMaterial.SetFloat("_DetailStrength", 0.55f);

            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.name.StartsWith("Chunk_"))
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }

            if (terrain.Water != null)
            {
                terrain.Water.transform.localScale = new Vector3(24f, 1f, 24f);
            }

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp != null)
            {
                urp.shadowDistance = 420f;
            }

            Vector3 site = FindViewpoint(data, out Vector3 viewDir);
            Debug.Log($"[AI003][ASHKAR] site={site} dir={viewDir} h={data.HeightAt(site.x, site.z):F1}");

            var paths = new System.Collections.Generic.List<string>();
            Run("S2", () => paths.Add(RenderS2(data, terrain, outDir, site, viewDir)));
            Run("S1", () => paths.Add(RenderS1(data, terrain, outDir, site, viewDir)));
            Run("S4", () => paths.Add(RenderS4(data, terrain, outDir)));

            UnityEngine.Object.DestroyImmediate(root);
            return paths.ToArray();
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][ASHKAR] shot {name} failed: {e}");
            }
        }

        // ------------------------------------------------------------- S2 ---
        private static string RenderS2(WorldData data, WorldTerrain terrain, string outDir, Vector3 site, Vector3 viewDir)
        {
            Vector3 right = new Vector3(viewDir.z, 0f, -viewDir.x);
            var camPos = site + Vector3.up * 3.5f;
            float yaw = Yaw(viewDir);
            float pitch = 4f;

            var props = new GameObject("S2Props");
            var body = AshkarProps.Material("BasaltBody", new Color(0.12f, 0.10f, 0.09f), 0.25f);
            var trim = AshkarProps.Material("Brass", new Color(0.42f, 0.31f, 0.16f), 0.45f, 0.6f);
            var robe = AshkarProps.Material("Robes", new Color(0.09f, 0.07f, 0.06f), 0.1f);

            var spirePos = Ground(data, site + viewDir * 250f + right * 100f);
            var spire = AshkarProps.Wellspire(spirePos, 85f, body, trim);
            spire.transform.SetParent(props.transform, true);

            Vector3 figBase = site + viewDir * 22f;
            var f1 = AshkarProps.Figure(Ground(data, figBase + right * 1.4f), yaw + 180f, 1.85f, robe);
            f1.transform.SetParent(props.transform, true);
            var f2 = AshkarProps.Figure(Ground(data, figBase + right * 2.8f), yaw + 175f, 1.78f, robe);
            f2.transform.SetParent(props.transform, true);

            var look = new Look
            {
                zenith = FromHex("31343A"),
                horizon = FromHex("E4BE93"),
                ground = FromHex("6B4F36"),
                sunColor = SunColor,
                sun2Color = Sun2Color,
                fog = Color.Lerp(FromHex("E4BE93"), FromHex("D8D2C6"), 0.55f),
                sunElevation = 5.0f,
                sun2Elevation = 24f,
                sunIntensity = 1.75f,
                sun2Intensity = 0.18f,
                fogStart = 900f,
                fogEnd = 7000f,
                exposure = -0.35f,
                stars = 0f,
                bloom = 0.42f,
            };

            Debug.Log($"[AI003][ASHKAR] S2 cam={camPos} yaw={yaw:F1} pitch={pitch:F1} spire={spirePos} " +
                      $"dist={(spirePos - camPos).magnitude:F0}");
            string path = Shot(outDir, "s2_dusk", camPos, yaw, pitch, 36f, look, terrain, null);
            UnityEngine.Object.DestroyImmediate(props);
            body = null;
            return path;
        }

        // ------------------------------------------------------------- S1 ---
        private static string RenderS1(WorldData data, WorldTerrain terrain, string outDir, Vector3 site, Vector3 viewDir)
        {
            // Well One needs flat ground, not a knoll: a plaza on a slope reads as
            // a tilted disc. Pick the broadest flat area and let the land fall away
            // behind it.
            site = FindFlatSite(data);
            Vector3 bestDir = Vector3.forward;
            float lowest = float.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                float far = data.HeightAt(site.x + dir.x * 150f, site.z + dir.z * 150f);
                if (far < lowest)
                {
                    lowest = far;
                    bestDir = dir;
                }
            }
            viewDir = bestDir;
            Vector3 right = new Vector3(viewDir.z, 0f, -viewDir.x);
            // Approach shot: the plaza rim sits on the skyline, the wellspire rises
            // behind it. Eye level, so the tower reads as tall rather than wide.
            // Arrival: the plaza bowl in the foreground, the spire rising behind it.
            // The camera sits 8 m up so the ring wall does not hide the plaza floor.
            var camPos = site - viewDir * 50f;
            camPos.y = data.HeightAt(camPos.x, camPos.z) + 8f;
            Vector3 towerAim = site + Vector3.up * 6f;
            float yaw = Yaw(site - camPos);
            float pitch = PitchTo(camPos, towerAim);

            var props = new GameObject("S1Props");
            var brick = AshkarProps.Material("SaltBrick", new Color(0.52f, 0.50f, 0.45f), 0.18f);
            var wood = AshkarProps.Material("WeatheredWood", new Color(0.20f, 0.16f, 0.12f), 0.12f);
            var bell = AshkarProps.Material("BellBronze", new Color(0.35f, 0.28f, 0.16f), 0.55f, 0.7f);
            var body = AshkarProps.Material("BasaltBody", new Color(0.14f, 0.12f, 0.10f), 0.2f);
            var trim = AshkarProps.Material("Brass", new Color(0.42f, 0.31f, 0.16f), 0.45f, 0.6f);
            var robe = AshkarProps.Material("Robes", new Color(0.10f, 0.08f, 0.07f), 0.1f);

            var plaza = AshkarProps.WellOne(Ground(data, site), 24f, brick, wood, bell);
            plaza.transform.SetParent(props.transform, true);

            var spirePos = Ground(data, site + viewDir * 120f);
            var spire = AshkarProps.Wellspire(spirePos, 95f, body, trim);
            spire.transform.SetParent(props.transform, true);

            var fairway = (site + viewDir * 16f - camPos);
            float figYaw = Yaw(fairway);
            var f1 = AshkarProps.Figure(Ground(data, site + viewDir * 14f + right * 6f), figYaw, 1.8f, robe);
            f1.transform.SetParent(props.transform, true);
            var f2 = AshkarProps.Figure(Ground(data, site + viewDir * 18f + right * 9f), figYaw + 20f, 1.75f, robe);
            f2.transform.SetParent(props.transform, true);

            var look = new Look
            {
                zenith = FromHex("9AA2A8"),
                horizon = FromHex("EFE0C8"),
                ground = FromHex("8A7B64"),
                sunColor = FromHex("FFF6E2"),
                sun2Color = FromHex("E8B98C"),
                fog = FromHex("E9DDC8"),
                sunYawOffset = 148f,
                sunElevation = 18f,
                sun2Elevation = 34f,
                sunIntensity = 2.1f,
                sun2Intensity = 0.22f,
                fogStart = 260f,
                fogEnd = 2600f,
                exposure = 0.05f,
                stars = 0f,
                bloom = 0.30f,
            };

            Debug.Log($"[AI003][ASHKAR] S1 cam={camPos} yaw={yaw:F1} pitch={pitch:F1} site={site} " +
                      $"plaza={plaza.transform.position} spire={spirePos} dist={(site - camPos).magnitude:F0}");
            // Dawn: low contrast, cool shadows, thin ground haze.
            string path = Shot(outDir, "s1_dawn", camPos, yaw, pitch, 56f, look, terrain, null);
            UnityEngine.Object.DestroyImmediate(props);
            return path;
        }

        // ------------------------------------------------------------- S4 ---
        private static string RenderS4(WorldData data, WorldTerrain terrain, string outDir)
        {
            Vector3 spot = FindSaltFlat(data);
            var props = new GameObject("S4Props");

            var crystalShader = Shader.Find("AI003/AshkarCrystal");
            var crystal = new Material(crystalShader) { name = "Coldfire" };
            crystal.SetColor("_BaseColor", new Color(0.10f, 0.30f, 0.28f));
            crystal.SetColor("_EmissionColor", new Color(0.30f, 0.90f, 0.82f));
            crystal.SetFloat("_EmissionStrength", 0.85f);
            crystal.SetColor("_FresnelColor", new Color(0.55f, 0.95f, 0.90f));
            crystal.SetFloat("_FresnelPower", 2.6f);
            var robe = AshkarProps.Material("Robes", new Color(0.04f, 0.05f, 0.06f), 0.1f);

            var cluster = AshkarProps.CrystalCluster(Ground(data, spot), 9, 3.0f, crystal, 7);
            cluster.transform.SetParent(props.transform, true);

            // Fake mirror: the same cluster flipped about the salt plane.
            var mirror = AshkarProps.CrystalCluster(Ground(data, spot), 9, 3.0f, crystal, 7);
            mirror.transform.SetParent(props.transform, true);
            mirror.transform.localScale = new Vector3(1f, -1f, 1f);

            var fig = AshkarProps.Figure(Ground(data, spot + new Vector3(9f, 0f, 4f)), 200f, 1.8f, robe);
            fig.transform.SetParent(props.transform, true);

            // Look from the land side out over the salt sea, so the sky is behind
            // the crystals instead of a dune.
            Vector3 seaward = Vector3.forward;
            float bestDrop = float.NegativeInfinity;
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                float drop = spot.y - data.HeightAt(spot.x + dir.x * 120f, spot.z + dir.z * 120f);
                if (drop > bestDrop)
                {
                    bestDrop = drop;
                    seaward = dir;
                }
            }

            var camPos = spot - seaward * 15f + Vector3.up * 2.4f;
            float yaw = Yaw(seaward);
            float pitch = PitchTo(camPos, spot + Vector3.up * 2.0f);

            var look = new Look
            {
                zenith = FromHex("05070E"),
                horizon = FromHex("1B2734"),
                ground = FromHex("0A0F15"),
                sunColor = FromHex("CFE0EC"),
                sun2Color = FromHex("9FB8C8"),
                fog = FromHex("0E1720"),
                sunElevation = 15f,
                sun2Elevation = 27f,
                sunIntensity = 0.42f,
                sun2Intensity = 0.08f,
                fogStart = 220f,
                fogEnd = 2400f,
                exposure = 0.15f,
                stars = 1f,
                bloom = 1.1f,
            };

            Debug.Log($"[AI003][ASHKAR] S4 cam={camPos} yaw={yaw:F1} pitch={pitch:F1} spot={spot} " +
                      $"h={(data.HeightAt(spot.x, spot.z)):F1} dist={(spot - camPos).magnitude:F0}");
            string path = Shot(outDir, "s4_night", camPos, yaw, pitch, 38f, look, terrain, null);
            UnityEngine.Object.DestroyImmediate(props);
            return path;
        }

        // ----------------------------------------------------------- shot ---
        private static string Shot(string dir, string name, Vector3 camPos, float yaw, float pitch, float fov,
            Look look, WorldTerrain terrain, GameObject props)
        {
            var spawned = new System.Collections.Generic.List<GameObject>();

            Vector3 sunDir = Direction(yaw + look.sunYawOffset, look.sunElevation);
            Vector3 sun2Dir = Direction(yaw + look.sunYawOffset + 128f, look.sun2Elevation);

            var sunObject = new GameObject("Sun");
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = look.sunColor;
            sun.intensity = look.sunIntensity;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.9f;
            sunObject.transform.rotation = Quaternion.LookRotation(-sunDir, Vector3.up);
            spawned.Add(sunObject);

            var sun2Object = new GameObject("Sun2");
            var sun2 = sun2Object.AddComponent<Light>();
            sun2.type = LightType.Directional;
            sun2.color = look.sun2Color;
            sun2.intensity = look.sun2Intensity;
            sun2.shadows = LightShadows.None;
            sun2Object.transform.rotation = Quaternion.LookRotation(-sun2Dir, Vector3.up);
            spawned.Add(sun2Object);

            bool night = look.stars > 0.5f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            if (night)
            {
                RenderSettings.ambientSkyColor = FromHex("8FB0C8") * 0.22f;
                RenderSettings.ambientEquatorColor = FromHex("4A6070") * 0.18f;
                RenderSettings.ambientGroundColor = FromHex("10161C") * 0.25f;
            }
            else
            {
                RenderSettings.ambientSkyColor = FromHex("8FA3B2") * 0.50f;
                RenderSettings.ambientEquatorColor = FromHex("C0A98C") * 0.32f;
                RenderSettings.ambientGroundColor = FromHex("3A2E26") * 0.40f;
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = look.fog;
            RenderSettings.fogStartDistance = look.fogStart;
            RenderSettings.fogEndDistance = look.fogEnd;

            if (terrain != null)
            {
                terrain.TerrainMaterial.SetColor("_HeightFogColor", look.fog.linear);
                terrain.TerrainMaterial.SetFloat("_HeightFogStrength", night ? 0.25f : 0.55f);
                terrain.TerrainMaterial.SetFloat("_HeightFogDistance", look.fogEnd * 0.55f);
            }

            var skyMat = new Material(Shader.Find("AI003/AshkarSky")) { name = "AshkarSky" };
            skyMat.SetColor("_Zenith", look.zenith.linear);
            skyMat.SetColor("_Horizon", look.horizon.linear);
            skyMat.SetColor("_Ground", look.ground.linear);
            skyMat.SetVector("_SunDir", sunDir);
            skyMat.SetColor("_SunColor", look.sunColor.linear);
            skyMat.SetFloat("_SunSize", Mathf.Cos(1.15f * Mathf.Deg2Rad));
            skyMat.SetVector("_Sun2Dir", sun2Dir);
            skyMat.SetColor("_Sun2Color", look.sun2Color.linear);
            skyMat.SetFloat("_Sun2Size", Mathf.Cos(0.75f * Mathf.Deg2Rad));
            skyMat.SetFloat("_Haze", night ? 0.25f : 0.45f);
            skyMat.SetFloat("_DustBand", night ? 0.05f : 0.25f);
            skyMat.SetFloat("_Stars", look.stars);
            skyMat.SetColor("_FogColor", look.fog.linear);
            skyMat.SetFloat("_HorizonBand", night ? 0.35f : 0.55f);
            RenderSettings.skybox = skyMat;

            var volumeObject = new GameObject("Volume");
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.profile = BuildProfile(look.exposure, look.bloom, night);
            spawned.Add(volumeObject);

            var cameraObject = new GameObject("Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = fov;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 9000f;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.allowHDR = true;
            camera.transform.position = camPos;
            camera.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
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

        private static VolumeProfile BuildProfile(float exposure, float bloomIntensity, bool night)
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.overrideState = true;
            tone.mode.value = TonemappingMode.ACES;

            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.overrideState = true;
            color.postExposure.value = exposure;
            color.contrast.overrideState = true;
            color.contrast.value = 14f;
            color.saturation.overrideState = true;
            color.saturation.value = night ? -6f : -14f;
            color.colorFilter.overrideState = true;
            color.colorFilter.value = new Color(1f, 0.97f, 0.92f);

            var balance = profile.Add<WhiteBalance>(true);
            balance.temperature.overrideState = true;
            balance.temperature.value = night ? -18f : 8f;

            var split = profile.Add<SplitToning>(true);
            split.shadows.overrideState = true;
            split.shadows.value = new Color(0.32f, 0.40f, 0.48f);
            split.highlights.overrideState = true;
            split.highlights.value = new Color(1f, 0.87f, 0.70f);
            split.balance.overrideState = true;
            split.balance.value = -5f;

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.overrideState = true;
            bloom.threshold.value = night ? 0.85f : 1.60f;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = bloomIntensity;
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

        // ---------------------------------------------------------- helpers ---
        private static Vector3 Direction(float yawDeg, float elevationDeg)
        {
            float yaw = yawDeg * Mathf.Deg2Rad;
            float el = elevationDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(yaw) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(yaw) * Mathf.Cos(el));
        }

        private static float Yaw(Vector3 fromTo)
        {
            return Mathf.Atan2(fromTo.x, fromTo.z) * Mathf.Rad2Deg;
        }

        /// <summary>Downward pitch (degrees) that points the camera at a target.</summary>
        private static float PitchTo(Vector3 camera, Vector3 target)
        {
            Vector3 flat = new Vector3(target.x - camera.x, 0f, target.z - camera.z);
            return Mathf.Atan2(camera.y - target.y, flat.magnitude) * Mathf.Rad2Deg;
        }

        private static Vector3 Ground(WorldData data, Vector3 at)
        {
            at.y = data.HeightAt(at.x, at.z);
            return at;
        }

        /// <summary>Highest-prominence knoll whose view drops away: a place to stand on.</summary>
        private static Vector3 FindViewpoint(WorldData data, out Vector3 lookDir)
        {
            float half = data.SizeMeters * 0.5f;
            float best = float.NegativeInfinity;
            Vector3 bestPoint = Vector3.zero;
            Vector3 bestDir = Vector3.forward;

            for (float z = -half + 200f; z < half - 200f; z += 12f)
            {
                for (float x = -half + 200f; x < half - 200f; x += 12f)
                {
                    float h = data.HeightAt(x, z);
                    if (h < 40f || h > 130f)
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

                    float lowest = float.MaxValue;
                    for (int k = 0; k < 8; k++)
                    {
                        float ak = k * Mathf.PI / 4f;
                        lowest = Mathf.Min(lowest, data.HeightAt(x + Mathf.Cos(ak) * 55f, z + Mathf.Sin(ak) * 55f));
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

                        if (data.HeightAt(x + dx * 420f, z + dz * 420f) < 8f ||
                            data.HeightAt(x + dx * 900f, z + dz * 900f) < 4f)
                        {
                            continue;
                        }

                        float drop = h - far;
                        if (drop < 4f)
                        {
                            continue;
                        }
                        float score = drop + prominence * 1.3f;
                        if (score > best)
                        {
                            best = score;
                            bestPoint = new Vector3(x, h, z);
                            bestDir = new Vector3(dx, 0f, dz);
                        }
                    }
                }
            }

            lookDir = bestDir;
            return bestPoint;
        }

        /// <summary>Broadest flat shelf in the mid desert: a plaza needs level ground.</summary>
        private static Vector3 FindFlatSite(WorldData data)
        {
            float half = data.SizeMeters * 0.5f;
            Vector3 best = new Vector3(0f, data.HeightAt(0f, 0f), 0f);
            float bestDeviation = float.MaxValue;

            for (float z = -half + 250f; z < half - 250f; z += 20f)
            {
                for (float x = -half + 250f; x < half - 250f; x += 20f)
                {
                    float h = data.HeightAt(x, z);
                    if (h < 30f || h > 105f)
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

                    float deviation = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        float a = k * Mathf.PI / 4f;
                        deviation += Mathf.Abs(data.HeightAt(x + Mathf.Cos(a) * 70f, z + Mathf.Sin(a) * 70f) - h);
                    }

                    if (deviation < bestDeviation)
                    {
                        bestDeviation = deviation;
                        best = new Vector3(x, h, z);
                    }
                }
            }

            return best;
        }

        /// <summary>A flat spot just above the salt sea, for the night frame.</summary>
        private static Vector3 FindSaltFlat(WorldData data)
        {
            float half = data.SizeMeters * 0.5f;
            Vector3 best = Vector3.zero;
            float bestScore = float.NegativeInfinity;
            Vector3 lowest = Vector3.zero;
            float lowestH = float.MaxValue;

            for (float z = -half + 250f; z < half - 250f; z += 8f)
            {
                for (float x = -half + 250f; x < half - 250f; x += 8f)
                {
                    float h = data.HeightAt(x, z);
                    if (h < lowestH)
                    {
                        lowestH = h;
                        lowest = new Vector3(x, h, z);
                    }
                    if (h < -1f || h > 8f)
                    {
                        continue;
                    }
                    float flat = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float a = k * Mathf.PI * 0.5f;
                        flat += Mathf.Abs(data.HeightAt(x + Mathf.Cos(a) * 22f, z + Mathf.Sin(a) * 22f) - h);
                    }
                    // Prefer salt right at the shoreline, and reasonably level.
                    float score = -flat * 1.2f - Mathf.Abs(h - 2f) * 0.8f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Vector3(x, h, z);
                    }
                }
            }

            return best == Vector3.zero ? lowest : best;
        }

        private static Color FromHex(string hex)
        {
            return ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.magenta;
        }
    }
}
