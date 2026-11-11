using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace AI003.World
{
    /// <summary>
    /// Renders the server-authoritative world events that ride along with the
    /// 10 Hz snapshot: the tide wall, the salt nodes, and the elder's wake.
    /// Nothing here decides state — it only interpolates towards what the server
    /// said.
    /// </summary>
    public sealed class WorldEvents : MonoBehaviour
    {
        public static WorldEvents Instance { get; private set; }

        private TideWall _tide;
        private ElderWake _elder;
        private readonly List<NodeMarker> _nodes = new List<NodeMarker>();
        private JObject _lastWorld;

        public static WorldEvents Ensure()
        {
            if (Instance != null)
            {
                return Instance;
            }
            var go = new GameObject("[AI003] WorldEvents");
            Instance = go.AddComponent<WorldEvents>();
            return Instance;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>Applies the `world` object from a snapshot.</summary>
        public void Apply(JObject world)
        {
            _lastWorld = world;

            var tide = world["tide"] as JObject;
            if (tide != null)
            {
                _tide ??= TideWall.Create(transform);
                _tide.Apply(tide);
            }

            var nodes = world["nodes"] as JArray;
            if (nodes != null)
            {
                EnsureNodeCount(nodes.Count);
                for (int i = 0; i < nodes.Count && i < _nodes.Count; i++)
                {
                    _nodes[i].Apply(nodes[i] as JObject);
                }
            }

            var elder = world["elder"] as JObject;
            if (elder != null)
            {
                _elder ??= ElderWake.Create(transform);
                _elder.Apply(elder);
            }
        }

        /// <summary>Jumps the visuals to the last server state (offscreen renders, tests).</summary>
        public void SnapToServer()
        {
            _tide?.Snap();
            _elder?.Snap();
        }

        public void Clear()
        {
            _lastWorld = null;
            if (_tide != null) _tide.gameObject.SetActive(false);
            if (_elder != null) _elder.gameObject.SetActive(false);
        }

        private void EnsureNodeCount(int count)
        {
            while (_nodes.Count < count)
            {
                _nodes.Add(NodeMarker.Create(transform, _nodes.Count));
            }
        }

        private void Update()
        {
            _tide?.Smooth();
            _elder?.Smooth();
        }

        // --------------------------------------------------------- tide ---
        private sealed class TideWall
        {
            private GameObject _root;
            private float _targetX;
            private float _currentX;

            public GameObject gameObject => _root;

            public static TideWall Create(Transform parent)
            {
                var root = new GameObject("TideWall");
                root.transform.SetParent(parent, false);

                var mesh = new Mesh { name = "TideWallQuad" };
                const float halfWidth = 1100f;
                const float height = 330f;
                mesh.vertices = new[]
                {
                    new Vector3(-halfWidth, 0f, 0f),
                    new Vector3(halfWidth, 0f, 0f),
                    new Vector3(-halfWidth, height, 0f),
                    new Vector3(halfWidth, height, 0f),
                };
                mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
                mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
                mesh.RecalculateBounds();

                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = root.AddComponent<MeshRenderer>();
                var shader = Shader.Find("AI003/TideWall");
                var material = new Material(shader) { name = "TideWall" };
                material.SetFloat("_NoiseScale", 30f);
                material.SetFloat("_Alpha", 0.80f);
                material.SetFloat("_Scroll", 0.45f);
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                // The quad faces +Z; the tide sweeps along +X, so face it along X.
                root.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                root.SetActive(false);

                return new TideWall { _root = root };
            }

            public void Apply(JObject tide)
            {
                bool active = tide.Value<bool?>("active") ?? false;
                _root.SetActive(active);
                if (!active)
                {
                    return;
                }
                _targetX = tide.Value<float?>("x") ?? 0f;
                float height = tide.Value<float?>("height") ?? 330f;
                _root.transform.localScale = new Vector3(1f, height / 330f, 1f);
            }

            public void Smooth()
            {
                if (!_root.activeSelf)
                {
                    return;
                }
                _currentX = Mathf.Lerp(_currentX, _targetX, 1f - Mathf.Exp(-4f * Time.deltaTime));
                ApplyPosition();
            }

            public void Snap()
            {
                _currentX = _targetX;
                ApplyPosition();
            }

            private void ApplyPosition()
            {
                var p = _root.transform.position;
                _root.transform.position = new Vector3(_currentX, p.y, 0f);
            }
        }

        // -------------------------------------------------------- nodes ---
        private sealed class NodeMarker
        {
            private GameObject _root;
            private Material _crystal;
            private Transform _ring;
            private float _targetProgress;

            public static NodeMarker Create(Transform parent, int index)
            {
                var root = new GameObject($"SaltNode{index}");
                root.transform.SetParent(parent, false);

                var marker = new NodeMarker { _root = root };

                var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.Destroy(ring.GetComponent<Collider>());
                ring.name = "Ring";
                ring.transform.SetParent(root.transform, false);
                ring.transform.localScale = new Vector3(9f, 0.06f, 9f);
                ring.transform.localPosition = new Vector3(0f, 0.08f, 0f);
                var ringMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "NodeRing" };
                ringMat.SetColor("_BaseColor", new Color(0.10f, 0.5f, 0.45f));
                ringMat.EnableKeyword("_EMISSION");
                ringMat.SetColor("_EmissionColor", new Color(0.15f, 0.7f, 0.62f) * 0.8f);
                ring.GetComponent<MeshRenderer>().sharedMaterial = ringMat;
                marker._ring = ring.transform;

                var crystalMat = new Material(Shader.Find("AI003/AshkarCrystal")) { name = "NodeCrystal" };
                crystalMat.SetColor("_BaseColor", new Color(0.10f, 0.30f, 0.28f));
                crystalMat.SetColor("_EmissionColor", new Color(0.30f, 0.90f, 0.82f));
                crystalMat.SetFloat("_EmissionStrength", 0.55f);
                crystalMat.SetColor("_FresnelColor", new Color(0.55f, 0.95f, 0.90f));
                crystalMat.SetFloat("_FresnelPower", 2.6f);
                marker._crystal = crystalMat;

                AshkarProps.CrystalCluster(root.transform.position, 5, 2.2f, crystalMat, 100 + index)
                    .transform.SetParent(root.transform, false);

                return marker;
            }

            public void Apply(JObject node)
            {
                float x = node.Value<float?>("x") ?? 0f;
                float z = node.Value<float?>("z") ?? 0f;
                float y = 0f;

                var world = WorldBootstrap.Instance;
                if (world != null && world.Data != null)
                {
                    y = world.Data.HeightAt(x, z);
                }
                _root.transform.position = new Vector3(x, y, z);

                string owner = node.Value<string>("owner") ?? string.Empty;
                float progress = node.Value<float?>("progress") ?? 0f;
                _targetProgress = progress;

                // Untouched: teal. Captured: amber.
                Color tint = string.IsNullOrEmpty(owner)
                    ? new Color(0.30f, 0.90f, 0.82f)
                    : new Color(1.0f, 0.72f, 0.35f);
                _crystal.SetColor("_EmissionColor", tint);
            }

            public void Smooth()
            {
                if (_ring == null)
                {
                    return;
                }
                float scale = Mathf.Lerp(_ring.localScale.x, 2f + _targetProgress * 7f, 0.15f);
                _ring.localScale = new Vector3(scale, 0.06f, scale);
            }
        }

        // -------------------------------------------------------- elder ---
        private sealed class ElderWake
        {
            private GameObject _root;
            private float _targetX;
            private float _targetZ;
            private float _currentX;
            private float _currentZ;

            public GameObject gameObject => _root;

            public static ElderWake Create(Transform parent)
            {
                var root = new GameObject("ElderWake");
                root.transform.SetParent(parent, false);

                var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(body.GetComponent<Collider>());
                body.name = "Wake";
                body.transform.SetParent(root.transform, false);
                body.transform.localScale = new Vector3(220f, 26f, 70f);
                body.transform.localPosition = new Vector3(0f, 6f, 0f);
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "ElderWake" };
                mat.SetColor("_BaseColor", new Color(0.16f, 0.12f, 0.09f));
                mat.SetFloat("_Smoothness", 0.05f);
                body.GetComponent<MeshRenderer>().sharedMaterial = mat;
                body.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

                root.SetActive(false);
                return new ElderWake { _root = root };
            }

            public void Apply(JObject elder)
            {
                bool active = elder.Value<bool?>("active") ?? false;
                _root.SetActive(active);
                if (!active)
                {
                    return;
                }
                _targetX = elder.Value<float?>("x") ?? 0f;
                _targetZ = elder.Value<float?>("z") ?? 0f;
            }

            public void Smooth()
            {
                if (!_root.activeSelf)
                {
                    return;
                }
                _currentX = Mathf.Lerp(_currentX, _targetX, 1f - Mathf.Exp(-2f * Time.deltaTime));
                _currentZ = Mathf.Lerp(_currentZ, _targetZ, 1f - Mathf.Exp(-2f * Time.deltaTime));
                ApplyPosition();
            }

            public void Snap()
            {
                _currentX = _targetX;
                _currentZ = _targetZ;
                ApplyPosition();
            }

            private void ApplyPosition()
            {
                float y = WorldBootstrap.Instance != null && WorldBootstrap.Instance.Data != null
                    ? WorldBootstrap.Instance.Data.HeightAt(_currentX, _currentZ)
                    : 0f;
                _root.transform.position = new Vector3(_currentX, y, _currentZ);
            }
        }
    }
}
