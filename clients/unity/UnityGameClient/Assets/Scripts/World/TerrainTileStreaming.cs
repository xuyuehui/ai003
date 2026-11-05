using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;
using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Memory-level ECS terrain streaming: each 512 m tile is its own baked
    /// SubScene. Tiles inside the view distance are loaded with SceneSystem and
    /// unloaded again when the camera moves away.
    /// </summary>
    public sealed class TerrainTileStreaming : MonoBehaviour
    {
        public float ViewDistance = 1200f;
        public float UnloadDistance = 1600f;
        public float UpdateInterval = 0.5f;

        private TerrainTileCatalog _catalog;
        private Entity[] _loaded;
        private float _nextUpdate;
        private int _loadedCount = -1;

        private void Start()
        {
            // Only the tiled streaming demo scene streams tiles; the normal game
            // scene already has the GameObject terrain and must not double up.
            if (!gameObject.scene.name.Contains("Streaming"))
            {
                enabled = false;
                return;
            }
            _catalog = Resources.Load<TerrainTileCatalog>("TerrainTileCatalog");
            if (_catalog == null || _catalog.Guids.Length == 0)
            {
                enabled = false;
                return;
            }
            _loaded = new Entity[_catalog.Guids.Length];
            Debug.Log($"[AI003][TILE-STREAM] catalog: {_catalog.Guids.Length} tiles of {_catalog.TileSizeMeters:0} m");
        }

        private void Update()
        {
            if (_catalog == null || Time.time < _nextUpdate)
            {
                return;
            }
            _nextUpdate = Time.time + UpdateInterval;

            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return;
            }

            Camera camera = Camera.main;
            Vector3 eye = camera != null
                ? camera.transform.position
                : (WorldBootstrap.Instance != null ? WorldBootstrap.Instance.transform.position : Vector3.zero);

            int loaded = 0;
            for (int i = 0; i < _catalog.Guids.Length; i++)
            {
                Vector2Int coord = _catalog.Coords[i];
                float half = _catalog.TileSizeMeters * 0.5f;
                var center = new Vector3(
                    -half * _catalog.TilesPerAxis + (coord.x + 0.5f) * _catalog.TileSizeMeters,
                    0f,
                    -half * _catalog.TilesPerAxis + (coord.y + 0.5f) * _catalog.TileSizeMeters);
                float distance = Vector3.Distance(new Vector3(eye.x, 0f, eye.z), new Vector3(center.x, 0f, center.z));

                bool isLoaded = _loaded[i] != Entity.Null && world.EntityManager.Exists(_loaded[i]);
                if (distance <= ViewDistance && !isLoaded)
                {
                    var guid = new Unity.Entities.Hash128(_catalog.Guids[i]);
                    _loaded[i] = SceneSystem.LoadSceneAsync(world.Unmanaged, guid, default);
                    isLoaded = true;
                }
                else if (distance > UnloadDistance && isLoaded)
                {
                    SceneSystem.UnloadScene(world.Unmanaged, _loaded[i], SceneSystem.UnloadParameters.DestroyMetaEntities);
                    _loaded[i] = Entity.Null;
                    isLoaded = false;
                }

                if (isLoaded)
                {
                    loaded++;
                }
            }

            if (loaded != _loadedCount)
            {
                _loadedCount = loaded;
                Debug.Log($"[AI003][TILE-STREAM] tiles loaded {loaded}/{_catalog.Guids.Length} (view {ViewDistance:0} m)");
            }
        }
    }
}
