using System;
using System.IO;
using AI003.Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace AI003.World
{
    /// <summary>
    /// Loads the shared world, builds terrain and water, and spawns the player,
    /// camera and sun. Installs itself after scene load so any scene works.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class WorldBootstrap : MonoBehaviour
    {
        public static WorldBootstrap Instance { get; private set; }

        public WorldData Data { get; private set; }
        public WorldTerrain Terrain { get; private set; }
        public ThirdPersonController Player { get; private set; }
        public Camera PlayerCamera { get; private set; }
        public OrbitCamera CameraRig { get; private set; }
        public bool IsReady => Data != null && Player != null;

        private Transform _model;
        private float _modelBaseYaw;

        // Extra yaw on top of the prefab's authored rotation. Default 0 because the
        // prefab already carries the correction; hardcoding 180 here would override
        // (and break) a prefab that was authored without a rotation.
        private const string ModelYawKey = "AI003.ModelYawExtra";

        /// <summary>
        /// F9 adds 180° to the model and persists the extra offset (PlayerPrefs),
        /// for machines whose imported rig still faces the other way.
        /// </summary>
        private void Update()
        {
            if (_model == null) return;
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.f9Key.wasPressedThisFrame)
            {
                float extra = PlayerPrefs.GetFloat(ModelYawKey, 0f) + 180f;
                extra = Mathf.Repeat(extra, 360f);
                PlayerPrefs.SetFloat(ModelYawKey, extra);
                PlayerPrefs.Save();
                _model.localRotation = Quaternion.Euler(0f, _modelBaseYaw + extra, 0f);
                Debug.Log($"[AI003] model extra yaw {extra:F0} (base {_modelBaseYaw:F0}, saved {ModelYawKey})");
            }
        }

        [SerializeField] private bool _autoBuild = true;

        private GUIStyle _hudStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            if (UnityEngine.Object.FindFirstObjectByType<WorldBootstrap>() != null)
            {
                return;
            }
            var go = new GameObject("[AI003] World");
            go.AddComponent<WorldBootstrap>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // Baked entity terrain (AI003/Bake Terrain SubScene) streams its chunks
            // by distance; without baked entities the component is a no-op.
            if (GetComponent<TerrainEcsStreaming>() == null)
            {
                gameObject.AddComponent<TerrainEcsStreaming>();
            }
        }

        private void Start()
        {
            if (_autoBuild)
            {
                Build();
            }
        }

        public void Build()
        {
            try
            {
                string directory = Path.Combine(Application.streamingAssetsPath, "world");
                Data = WorldData.Load(directory);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI003] cannot load world: {e.Message}");
                return;
            }

            ConfigureRendering();

            var terrainObject = new GameObject("[AI003] Terrain");
            terrainObject.transform.SetParent(transform, false);
            Terrain = terrainObject.AddComponent<WorldTerrain>();
            Terrain.Build(Data);

            SpawnPlayer();

            Debug.Log($"[AI003] world ready: {Data.SizeMeters:0} m, {Data.Resolution}x{Data.Resolution} vertices, " +
                      $"{Terrain.ChunkCount} chunks, spawn ({Data.Spawn.x:0}, {Data.Spawn.y:0}, {Data.Spawn.z:0})");
        }

        private void ConfigureRendering()
        {
            // Start from a clean slate: the template scene ships its own camera
            // and directional light, both would fight with ours.
            foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                camera.gameObject.SetActive(false);
            }
            foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                {
                    light.gameObject.SetActive(false);
                }
            }

            var sunObject = new GameObject("[AI003] Sun");
            sunObject.transform.SetParent(transform, false);
            sunObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            sun.shadowBias = 0.04f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.62f, 0.71f, 0.80f);
            RenderSettings.fogStartDistance = 320f;
            RenderSettings.fogEndDistance = 2200f;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.reflectionIntensity = 0.5f;
            DynamicGI.UpdateEnvironment();
        }

        private void SpawnPlayer()
        {
            var playerObject = new GameObject("[AI003] Player");
            playerObject.transform.position = Data.Spawn + Vector3.up * 1.2f;

            var controller = playerObject.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.slopeLimit = 48f;
            controller.stepOffset = 0.45f;
            controller.skinWidth = 0.03f;
            controller.minMoveDistance = 0f;

            Player = playerObject.AddComponent<ThirdPersonController>();
            Player.World = Data;
            Player.SeaLevel = Data.SeaLevel;

            AttachCharacter(playerObject);

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            PlayerCamera = cameraObject.AddComponent<Camera>();
            PlayerCamera.fieldOfView = 62f;
            PlayerCamera.nearClipPlane = 0.1f;
            PlayerCamera.farClipPlane = 4000f;
            PlayerCamera.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            PlayerCamera.backgroundColor = RenderSettings.fogColor;

            CameraRig = cameraObject.AddComponent<OrbitCamera>();
            CameraRig.Target = playerObject.transform;

            Player.CameraTransform = cameraObject.transform;

            CameraRig.Snap();
        }

        /// <summary>
        /// Spawns the Kenney protagonists model (Resources) under the controller and
        /// wires its animator; falls back to the old capsule if the asset is missing.
        /// </summary>
        private void AttachCharacter(GameObject playerObject)
        {
            const string root = "Characters/KenneyProtagonists/";
            GameObject prefab = Resources.Load<GameObject>(root + "Player");
            if (prefab == null)
            {
                Debug.LogWarning("[AI003] player prefab missing, using capsule fallback (run SetupPlayerCharacter)");
                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(playerObject.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                body.transform.localScale = new Vector3(0.72f, 0.9f, 0.72f);
                body.GetComponent<MeshRenderer>().sharedMaterial = CreateLitMaterial(new Color(0.10f, 0.52f, 0.58f));
                return;
            }

            GameObject model = Instantiate(prefab, playerObject.transform);
            model.name = "CharacterModel";
            model.transform.localPosition = Vector3.zero;

            // Keep the rotation authored in the prefab and add an optional extra
            // offset (PlayerPrefs / env). Overwriting the authored rotation with a
            // hardcoded 180° flips rigs whose prefab was not pre-rotated.
            _modelBaseYaw = model.transform.localEulerAngles.y;
            float extraYaw = PlayerPrefs.GetFloat(ModelYawKey, 0f);
            string overrideYaw = Environment.GetEnvironmentVariable("AI003_MODEL_YAW");
            if (!string.IsNullOrEmpty(overrideYaw) && float.TryParse(overrideYaw, out float parsed))
            {
                extraYaw = parsed;
            }
            model.transform.localRotation = Quaternion.Euler(0f, _modelBaseYaw + extraYaw, 0f);
            _model = model.transform;
            Debug.Log($"[AI003] model yaw base {_modelBaseYaw:F0} + extra {extraYaw:F0}");
            model.transform.localScale = Vector3.one * 0.45f;

            foreach (var collider in model.GetComponentsInChildren<Collider>())
            {
                Destroy(collider);
            }

            var animation = model.AddComponent<PlayerAnimation>();
            animation.Controller = Player;
        }

        private static Material CreateLitMaterial(Color color)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.35f);
            return material;
        }

        private void OnGUI()
        {
            if (!IsReady)
            {
                return;
            }

            // The arena draws its own HUD while it owns the avatar.
            if (AI003.Net.RoomClient.AnyActive)
            {
                return;
            }

            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(14, Screen.height / 48),
                    normal = { textColor = new Color(1f, 1f, 1f, 0.92f) },
                };
            }

            Vector3 position = Player.transform.position;
            float ground = Data.HeightAt(position.x, position.z);
            string state = Player.IsSwimming ? "swimming" : (Player.IsGrounded ? "grounded" : "airborne");

            string text =
                $"AI003  ·  {Data.Meta.name}\n" +
                $"pos   {position.x,7:0.0}  {position.y,6:0.0}  {position.z,7:0.0}\n" +
                $"speed {Player.CurrentSpeed,4:0.0} m/s   {state}\n" +
                $"ground {ground,6:0.0} m   sea {Data.SeaLevel:0} m";

            var rect = new Rect(16f, 16f, 320f, 120f);
            GUI.color = new Color(0f, 0f, 0f, 0.42f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 12f, rect.y + 10f, rect.width - 20f, rect.height), text, _hudStyle);
        }
    }
}
