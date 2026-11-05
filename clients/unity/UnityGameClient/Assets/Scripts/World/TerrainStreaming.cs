using System.Collections.Generic;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace AI003.World
{
    /// <summary>
    /// Streams terrain chunks around the player with three LOD levels.
    ///
    /// Chunks are built lazily (full resolution near the player, decimated further
    /// out) and deactivated beyond the view distance, so a multi-kilometre world
    /// only pays for what the camera can see. Counters live in <see cref="WorldStats"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TerrainStreaming : MonoBehaviour
    {
        private sealed class Chunk
        {
            public int X;
            public int Z;
            public int Lod = -1;
            public GameObject Object;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public MeshCollider Collider;
            public Vector3 Center;
        }

        [SerializeField] private float viewDistance = 2000f;
        [SerializeField] private float nearDistance = 700f;
        [SerializeField] private float midDistance = 1400f;
        [SerializeField] private float refreshSeconds = 0.25f;

        private WorldData _data;
        private WorldTerrain _terrain;
        private bool _ecs;
        private EntityManager _em;
        private Entity[] _entities;
        public int LoadedChunks { get; private set; }
        private Chunk[] _chunks;
        private int _chunksPerAxis;
        private float _chunkSize;
        private Transform _player;
        private float _nextRefresh;
        private float _buildMs;

        public void Configure(WorldTerrain terrain, WorldData data)
        {
            _terrain = terrain;
            _data = data;
        }

        private void Start()
        {
            _chunksPerAxis = _terrain.ChunksPerAxis;
            _chunkSize = WorldTerrain.CellsPerChunk * _data.CellSize;
            _chunks = new Chunk[_chunksPerAxis * _chunksPerAxis];
            _entities = new Entity[_chunks.Length];
            _ecs = System.Environment.GetEnvironmentVariable("AI003_TERRAIN_ECS") == "1"
                && Unity.Entities.World.DefaultGameObjectInjectionWorld != null
                && Unity.Entities.World.DefaultGameObjectInjectionWorld.IsCreated;
            if (_ecs) _em = Unity.Entities.World.DefaultGameObjectInjectionWorld.EntityManager;

            float half = _data.SizeMeters * 0.5f;
            for (int z = 0; z < _chunksPerAxis; z++)
            {
                for (int x = 0; x < _chunksPerAxis; x++)
                {
                    var center = new Vector3(
                        -half + (x + 0.5f) * _chunkSize,
                        0f,
                        -half + (z + 0.5f) * _chunkSize);
                    _chunks[z * _chunksPerAxis + x] = new Chunk { X = x, Z = z, Center = center };
                }
            }

            WorldStats.Reset();
            WorldStats.TotalChunks = _chunks.Length;
            // Entities Graphics ignores entities created before its systems finish
            // initialising, so that path waits a moment; the GameObject path must
            // build immediately so the player spawns on a collider.
            _nextRefresh = Time.unscaledTime + 0.75f;
            if (!_ecs) Refresh();
        }

        /// <summary>Test seam: shrink the view so the test can prove culling.</summary>
        public void SetViewDistance(float distance)
        {
            viewDistance = distance;
            nearDistance = distance * 0.35f;
            midDistance = distance * 0.7f;
        }

        /// <summary>Test seam: override the streaming focus.</summary>
        public void SetPlayer(Transform player)
        {
            _player = player;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + refreshSeconds;
            Refresh();
        }

        private void Refresh()
        {
            if (_player == null && WorldBootstrap.Instance != null && WorldBootstrap.Instance.Player != null)
            {
                _player = WorldBootstrap.Instance.Player.transform;
            }
            if (_player == null) return;

            float started = Time.realtimeSinceStartup;
            _buildMs = 0f;

            Vector3 player = _player.position;
            for (int i = 0; i < _chunks.Length; i++)
            {
                Chunk chunk = _chunks[i];
                float distance = new Vector2(chunk.Center.x - player.x, chunk.Center.z - player.z).magnitude;

                if (distance > viewDistance)
                {
                    Hide(i, chunk);
                    continue;
                }

                int lod = distance <= nearDistance ? 0 : distance <= midDistance ? 1 : 2;
                if (chunk.Lod != lod)
                {
                    Build(chunk, lod);
                }
                Show(i, chunk);
            }

            if (_player == null) return;

            int loaded = 0;
            int vertices = 0;
            int colliders = 0;
            for (int i = 0; i < _chunks.Length; i++)
            {
                Chunk chunk = _chunks[i];
                if (!IsVisible(i, chunk)) continue;
                loaded++;
                vertices += chunk.Mesh != null ? chunk.Mesh.vertexCount : 0;
                if (chunk.Collider != null && chunk.Collider.enabled) colliders++;
            }

            WorldStats.LoadedChunks = loaded;
            WorldStats.Vertices = vertices;
            WorldStats.Colliders = colliders;
            WorldStats.LastRefreshMs = (Time.realtimeSinceStartup - started) * 1000f;
            WorldStats.TotalBuildMs += _buildMs;
            LoadedChunks = loaded;
        }

        private bool IsVisible(int index, Chunk chunk)
        {
            if (_ecs)
            {
                Entity entity = _entities[index];
                return entity != Entity.Null && !_em.HasComponent<DisableRendering>(entity);
            }
            return chunk.Object != null && chunk.Object.activeSelf;
        }

        private void Show(int index, Chunk chunk)
        {
            if (!_ecs)
            {
                if (chunk.Object != null) chunk.Object.SetActive(true);
                return;
            }

            Entity entity = _entities[index];
            if (entity != Entity.Null && _em.HasComponent<DisableRendering>(entity))
            {
                _em.RemoveComponent<DisableRendering>(entity);
            }
        }

        private void Hide(int index, Chunk chunk)
        {
            if (!_ecs)
            {
                if (chunk.Object != null) chunk.Object.SetActive(false);
                return;
            }

            Entity entity = _entities[index];
            if (entity != Entity.Null && !_em.HasComponent<DisableRendering>(entity))
            {
                _em.AddComponent<DisableRendering>(entity);
            }
        }

        private void Build(Chunk chunk, int lod)
        {
            int stride = lod == 0 ? 1 : lod == 1 ? 2 : 4;
            float started = Time.realtimeSinceStartup;

            Vector3 chunkPosition = new Vector3(
                -_data.SizeMeters * 0.5f + chunk.X * _chunkSize,
                0f,
                -_data.SizeMeters * 0.5f + chunk.Z * _chunkSize);

            if (chunk.Mesh != null)
            {
                Destroy(chunk.Mesh);
                chunk.Mesh = null;
            }
            chunk.Mesh = WorldTerrain.BuildChunkMesh(_data, chunk.X, chunk.Z, stride);

            if (_ecs)
            {
                BuildEcs(chunk, chunkPosition, lod);
            }
            else
            {
                BuildGameObject(chunk, chunkPosition, lod);
            }

            chunk.Lod = lod;
            _buildMs += (Time.realtimeSinceStartup - started) * 1000f;
        }

        private void BuildGameObject(Chunk chunk, Vector3 chunkPosition, int lod)
        {
            if (chunk.Object == null)
            {
                chunk.Object = new GameObject($"Chunk_{chunk.X}_{chunk.Z}");
                chunk.Object.transform.SetParent(transform, false);
                chunk.Object.transform.localPosition = chunkPosition;

                chunk.Object.AddComponent<MeshFilter>();
                chunk.Renderer = chunk.Object.AddComponent<MeshRenderer>();
                chunk.Renderer.sharedMaterial = _terrain.TerrainMaterial;
                chunk.Renderer.shadowCastingMode = ShadowCastingMode.Off;
                chunk.Renderer.receiveShadows = true;
            }

            chunk.Object.GetComponent<MeshFilter>().sharedMesh = chunk.Mesh;

            if (lod == 0)
            {
                if (chunk.Collider == null) chunk.Collider = chunk.Object.AddComponent<MeshCollider>();
                chunk.Collider.sharedMesh = chunk.Mesh;
                chunk.Collider.enabled = true;
            }
            else if (chunk.Collider != null)
            {
                chunk.Collider.enabled = false;
            }
        }

        private void BuildEcs(Chunk chunk, Vector3 chunkPosition, int lod)
        {
            int index = chunk.Z * _chunksPerAxis + chunk.X;

            // Collision stays on a hidden GameObject; rendering is the entity.
            if (chunk.Object == null)
            {
                chunk.Object = new GameObject($"Chunk_{chunk.X}_{chunk.Z}");
                chunk.Object.transform.SetParent(transform, false);
                chunk.Object.transform.localPosition = chunkPosition;
                chunk.Collider = chunk.Object.AddComponent<MeshCollider>();
            }

            chunk.Collider.sharedMesh = chunk.Mesh;
            chunk.Collider.enabled = lod == 0;

            var array = new RenderMeshArray(
                new[] { _terrain.TerrainMaterial }, new[] { chunk.Mesh });
            var description = new RenderMeshDescription(ShadowCastingMode.Off, true);

            if (_entities[index] == Entity.Null)
            {
                Entity entity = _em.CreateEntity();
                _em.AddComponentData(entity, LocalTransform.FromPositionRotationScale(
                    chunkPosition, Quaternion.identity, 1f));
                RenderMeshUtility.AddComponents(entity, _em, in description, array,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                _entities[index] = entity;
            }
            else
            {
                _em.SetComponentData(_entities[index], LocalTransform.FromPositionRotationScale(
                    chunkPosition, Quaternion.identity, 1f));
                _em.SetSharedComponentManaged(_entities[index], array);
            }
        }

        private void OnDestroy()
        {
            if (!_ecs || _entities == null) return;
            for (int i = 0; i < _entities.Length; i++)
            {
                if (_entities[i] != Entity.Null &&
                    Unity.Entities.World.DefaultGameObjectInjectionWorld != null &&
                    Unity.Entities.World.DefaultGameObjectInjectionWorld.IsCreated &&
                    _em.Exists(_entities[i]))
                {
                    _em.DestroyEntity(_entities[i]);
                }
            }
        }
    }
}
