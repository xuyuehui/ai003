using System;
using System.Collections.Generic;
using System.IO;
using AI003.World;
using UnityEditor;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Renders what the editor shows when you press Play: it builds the world
    /// through WorldBootstrap (same code path as Play mode) and captures the
    /// player camera. Used to check the runtime look without entering Play.
    /// </summary>
    public static class AshkarPlayShot
    {
        private const int Width = 1920;
        private const int Height = 804;

        [MenuItem("AI003/Render Play Frame")]
        public static void RenderFromMenu()
        {
            string path = Render();
            Debug.Log($"[AI003][PLAYSHOT] PASS {path}");
        }

        public static void Run()
        {
            try
            {
                string path = Render();
                Debug.Log($"[AI003][PLAYSHOT] PASS {path}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][PLAYSHOT] FAIL: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static string Render()
        {
            string outDir = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "..", "artifacts", "ashkar"));
            Directory.CreateDirectory(outDir);

            var root = new GameObject("[AI003][PLAYSHOT]");
            var boot = root.AddComponent<WorldBootstrap>();
            boot.Build();

            Camera camera = boot.PlayerCamera;
            if (camera == null)
            {
                throw new InvalidOperationException("player camera was not created");
            }

            // Three orbit poses, same rig as OrbitCamera, so the sky gradient and
            // the character framing can be judged together.
            Vector3 pivot = boot.Player.transform.position + Vector3.up * 1.5f;
            float[] pitches = { 12f, -12f, -34f };
            string[] names = { "play_level", "play_up", "play_down" };
            string last = null;

            for (int i = 0; i < pitches.Length; i++)
            {
                Quaternion rotation = Quaternion.Euler(pitches[i], 160f, 0f);
                Vector3 camPos = pivot - rotation * Vector3.forward * 6.5f;

                // Mirrors OrbitCamera's ground clearance.
                if (Physics.Raycast(camPos + Vector3.up * 20f, Vector3.down, out RaycastHit ground,
                        40f, ~0, QueryTriggerInteraction.Ignore))
                {
                    camPos.y = Mathf.Max(camPos.y, ground.point.y + 0.6f);
                }

                camera.transform.position = camPos;
                camera.transform.rotation = Quaternion.LookRotation(
                    (pivot + Vector3.up * 0.22f) - camPos);

                last = Path.Combine(outDir, names[i] + ".png");
                RenderTo(camera, last);

                if (i == 1)
                {
                    WriteDiagnostics(camera, outDir, i);
                }
            }

            // Diagnostic: same "look up" pose with the salt-sea plane hidden, to
            // tell a bad sky apart from a water plane crossing the view.
            if (boot.Terrain != null && boot.Terrain.Water != null)
            {
                boot.Terrain.Water.SetActive(false);
                Quaternion rotation = Quaternion.Euler(-12f, 160f, 0f);
                camera.transform.position = pivot - rotation * Vector3.forward * 6.5f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (pivot + Vector3.up * 0.22f) - camera.transform.position);
                last = Path.Combine(outDir, "play_up_nowater.png");
                RenderTo(camera, last);
            }

            UnityEngine.Object.DestroyImmediate(root);
            return last;
        }

        /// <summary>Writes what the camera actually hits, so the band can be identified.</summary>
        private static void WriteDiagnostics(Camera camera, string outDir, int pose)
        {
            var lines = new List<string>
            {
                $"pose={pose} camPos={camera.transform.position} forward={camera.transform.forward}",
            };

            float[] uvs = { 0.5f, 0.5f, 0.5f, 0.75f, 0.5f, 0.25f };
            for (int k = 0; k < uvs.Length; k += 2)
            {
                Ray ray = camera.ViewportPointToRay(new Vector3(uvs[k], 0.5f, 0f));
                if (Physics.Raycast(ray, out RaycastHit hit, 4000f, ~0, QueryTriggerInteraction.Ignore))
                {
                    lines.Add($"vp({uvs[k]:F2}) hit '{hit.collider.name}' at {hit.distance:F1} m " +
                              $"pos={hit.point} normal={hit.normal}");
                }
                else
                {
                    lines.Add($"vp({uvs[k]:F2}) no hit");
                }
            }

            foreach (var name in new[] { "[AI003] Dressing", "[AI003] Environment", "Water" })
            {
                var go = GameObject.Find(name);
                lines.Add($"{name}: {(go == null ? "absent" : "present active=" + go.activeInHierarchy)}");
            }

            File.WriteAllLines(System.IO.Path.Combine(outDir, "diag.txt"), lines);
        }

        private static void RenderTo(Camera camera, string path)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4,
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
    }
}
