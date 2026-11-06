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

        // Kept alive after the capture so the UE bot still has an online target
        // and actually fights (demo-room sets AI003_ROOM_HOLD).
        private static double HoldSeconds = 2.0;

        private static string _outputPath;
        private static double _playStartedAt;
        private static double _capturedAt;
        private static bool _captured;
        private static bool _subscribed;
        private static AI003.Net.ArenaClient arena;

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

            double hold = 0;
            string holdText = System.Environment.GetEnvironmentVariable("AI003_ROOM_HOLD");
            if (!string.IsNullOrEmpty(holdText))
            {
                double.TryParse(holdText, out hold);
            }
            if (hold > 0) HoldSeconds = hold;

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
                bool arenaMode = System.Environment.GetEnvironmentVariable("AI003_ARENA") == "1";
                bool ready;
                string roomId;
                int fighters;
                if (arenaMode)
                {
                    if (arena == null && bootstrap != null)
                    {
                        arena = bootstrap.GetComponent<AI003.Net.ArenaClient>();
                        if (arena == null) arena = bootstrap.gameObject.AddComponent<AI003.Net.ArenaClient>();
                    }
                    ready = arena != null && arena.InRoom && arena.RemoteCount >= 1 && worldReady;
                    roomId = "arena";
                    fighters = arena != null ? arena.RemoteCount + 1 : 0;
                }
                else
                {
                    // Wait until the local fighter exists and prediction has caught up.
                    ready = room != null && room.InRoom && room.Players >= 2
                            && worldReady
                            && room.PredictedTick >= room.ServerTick;
                    roomId = room != null ? room.RoomId : "?";
                    fighters = room != null ? room.Players : 0;
                }

                if (!_captured && ready && elapsed > ReadySeconds)
                {
                    ScreenCapture.CaptureScreenshot(_outputPath);
                    _captured = true;
                    _capturedAt = Time.realtimeSinceStartupAsDouble;
                    Debug.Log($"[AI003][ROOM-SHOT] screenshot requested ({roomId}, {fighters} fighters)");
                }

                if (_captured && Time.realtimeSinceStartupAsDouble - _capturedAt > HoldSeconds)
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
