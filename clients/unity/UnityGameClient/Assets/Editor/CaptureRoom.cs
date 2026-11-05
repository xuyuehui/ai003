using System;
using System.IO;
using AI003.Net;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// Enters play mode with AI003_ROOM=1, waits until two fighters are in the
    /// room, captures the game view and quits. Used by scripts/demo-room.ps1 so
    /// the room demo needs no manual clicking:
    ///
    ///   AI003_ROOM=1 Unity.exe -projectPath ... \
    ///     -executeMethod AI003.EditorTools.CaptureRoom.Run
    /// </summary>
    public static class CaptureRoom
    {
        private const string ScenePath = "Assets/Scenes/OpenWorld.unity";
        private const double ReadySeconds = 2.5;
        private const double TimeoutSeconds = 120;

        private static string _outputPath;
        private static double _playStartedAt;
        private static double _capturedAt;
        private static bool _captured;
        private static bool _subscribed;

        public static void Run()
        {
            _outputPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "..", "artifacts", "demo", "room-unity.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath));

            if (File.Exists(ScenePath))
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            _subscribed = true;
            EditorApplication.EnterPlaymode();
            Debug.Log($"[AI003][ROOM-SHOT] capturing to {_outputPath}");
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

                double elapsed = Time.realtimeSinceStartupAsDouble - _playStartedAt;
                RoomClient room = RoomClient.Instance;
                var bootstrap = UnityEngine.Object.FindFirstObjectByType<AI003.World.WorldBootstrap>();
                bool worldReady = bootstrap != null && bootstrap.IsReady;
                // Wait until the local fighter exists and prediction has caught up:
                // during world bootstrap the controller is missing for a moment and
                // the room tick would otherwise be captured ahead of the prediction.
                bool ready = room != null && room.InRoom && room.Players >= 2
                             && worldReady
                             && room.PredictedTick >= room.ServerTick;

                if (!_captured && ready && elapsed > ReadySeconds)
                {
                    ScreenCapture.CaptureScreenshot(_outputPath);
                    _captured = true;
                    _capturedAt = Time.realtimeSinceStartupAsDouble;
                    Debug.Log($"[AI003][ROOM-SHOT] screenshot requested ({room.RoomId}, {room.Players} fighters)");
                }

                if (_captured && Time.realtimeSinceStartupAsDouble - _capturedAt > 2.0)
                {
                    Finish(0);
                }

                if (elapsed > TimeoutSeconds)
                {
                    Debug.LogError("[AI003][ROOM-SHOT] timed out waiting for two fighters");
                    Finish(1);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003][ROOM-SHOT] tick failed: {e}");
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
