using System;
using System.IO;
using AI003.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AI003.EditorTools
{
    /// <summary>
    /// Renders the world to PNG without entering play mode, so it works in a
    /// batch-mode editor that still has a graphics device:
    ///
    ///   Unity.exe -batchmode -projectPath ... \
    ///     -executeMethod AI003.EditorTools.RenderWorldPreview.Run -quit
    ///
    /// Produces artifacts/world-unity.png (third person spawn view) and
    /// artifacts/world-unity-overview.png (elevated landscape view).
    /// </summary>
    public static class RenderWorldPreview
    {
        private const int Width = 1280;
        private const int Height = 720;

        public static void Run()
        {
            try
            {
                RenderAll();
                Debug.Log("[AI003][SHOT] PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][SHOT] FAIL: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static void RenderAll()
        {
            string artifacts = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", "artifacts"));
            Directory.CreateDirectory(artifacts);

            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));

            var root = new GameObject("[AI003][SHOT] Root");
            var terrain = root.AddComponent<WorldTerrain>();
            terrain.Build(data);

            var sunObject = new GameObject("[AI003][SHOT] Sun");
            sunObject.transform.rotation = Quaternion.Euler(46f, -34f, 0f);
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 1.25f;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.48f, 0.56f);
            RenderSettings.fog = false;
            RenderSettings.skybox = null;

            var playerObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            playerObject.name = "[AI003][SHOT] Player";
            playerObject.transform.position = data.Spawn + Vector3.up * 0.9f;
            playerObject.transform.localScale = new Vector3(0.72f, 0.9f, 0.72f);
            UnityEngine.Object.DestroyImmediate(playerObject.GetComponent<Collider>());
            var playerMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            playerMaterial.SetColor("_BaseColor", new Color(0.10f, 0.52f, 0.58f));
            playerObject.GetComponent<MeshRenderer>().sharedMaterial = playerMaterial;

            var cameraObject = new GameObject("[AI003][SHOT] Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 62f;
            camera.nearClipPlane = 0.2f;
            camera.farClipPlane = 6000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.68f, 0.80f);

            // Spawn view: look towards the highest peak so mountains are in frame.
            Vector3 peak = FindPeak(data);
            Vector3 spawn = data.Spawn;
            Vector3 toPeak = Vector3.ProjectOnPlane(peak - spawn, Vector3.up).normalized;
            Vector3 cameraPosition = spawn - toPeak * 7.5f + Vector3.up * 3.4f;
            Render(camera, cameraPosition, spawn + Vector3.up * 1.6f, Path.Combine(artifacts, "world-unity.png"), true);

            // Overview: high above the south-west corner looking at the island.
            Vector3 overviewPosition = new Vector3(-data.SizeMeters * 0.42f, 320f, -data.SizeMeters * 0.42f);
            Render(camera, overviewPosition, new Vector3(0f, 40f, 0f), Path.Combine(artifacts, "world-unity-overview.png"), false);

            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(playerObject);
            UnityEngine.Object.DestroyImmediate(sunObject);
            UnityEngine.Object.DestroyImmediate(root);
        }

        private static Vector3 FindPeak(WorldData data)
        {
            int res = data.Resolution;
            int best = 0;
            for (int i = 0; i < data.Heights.Length; i++)
            {
                if (data.Heights[i] > data.Heights[best]) best = i;
            }
            int x = best % res;
            int z = best / res;
            float half = data.SizeMeters * 0.5f;
            return new Vector3(-half + x * data.CellSize, data.Heights[best], -half + z * data.CellSize);
        }

        private static void Render(Camera camera, Vector3 position, Vector3 lookAt, string path, bool fog)
        {
            camera.transform.position = position;
            camera.transform.LookAt(lookAt);

            RenderSettings.fog = fog;
            if (fog)
            {
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = new Color(0.62f, 0.71f, 0.80f);
                RenderSettings.fogStartDistance = 240f;
                RenderSettings.fogEndDistance = 1900f;
            }

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

            Debug.Log($"[AI003][SHOT] wrote {path}");
        }
    }
}
