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

        /// <summary>
        /// One pinned still per terrain. The numbers are the coordinates from
        /// artifacts/worldview-sites (site + 3.5 m, yaw, pitch, fov, subject distance),
        /// baked in so a re-run reproduces the frames without any environment variable.
        /// </summary>
        private struct Frame
        {
            public string name;
            public float x;
            public float z;
            public float yaw;
            public float pitch;
            public float fov;
            public float distance;
            public float exposure;
            /// <summary>Sun azimuth in degrees; NaN keeps the shared look's direction.</summary>
            public float sunAzimuth;
        }

        private static readonly Frame[] Frames =
        {
            new Frame { name = "desert", x = -592f, z = 248f, yaw = 76f, pitch = 4f, fov = 40f, distance = 130f, exposure = -0.35f, sunAzimuth = float.NaN },
            new Frame { name = "ocean", x = -556f, z = 576f, yaw = 225f, pitch = 6f, fov = 64f, distance = 25f, exposure = -0.10f, sunAzimuth = float.NaN },
            // 验证's forest point: 82 m from the stand core (−292,666), 20 conifers in
            // front and 43% canopy coverage; the old (−116,728)/yaw236 looked away.
            // 覆盖率实测（验证 的 UNLIT 差分，几何投影，与颜色无关）：
            //   fov50 / 82 m, pitch 3  -> 17.0%
            //   fov32 / 82 m, pitch 3  -> 24.61%（可分冠 60）
            //   site 前移 35 m + fov28, pitch 4 -> 22.27%（可分冠 40，近树冠被上沿切掉）
            // 结论：收 FOV / 前移都让近树冠出画，边际是负的。改成退回 82 m、
            // 抬高 pitch 把近树冠接进画面上沿（fov 保持 32，仍比 50 紧一点）。
            new Frame { name = "forest", x = -237f, z = 727f, yaw = 222f, pitch = 10f, fov = 32f, distance = 82f, exposure = 0.05f, sunAzimuth = float.NaN },
            // 草原：原 (423,-203)/yaw339 落在湿森林口袋里（地面是森林绿、近景全是针叶树）。
            // (−592,−368) 是这片世界最大的连片干草地之一（m≈0.58，前方 400 m 九成是草地，
            // 500 m 视锥内针叶树 18 株，全是远景），高度 134 m 能俯看草原。
            new Frame { name = "steppe", x = -592f, z = -368f, yaw = 315f, pitch = 6f, fov = 60f, distance = 120f, exposure = -0.05f, sunAzimuth = float.NaN },
            // Snow: the mean snow normal faces ~255°, so the shared sun leaves it in
            // shadow; 254° lights 99% of the snow pixels (verified by 验证).
            new Frame { name = "snow", x = 109f, z = 512f, yaw = 30f, pitch = -10f, fov = 50f, distance = 103f, exposure = -0.25f, sunAzimuth = 254f },
            new Frame { name = "mountain", x = 115f, z = -409f, yaw = 355f, pitch = -4f, fov = 46f, distance = 900f, exposure = -0.10f, sunAzimuth = float.NaN },
        };

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
            // Experiments must never clobber the accepted stills: AI003_STILL_OUTDIR
            // redirects the whole frame set to a run directory.
            string outDir = Environment.GetEnvironmentVariable("AI003_STILL_OUTDIR");
            if (string.IsNullOrEmpty(outDir))
            {
                outDir = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "..", "..", "..", "artifacts", "ashkar"));
            }
            outDir = Path.GetFullPath(outDir);
            Debug.Log($"[AI003][ASHKAR] outDir={outDir}");
            Directory.CreateDirectory(outDir);

            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));
            var theme = WorldTheme.CreateAshkar();
            var colors = theme.BuildColorGrid(data);

            var root = new GameObject("[AI003][ASHKAR] World");
            var paths = new System.Collections.Generic.List<string>();

            // Everything this tool touches outside the throwaway root is snapshotted
            // here and put back in the finally: a still render must never leak into
            // OpenWorld.unity, the URP asset or the shared fog material.
            RenderSettingsState sceneState = CaptureRenderSettings();

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            float prevShadowDistance = urp != null ? urp.shadowDistance : 0f;
            int prevCascades = urp != null ? urp.shadowCascadeCount : 0;

            try
            {
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
                WireSurfaceTextures(terrain.TerrainMaterial);

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
                    var waterRenderer = terrain.Water.GetComponent<MeshRenderer>();
                    Debug.Log($"[AI003][ASHKAR] water y={terrain.Water.transform.position.y:F1} " +
                              $"scale={terrain.Water.transform.localScale} " +
                              $"mat={(waterRenderer != null && waterRenderer.sharedMaterial != null ? waterRenderer.sharedMaterial.name : "null")}");
                }
                else
                {
                    Debug.LogWarning("[AI003][ASHKAR] terrain has no water object");
                }
                WireWaterTextures();

                // The still camera world is built from scratch, so the baked vegetation
                // has to be attached here too; its meshes are already in world space.
                SpawnBakedVegetation(root.transform);

                if (urp != null)
                {
                    // Stills-only lighting: the 34° sun over 420 m with one cascade
                    // dragged long black wedges across every frame. Restored below.
                    urp.shadowDistance = 120f;
                    urp.shadowCascadeCount = 2;
                }

                Vector3 site;
                Vector3 viewDir;
                bool pinned = ResolveRig(data, out site, out viewDir);
                // "pinned" only when every vantage input came from the environment:
                // a half-pinned frame is still auto and must not be compared.
                string rigSource = pinned ? "pinned" : "auto";

                string only = Environment.GetEnvironmentVariable("AI003_STILL_ONLY");
                bool Wanted(string shotName)
                {
                    return string.IsNullOrEmpty(only) ||
                           ("," + only.ToUpperInvariant() + ",").Contains("," + shotName.ToUpperInvariant() + ",");
                }

                // S1/S2/S4/S5 are the retired historical baselines: the code stays, but
                // they are no longer part of the image set (AI003_STILL_LEGACY=1 revives).
                bool legacy = Environment.GetEnvironmentVariable("AI003_STILL_LEGACY") == "1";
                if (legacy && Wanted("S2")) Run("S2", () => paths.Add(RenderS2(data, terrain, outDir, site, viewDir, rigSource)));
                if (legacy && Wanted("S1")) Run("S1", () => paths.Add(RenderS1(data, terrain, outDir, site, viewDir)));
                if (legacy && Wanted("S4")) Run("S4", () => paths.Add(RenderS4(data, terrain, outDir)));
                if (legacy && Wanted("S5")) Run("S5", () => paths.Add(RenderS5(data, terrain, outDir, site, viewDir, rigSource)));

                // (bisect ①: Shader.WarmupAllShaders() removed — it mutates global
                // shader state and is a magenta suspect.)

                // Six pinned terrain frames: the acceptance set for the biome work.
                foreach (Frame frame in Frames)
                {
                    Frame f = frame;
                    if (!Wanted(f.name))
                    {
                        continue;
                    }
                    Run(f.name, () => paths.Add(RenderFrame(data, terrain, outDir, f)));
                }
            }
            finally
            {
                if (urp != null)
                {
                    urp.shadowDistance = prevShadowDistance;
                    urp.shadowCascadeCount = prevCascades;
                }
                RestoreRenderSettings(sceneState);
                UnityEngine.Object.DestroyImmediate(root);
            }

            return paths.ToArray();
        }

        /// <summary>Site and direction for the world shots.</summary>
        /// <remarks>
        /// Auto-picked by default. Pin it for comparable before/after frames:
        ///   AI003_STILL_SITE="x,z"   AI003_STILL_YAW="degrees"
        ///   AI003_STILL_FLAT="x,z"   (S1 plaza)    AI003_STILL_SALT="x,z"  (S4 salt flat)
        /// Environment variables so the menu item and -executeMethod both work.
        /// </remarks>
        private static bool ResolveRig(WorldData data, out Vector3 site, out Vector3 viewDir)
        {
            bool pinnedSite = TryParsePoint(data, "AI003_STILL_SITE", out site);
            bool pinnedYaw = TryParseYaw(out float yawDeg);

            if (!pinnedSite || !pinnedYaw)
            {
                Vector3 autoSite = FindViewpoint(data, out Vector3 autoDir);
                if (!pinnedSite)
                {
                    site = autoSite;
                }

                if (!pinnedYaw)
                {
                    viewDir = autoDir;
                    return pinnedSite;
                }
            }

            float rad = yawDeg * Mathf.Deg2Rad;
            viewDir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            return true;
        }

        /// <summary>Reads "x,z" from an environment variable; false when unset or malformed.</summary>
        private static bool TryParsePoint(WorldData data, string variable, out Vector3 point)
        {
            point = Vector3.zero;
            string raw = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            string[] parts = raw.Split(',');
            if (parts.Length < 2)
            {
                Debug.LogWarning($"[AI003][ASHKAR] {variable}='{raw}' is not 'x,z' — falling back to auto");
                return false;
            }

            bool parsedX = float.TryParse(parts[0].Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x);
            bool parsedZ = float.TryParse(parts[1].Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z);
            if (!parsedX || !parsedZ)
            {
                Debug.LogWarning($"[AI003][ASHKAR] {variable}='{raw}' is not 'x,z' — falling back to auto");
                return false;
            }

            point = new Vector3(x, data.HeightAt(x, z), z);
            return true;
        }

        /// <summary>Reads a yaw in degrees from AI003_STILL_YAW.</summary>
        private static bool TryParseYaw(out float yawDeg)
        {
            yawDeg = 0f;
            string raw = Environment.GetEnvironmentVariable("AI003_STILL_YAW");
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            if (!float.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out yawDeg))
            {
                Debug.LogWarning($"[AI003][ASHKAR] AI003_STILL_YAW='{raw}' is not a number — falling back to auto");
                return false;
            }

            return true;
        }

        /// <summary>Scene-wide render state that the still shots mutate.</summary>
        private struct RenderSettingsState
        {
            public Material skybox;
            public bool fog;
            public FogMode fogMode;
            public Color fogColor;
            public float fogStart;
            public float fogEnd;
            public AmbientMode ambientMode;
            public Color ambientSky;
            public Color ambientEquator;
            public Color ambientGround;
            public float reflectionIntensity;
            public Material fogMaterial;
            public bool fogMaterialWasDirty;
            public bool fogMaterialValid;
            public Color fogMaterialTint;
            public float fogMaterialDensity;
            public float fogMaterialFalloff;
            public float fogMaterialStart;
            public float fogMaterialBaseHeight;
        }

        private static RenderSettingsState CaptureRenderSettings()
        {
            var state = new RenderSettingsState
            {
                skybox = RenderSettings.skybox,
                fog = RenderSettings.fog,
                fogMode = RenderSettings.fogMode,
                fogColor = RenderSettings.fogColor,
                fogStart = RenderSettings.fogStartDistance,
                fogEnd = RenderSettings.fogEndDistance,
                ambientMode = RenderSettings.ambientMode,
                ambientSky = RenderSettings.ambientSkyColor,
                ambientEquator = RenderSettings.ambientEquatorColor,
                ambientGround = RenderSettings.ambientGroundColor,
                reflectionIntensity = RenderSettings.reflectionIntensity,
                fogMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/HeightFog.mat"),
            };

            state.fogMaterialWasDirty = state.fogMaterial != null && EditorUtility.IsDirty(state.fogMaterial);
            // Do not gate this on Material.HasProperty: it reports false in some editor
            // contexts even for properties the shader clearly has, which silently
            // skipped the restore. SetColor/SetFloat are no-ops on a missing property.
            state.fogMaterialValid = state.fogMaterial != null;
            if (state.fogMaterialValid)
            {
                state.fogMaterialTint = state.fogMaterial.GetColor("_FogColor");
                state.fogMaterialDensity = state.fogMaterial.GetFloat("_Density");
                state.fogMaterialFalloff = state.fogMaterial.GetFloat("_Falloff");
                state.fogMaterialStart = state.fogMaterial.GetFloat("_StartDistance");
                state.fogMaterialBaseHeight = state.fogMaterial.GetFloat("_BaseHeight");
            }

            return state;
        }

        private static void RestoreRenderSettings(RenderSettingsState state)
        {
            // Skybox first: the shots swap in a temp material they destroy.
            RenderSettings.skybox = state.skybox;
            RenderSettings.fog = state.fog;
            RenderSettings.fogMode = state.fogMode;
            RenderSettings.fogColor = state.fogColor;
            RenderSettings.fogStartDistance = state.fogStart;
            RenderSettings.fogEndDistance = state.fogEnd;
            RenderSettings.ambientMode = state.ambientMode;
            RenderSettings.ambientSkyColor = state.ambientSky;
            RenderSettings.ambientEquatorColor = state.ambientEquator;
            RenderSettings.ambientGroundColor = state.ambientGround;
            RenderSettings.reflectionIntensity = state.reflectionIntensity;

            float liveDensity = state.fogMaterial == null ? -1f : state.fogMaterial.GetFloat("_Density");
            Debug.Log($"[AI003][ASHKAR] restore fog: valid={state.fogMaterialValid} wasDirty={state.fogMaterialWasDirty} " +
                      $"savedDensity={state.fogMaterialDensity} liveDensity={liveDensity}");
            if (state.fogMaterialValid)
            {
                state.fogMaterial.SetColor("_FogColor", state.fogMaterialTint);
                state.fogMaterial.SetFloat("_Density", state.fogMaterialDensity);
                state.fogMaterial.SetFloat("_Falloff", state.fogMaterialFalloff);
                state.fogMaterial.SetFloat("_StartDistance", state.fogMaterialStart);
                state.fogMaterial.SetFloat("_BaseHeight", state.fogMaterialBaseHeight);
                if (!state.fogMaterialWasDirty)
                {
                    EditorUtility.ClearDirty(state.fogMaterial);
                }
            }
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
        private static string RenderS2(WorldData data, WorldTerrain terrain, string outDir, Vector3 site,
            Vector3 viewDir, string source)
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

            string path = Shot(outDir, "s2_dusk", camPos, yaw, pitch, 36f, look, terrain, null, source);
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
            Vector3 pinnedFlat;
            bool flatPinned = TryParsePoint(data, "AI003_STILL_FLAT", out pinnedFlat);
            site = flatPinned ? pinnedFlat : FindFlatSite(data);
            string source = flatPinned ? "pinned" : "auto";
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
            var brick = AshkarProps.Material("SaltBrick", new Color(0.40f, 0.38f, 0.34f), 0.18f);
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

            // Dawn: low contrast, cool shadows, thin ground haze.
            string path = Shot(outDir, "s1_dawn", camPos, yaw, pitch, 56f, look, terrain, null, source);
            UnityEngine.Object.DestroyImmediate(props);
            return path;
        }

        // ------------------------------------------------------------- S4 ---
        private static string RenderS4(WorldData data, WorldTerrain terrain, string outDir)
        {
            Vector3 pinnedSalt;
            bool saltPinned = TryParsePoint(data, "AI003_STILL_SALT", out pinnedSalt);
            Vector3 spot = saltPinned ? pinnedSalt : FindSaltFlat(data);
            string source = saltPinned ? "pinned" : "auto";
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

            string path = Shot(outDir, "s4_night", camPos, yaw, pitch, 38f, look, terrain, null, source);
            UnityEngine.Object.DestroyImmediate(props);
            return path;
        }

        // ------------------------------------------------------------- S5 ---
        private static string RenderS5(WorldData data, WorldTerrain terrain, string outDir, Vector3 site,
            Vector3 viewDir, string source)
        {
            // Feed the real client path (WorldEvents) a synthetic gateway payload:
            // the same code that renders the live tide renders this frame.
            var payload = new Newtonsoft.Json.Linq.JObject
            {
                ["tide"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["active"] = true,
                    ["x"] = site.x + 520f,
                    ["height"] = 360f,
                    ["speed"] = 12f,
                    ["eta"] = 24f,
                },
                ["elder"] = new Newtonsoft.Json.Linq.JObject { ["active"] = false },
                ["nodes"] = new Newtonsoft.Json.Linq.JArray(),
            };

            var events = WorldEvents.Ensure();
            events.Apply(payload);
            events.SnapToServer();

            // Look down the known-open fall line and hang the wall across it.
            var camPos = new Vector3(site.x, 0f, site.z);
            camPos.y = data.HeightAt(camPos.x, camPos.z) + 6f;
            float yaw = Yaw(viewDir);
            float pitch = -1.5f;

            var wall = events.transform.Find("TideWall");
            if (wall != null)
            {
                Vector3 wallPos = camPos + viewDir * 620f;
                wallPos.y = data.HeightAt(wallPos.x, wallPos.z);
                wall.position = wallPos;
                wall.rotation = Quaternion.LookRotation(-viewDir, Vector3.up);
            }

            var look = new Look
            {
                zenith = FromHex("3A2E28"),
                horizon = FromHex("C98F5C"),
                ground = FromHex("4A3527"),
                sunColor = FromHex("FFD9A8"),
                sun2Color = Sun2Color,
                fog = FromHex("B07A45"),
                sunElevation = 12f,
                sun2Elevation = 28f,
                sunIntensity = 1.6f,
                sun2Intensity = 0.20f,
                fogStart = 220f,
                fogEnd = 2600f,
                exposure = -0.25f,
                stars = 0f,
                bloom = 0.45f,
            };

            string path = Shot(outDir, "s5_tide", camPos, yaw, pitch, 52f, look, terrain, null, source);
            UnityEngine.Object.DestroyImmediate(events.gameObject);
            return path;
        }

        // ------------------------------------------------------- biome frames ---
        private static readonly System.Collections.Generic.List<MeshRenderer> VegetationRenderers =
            new System.Collections.Generic.List<MeshRenderer>();
        private static bool _vegetationDiagnosed;

        /// <summary>
        /// Adds the meshes baked by AI003/Author Vegetation to the throwaway still world.
        /// They are already in world space, so the objects can sit at the origin.
        /// </summary>
        private static void SpawnBakedVegetation(Transform parent)
        {
            VegetationRenderers.Clear();
            _vegetationDiagnosed = false;

            string[] species = { "conifer", "dead_tree", "acacia", "acacia_small", "snow_rock", "snow_rock_b", "grass_tuft_b", "bush_a" };
            int added = 0;
            foreach (string slug in species)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"Assets/Generated/Vegetation/{slug}.asset");
                if (mesh == null)
                {
                    continue;
                }
                var go = new GameObject("veg_" + slug);
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                // Trees and rocks have two material slots (trunk + canopy, rock + snow cap).
                int slots = Mathf.Max(1, mesh.subMeshCount);
                var materials = new Material[slots];
                for (int s = 0; s < slots; s++)
                {
                    string file = s == 0 ? slug : $"{slug}_{s}";
                    var material = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Settings/Vegetation/{file}.mat");
                    if (Environment.GetEnvironmentVariable("AI003_STILL_UNLIT") == "1")
                    {
                        // Diagnostic: a flat URP unlit colour separates "material/shader
                        // variant missing" from "geometry or hierarchy wrong". The built-in
                        // Unlit/Color shader has no URP pass and renders nothing at all.
                        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
                        material = unlit != null
                            ? new Material(unlit) { name = "veg_unlit" }
                            : material;
                        if (material != null && unlit != null)
                        {
                            material.SetColor("_BaseColor", Color.HSVToRGB((added * 0.17f) % 1f, 1f, 1f));
                        }
                    }
                    else if (Environment.GetEnvironmentVariable("AI003_STILL_CULLOFF") == "1" && material != null)
                    {
                        // Diagnostic: reversed winding would hide every front face from the
                        // colour pass while the shadow pass still draws.
                        material = new Material(material);
                        material.SetFloat("_Cull", 0f);
                    }

                    // bisect ③: strip the normal-map keyword when probing the variant
                    // trigger; the copy keeps the .mat asset untouched.
                    if (material != null && Environment.GetEnvironmentVariable("AI003_STILL_VEG_NONORMAL") == "1")
                    {
                        material = new Material(material);
                        material.DisableKeyword("_NORMALMAP");
                        material.SetTexture("_BumpMap", null);
                    }

                    materials[s] = material;
                }
                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                VegetationRenderers.Add(renderer);
                added++;
            }
            Debug.Log($"[AI003][ASHKAR] vegetation meshes attached: {added}");
        }

        /// <summary>One-shot diagnostic: why the trees do not show up in the forest frame.</summary>
        private static void DiagnoseVegetation(Camera camera)
        {
            if (_vegetationDiagnosed)
            {
                return;
            }
            _vegetationDiagnosed = true;
            Debug.Log($"[AI003][VEGDIAG] camera layerMask={camera.cullingMask} pos={camera.transform.position}");
            foreach (var renderer in VegetationRenderers)
            {
                var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                // Trees have two slots; the canopy is slot 1. Logging only
                // sharedMaterial (slot 0) made every run look like the canopy was
                // never attached, so print every slot's _BaseMap.
                var slotMaps = new System.Text.StringBuilder();
                var shared = renderer.sharedMaterials;
                for (int s = 0; s < shared.Length; s++)
                {
                    var tex = shared[s] != null ? shared[s].GetTexture("_BaseMap") : null;
                    if (s > 0) slotMaps.Append('|');
                    slotMaps.Append(tex != null ? tex.name : (shared[s] != null ? "null" : "noMat"));
                }
                Debug.Log($"[AI003][VEGDIAG] {renderer.name} isVisible={renderer.isVisible} " +
                          $"layer={renderer.gameObject.layer} enabled={renderer.enabled} " +
                          $"inCameraMask={((camera.cullingMask & (1 << renderer.gameObject.layer)) != 0)} " +
                          $"subMeshes={(mesh != null ? mesh.subMeshCount : -1)} slots={shared.Length} " +
                          $"baseMaps=[{slotMaps}] " +
                          $"meshVerts={(mesh != null ? mesh.vertexCount : -1)} " +
                          $"meshTris={(mesh != null ? mesh.triangles.Length : -1)} " +
                          $"boundsCenter={renderer.bounds.center} boundsSize={renderer.bounds.size}");
            }
        }

        /// <summary>
        /// Attaches the five scanned surface sets to the layered terrain shader.
        /// The material is created at runtime, so this is the one place the stills get
        /// real grain; tile sizes come from the delivery manifest (metres).
        /// </summary>
        private static void WireSurfaceTextures(Material m)
        {
            var layers = new (string set, string prop, float tile)[]
            {
                ("sand_fine", "SandFine", 0.6f),
                ("sand_mid_ripple", "SandMid", 2.5f),
                ("sand_coarse", "SandCoarse", 10.0f),
                ("salt_crust", "Salt", 2.0f),
                ("rock_cliff", "Rock", 4.0f),
            };
            foreach (var layer in layers)
            {
                var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    $"Assets/Settings/Textures/surface/{layer.set}/{layer.set}_albedo_1024.png");
                var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    $"Assets/Settings/Textures/surface/{layer.set}/{layer.set}_normal_1024.png");
                if (albedo != null)
                {
                    m.SetTexture($"_{layer.prop}Albedo", albedo);
                }
                if (normal != null)
                {
                    m.SetTexture($"_{layer.prop}Normal", normal);
                }
                m.SetFloat($"_{layer.prop}Tile", layer.tile);
                Debug.Log($"[AI003][ASHKAR] surface {layer.set} tile={layer.tile}m " +
                          $"albedo={(albedo != null ? albedo.name : "NULL")} " +
                          $"normal={(normal != null ? normal.name : "NULL")}");
            }
            m.SetFloat("_SurfaceStrength", 0.55f);
            m.SetFloat("_SurfaceNormalStrength", 0.35f);
            m.SetFloat("_SaltHeight", 3.0f);

            // Snow cap: the new wind-drift set replaces the old vegetation snow texture.
            var snowAlbedo = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Settings/Textures/surface/snow_field/snow_field_albedo_1024.png");
            var snowNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Settings/Textures/surface/snow_field/snow_field_normal_1024.png");
            m.SetTexture("_SnowAlbedo", snowAlbedo);
            m.SetTexture("_SnowNormal", snowNormal);
            m.SetFloat("_SnowTile", 3.0f);
            m.SetFloat("_SnowLine", WorldTheme.SnowLineMeters);
            m.SetFloat("_SnowBlend", 22.0f);

            // Steppe grass: 2 m dry-grass set, weighted by green-dominant vertex colour.
            var grassAlbedo = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Settings/Textures/surface/steppe_grass/steppe_grass_albedo_1024.png");
            var grassNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Settings/Textures/surface/steppe_grass/steppe_grass_normal_1024.png");
            m.SetTexture("_GrassAlbedo", grassAlbedo);
            m.SetTexture("_GrassNormal", grassNormal);
            m.SetFloat("_GrassTile", 2.0f);
            m.SetFloat("_GrassStrength", 0.45f);
            Debug.Log($"[AI003][ASHKAR] grass set tile=2m " +
                      $"albedo={(grassAlbedo != null ? grassAlbedo.name : "NULL")} " +
                      $"normal={(grassNormal != null ? grassNormal.name : "NULL")}");
            Debug.Log($"[AI003][ASHKAR] snow cap set tile=3m line={WorldTheme.SnowLineMeters}m " +
                      $"albedo={(snowAlbedo != null ? snowAlbedo.name : "NULL")} " +
                      $"normal={(snowNormal != null ? snowNormal.name : "NULL")}");
        }

        /// <summary>Water: the delivered ocean set on the sea plane's URP/Lit material.</summary>
        private static void WireWaterTextures()
        {
            var water = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/AshkarOcean.mat");
            if (water == null)
            {
                Debug.LogWarning("[AI003][ASHKAR] AshkarOcean.mat missing");
                return;
            }
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Settings/Textures/surface/water_ocean/water_ocean_albedo_1024.png");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Settings/Textures/surface/water_ocean/water_ocean_normal_1024.png");
            if (albedo != null)
            {
                water.SetTexture("_BaseMap", albedo);
                water.SetTextureScale("_BaseMap", new Vector2(1f / 8f, 1f / 8f));
            }
            if (normal != null)
            {
                water.SetTexture("_BumpMap", normal);
                water.SetTextureScale("_BumpMap", new Vector2(1f / 8f, 1f / 8f));
                water.SetFloat("_BumpScale", 0.8f);
                water.EnableKeyword("_NORMALMAP");
            }
            water.SetFloat("_Smoothness", 0.90f);
            EditorUtility.SetDirty(water);
            Debug.Log($"[AI003][ASHKAR] water set tile=8m " +
                      $"albedo={(albedo != null ? albedo.name : "NULL")} " +
                      $"normal={(normal != null ? normal.name : "NULL")}");
        }

        private static string RenderFrame(WorldData data, WorldTerrain terrain, string outDir, Frame frame)
        {
            string env = $"AI003_STILL_{frame.name.ToUpperInvariant()}_";

            var site = new Vector3(frame.x, data.HeightAt(frame.x, frame.z), frame.z);
            string origin = "table";
            if (StillOverrides.TrySite(frame.name, out Vector2 pinnedSite))
            {
                site = new Vector3(pinnedSite.x, data.HeightAt(pinnedSite.x, pinnedSite.y), pinnedSite.y);
                origin = "env";
            }

            float yaw = frame.yaw;
            float pitch = frame.pitch;
            float fov = frame.fov;
            float distance = frame.distance;
            if (StillOverrides.TryValue(frame.name, "YAW", out float v)) { yaw = v; origin = "env"; }
            if (StillOverrides.TryValue(frame.name, "PITCH", out v)) { pitch = v; origin = "env"; }
            if (StillOverrides.TryValue(frame.name, "FOV", out v)) { fov = v; origin = "env"; }
            if (StillOverrides.TryValue(frame.name, "DIST", out v)) { distance = v; origin = "env"; }

            Vector3 camPos = site + Vector3.up * 3.5f;
            string note = $"origin={origin} site=({site.x:F0},{site.z:F0}) " +
                          $"h={data.HeightAt(site.x, site.z):F1} dist={distance:F0} ";

            var look = TerrainLook(frame.exposure);
            float sunAzimuth = frame.sunAzimuth;
            if (StillOverrides.TryValue(frame.name, "SUN_AZ", out float az))
            {
                sunAzimuth = az;
                origin = "env";
            }
            if (!float.IsNaN(sunAzimuth))
            {
                look.sunYawOffset = sunAzimuth - yaw;
                note += $"sunAz={sunAzimuth:F0} ";
            }

            return Shot(outDir, frame.name, camPos, yaw, pitch, fov, look, terrain, null, "pinned", note);
        }

        /// <summary>Daylight look shared by the six frames; exposure is the only per-frame knob.</summary>
        private static Look TerrainLook(float exposure)
        {
            return new Look
            {
                zenith = FromHex("5E6B78"),
                horizon = FromHex("E4BE93"),
                ground = FromHex("C9BFAE"),
                sunColor = FromHex("FFEFD6"),
                sun2Color = FromHex("E08A5C"),
                fog = Color.Lerp(FromHex("E4BE93"), FromHex("D8D2C6"), 0.55f),
                sunElevation = 34f,
                sun2Elevation = 30f,
                sunIntensity = 1.6f,
                sun2Intensity = 0.25f,
                fogStart = 700f,
                fogEnd = 7000f,
                exposure = exposure,
                stars = 0f,
                bloom = 0.30f,
            };
        }

        /// <summary>Daylight look shared by the six frames; exposure is the only per-frame knob.</summary>
        private static string Shot(string dir, string name, Vector3 camPos, float yaw, float pitch, float fov,
            Look look, WorldTerrain terrain, GameObject props, string source, string note = "")
        {
            var spawned = new System.Collections.Generic.List<GameObject>();
            Material prevSky = RenderSettings.skybox;

            Vector3 sunDir = Direction(yaw + look.sunYawOffset, look.sunElevation);
            Vector3 sun2Dir = Direction(yaw + look.sunYawOffset + 128f, look.sun2Elevation);

            var sunObject = new GameObject("Sun");
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = look.sunColor;
            sun.intensity = look.sunIntensity;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.9f;
            // One cascade over 420 m gives ~0.2 m texels; without bias the terrain
            // covers itself in black acne wedges.
            sun.shadowBias = 0.06f;
            sun.shadowNormalBias = 0.6f;
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
                // The screen-space fog pass does the heavy lifting now; keep only a
                // little per-pixel haze for the shadowed dune faces.
                terrain.TerrainMaterial.SetColor("_HeightFogColor", look.fog.linear);
                terrain.TerrainMaterial.SetFloat("_HeightFogStrength", night ? 0.10f : 0.22f);
                terrain.TerrainMaterial.SetFloat("_HeightFogDistance", look.fogEnd * 0.55f);
            }

            var fogMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/HeightFog.mat");
            if (fogMaterial != null)
            {
                fogMaterial.SetColor("_FogColor", look.fog);
                fogMaterial.SetFloat("_Density", night ? 0.0008f : 0.0016f);
                fogMaterial.SetFloat("_Falloff", 0.020f);
                fogMaterial.SetFloat("_StartDistance", 120f);
                fogMaterial.SetFloat("_BaseHeight", 0f);
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

            // One line per frame — S5 included — so a drifted auto-pick shows up in
            // the console instead of silently invalidating a before/after pair.
            // mode= is not cosmetic: batchmode shades vegetation ~1.5 stops darker than
            // the GUI editor on the same rig (验证 measured it), so a frame's look is
            // only comparable to another frame with the same mode.
            string extra = string.IsNullOrEmpty(note) ? string.Empty : " " + note;
            Debug.Log($"[AI003][ASHKAR] shot={name} 来源={source}{extra} " +
                      $"mode={(Application.isBatchMode ? "batchmode" : "gui")} " +
                      $"cam=({camPos.x:F1},{camPos.y:F1},{camPos.z:F1}) yaw={yaw:F1} pitch={pitch:F1} fov={fov:F1}");

            string path = Path.Combine(dir, name + ".png");
            RenderTo(camera, path);
            if (name == "forest")
            {
                DiagnoseVegetation(camera);
            }

            // Restore the skybox BEFORE destroying the temp sky material, or
            // RenderSettings is left pointing at a destroyed object and the scene
            // saves as "no skybox".
            RenderSettings.skybox = prevSky;
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
