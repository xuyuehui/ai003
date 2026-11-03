using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Opens OpenWorld, enters play mode, waits for the world to build, takes a
    /// screenshot and quits. Must run with graphics (no -batchmode/-nographics):
    ///   Unity.exe -projectPath ... -executeMethod AI003.EditorTools.CaptureWorld.Run
    /// </summary>
    public static class CaptureWorld
    {
        private const string ScenePath = "Assets/Scenes/OpenWorld.unity";
        private static string _outputPath;
        private static double _playStartedAt;
        private static double _capturedAt;
        private static bool _captured;
        private static bool _subscribed;

        public static void Run()
        {
            // Application.dataPath is <project>/Assets, four levels below the repo root.
            _outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", "artifacts", "world-unity.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath));

            if (File.Exists(ScenePath))
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            // Entering play mode normally reloads the domain, which would wipe the
            // static state and the update callback below.
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            _subscribed = true;
            EditorApplication.EnterPlaymode();
            Debug.Log($"[AI003][SHOT] capturing to {_outputPath}");
        }

        private static void Tick()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return;
                }

                if (_playStartedAt <= 0)
                {
                    _playStartedAt = Time.realtimeSinceStartupAsDouble;
                }

                var bootstrap = UnityEngine.Object.FindFirstObjectByType<AI003.World.WorldBootstrap>();
                bool ready = bootstrap != null && bootstrap.IsReady;
                double elapsed = Time.realtimeSinceStartupAsDouble - _playStartedAt;

                if (!_captured && ready && elapsed > 2.5)
                {
                    ScreenCapture.CaptureScreenshot(_outputPath);
                    _captured = true;
                    _capturedAt = Time.realtimeSinceStartupAsDouble;
                    Debug.Log("[AI003][SHOT] screenshot requested");
                }

                if (_captured && Time.realtimeSinceStartupAsDouble - _capturedAt > 2.0)
                {
                    Finish(0);
                }

                if (elapsed > 30)
                {
                    Debug.LogError("[AI003][SHOT] timed out waiting for the world");
                    Finish(1);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][SHOT] tick failed: {e}");
                Finish(1);
            }
        }

        private static void Finish(int code)
        {
            if (_subscribed)
            {
                EditorApplication.update -= Tick;
                _subscribed = false;
            }
            EditorApplication.Exit(code);
        }
    }
}
