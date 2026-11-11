using System;
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

            // Slightly above and behind the orbit camera so the plaza and the
            // wellspire are both in frame.
            Vector3 target = boot.Player.transform.position + Vector3.up * 2f;
            Vector3 offset = new Vector3(0f, 7f, -16f);
            camera.transform.position = target + offset;
            camera.transform.LookAt(target + new Vector3(0f, 3f, 0f));

            string path = Path.Combine(outDir, "play_spawn.png");
            RenderTo(camera, path);

            UnityEngine.Object.DestroyImmediate(root);
            return path;
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
