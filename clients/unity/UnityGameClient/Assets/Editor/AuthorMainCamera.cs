using System.IO;
using AI003.Player;
using AI003.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace AI003.EditorTools
{
    /// <summary>
    /// Authors the gameplay camera into the OPEN scene (Main Camera tag, Camera +
    /// OrbitCamera). Play reuses it instead of creating one.
    /// </summary>
    public static class AuthorMainCamera
    {
        private const string CameraName = "Main Camera";

        [MenuItem("AI003/Author Main Camera")]
        public static void Run()
        {
            var data = WorldData.Load(Path.Combine(Application.streamingAssetsPath, "world"));

            var existing = GameObject.Find(CameraName);
            var go = existing != null ? existing : new GameObject(CameraName);
            go.tag = "MainCamera";

            var camera = go.GetComponent<Camera>();
            if (camera == null)
            {
                camera = go.AddComponent<Camera>();
            }
            camera.fieldOfView = 62f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 8000f;
            camera.clearFlags = RenderSettings.skybox != null
                ? CameraClearFlags.Skybox
                : CameraClearFlags.SolidColor;
            camera.backgroundColor = RenderSettings.fogColor;

            // The scene owns its [AI003] Environment, so WorldBootstrap skips
            // AshkarLook.ApplyScene at runtime. The camera has to carry the post
            // switch itself or the authored volume is never applied.
            var cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.None; // edge AA comes from PC_RPAsset MSAA

            if (go.GetComponent<AudioListener>() == null &&
                Object.FindAnyObjectByType<AudioListener>() == null)
            {
                go.AddComponent<AudioListener>();
            }

            // Same pose the code default produced: 6.5 m behind, 16 degrees down.
            Quaternion rotation = Quaternion.Euler(16f, 160f, 0f);
            Vector3 pivot = data.Spawn + Vector3.up * 2.7f;
            go.transform.position = pivot - rotation * Vector3.forward * 6.5f;
            go.transform.rotation = rotation;

            if (go.GetComponent<OrbitCamera>() == null)
            {
                go.AddComponent<OrbitCamera>();
            }

            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[AI003][AUTHOR] {CameraName} placed at {go.transform.position} — save the scene");
        }
    }
}
