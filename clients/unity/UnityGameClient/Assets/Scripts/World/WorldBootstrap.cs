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

        [SerializeField] private bool _autoBuild = true;
        [SerializeField] private bool _ashkarLook = true;
        /// <summary>Baked scenes already contain the terrain, props, lights and
        /// post volume; they only need the player spawned at Play.</summary>
        [SerializeField] private bool _buildTerrain = true;

        private GUIStyle _hudStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            if (UnityEngine.Object.FindAnyObjectByType<WorldBootstrap>() != null)
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

            if (!_buildTerrain)
            {
                // Baked scene: terrain, props, lights and post already live in the
                // scene file. We only need the data for physics queries and the
                // player to walk around. Do NOT run ConfigureRendering here: it
                // would disable the baked lights.
                Terrain = UnityEngine.Object.FindAnyObjectByType<WorldTerrain>();
                if (Terrain != null && HasBakedMeshes(Terrain))
                {
                    SpawnPlayer();
                    Debug.Log($"[AI003] baked world ready: {Data.Resolution}x{Data.Resolution} vertices, spawn " +
                              $"({Data.Spawn.x:0}, {Data.Spawn.y:0}, {Data.Spawn.z:0})");
                    return;
                }

                // Baked meshes are git-ignored; on a fresh clone they can be
                // missing. Fall back to building the same world at runtime.
                Debug.LogWarning("[AI003] baked terrain meshes missing — building at runtime " +
                                 "(run AI003/Bake Static Scene to bake them)");
                _buildTerrain = true;
            }

            ConfigureRendering();

            // The Ashkar palette is a client-side skin: it only changes the vertex
            // colours this client builds, the world files stay untouched.
            Color32[] colors = null;
            string shaderName = null;
            if (_ashkarLook)
            {
                var theme = WorldTheme.CreateAshkar();
                colors = theme.BuildColorGrid(Data);
                shaderName = "AI003/AshkarTerrain";
            }

            var terrainObject = new GameObject("[AI003] Terrain");
            terrainObject.transform.SetParent(transform, false);
            Terrain = terrainObject.AddComponent<WorldTerrain>();
            Terrain.Build(Data, colors, shaderName);

            SpawnPlayer();

            if (_ashkarLook)
            {
                AshkarLook.Apply(this);
                AshkarDressing.Build(this);
            }

            Debug.Log($"[AI003] world ready: {Data.SizeMeters:0} m, {Data.Resolution}x{Data.Resolution} vertices, " +
                      $"{Terrain.ChunkCount} chunks, spawn ({Data.Spawn.x:0}, {Data.Spawn.y:0}, {Data.Spawn.z:0})");
        }

        private void ConfigureRendering()
        {
            // Start from a clean slate: the template scene ships its own camera
            // and directional light, both would fight with ours.
            foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            {
                camera.gameObject.SetActive(false);
            }
            foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
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

        /// <summary>True when every terrain chunk in the scene has its mesh asset.</summary>
        private static bool HasBakedMeshes(WorldTerrain terrain)
        {
            var filters = terrain.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length == 0)
            {
                return false;
            }
            foreach (var filter in filters)
            {
                if (filter.sharedMesh == null)
                {
                    return false;
                }
            }
            return true;
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
            // The prefab already carries the avatar, material and 180-degree facing;
            // keep the visual scale in one place (3.96 m model -> 1.8 m character).
            model.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
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
