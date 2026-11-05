using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Runtime streaming for the baked entity terrain: chunks outside the view
    /// distance get DisableRendering, chunks inside get it removed again. This
    /// streams what is drawn (the baked entities themselves stay resident; real
    /// memory streaming needs per-chunk SubScenes or BRG).
    /// </summary>
    public sealed class TerrainEcsStreaming : MonoBehaviour
    {
        public float ViewDistance = 900f;
        public float UpdateInterval = 0.5f;

        private Unity.Entities.World _world;
        private EntityQuery _query;
        private readonly List<Entity> _chunks = new List<Entity>();
        private float _nextUpdate;
        private int _visible = -1;

        private void Update()
        {
            if (Time.time < _nextUpdate)
            {
                return;
            }
            _nextUpdate = Time.time + UpdateInterval;

            if (_world == null || !_world.IsCreated)
            {
                _world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                if (_world == null || !_world.IsCreated)
                {
                    return;
                }
                _query = _world.EntityManager.CreateEntityQuery(
                    typeof(MaterialMeshInfo), typeof(LocalToWorld));
            }

            var em = _world.EntityManager;
            if (_query.IsEmptyIgnoreFilter)
            {
                return;
            }

            if (_chunks.Count == 0)
            {
                using var entities = _query.ToEntityArray(Allocator.Temp);
                _chunks.AddRange(entities);
            }

            Camera camera = Camera.main;
            Vector3 eye = camera != null
                ? camera.transform.position
                : (WorldBootstrap.Instance != null ? WorldBootstrap.Instance.transform.position : Vector3.zero);

            int visible = 0;
            for (int i = 0; i < _chunks.Count; i++)
            {
                Entity entity = _chunks[i];
                if (!em.Exists(entity))
                {
                    continue;
                }

                Vector3 chunk = em.GetComponentData<LocalToWorld>(entity).Position;
                bool near = (chunk - eye).sqrMagnitude <= ViewDistance * ViewDistance;
                bool disabled = em.HasComponent<DisableRendering>(entity);

                if (near && disabled)
                {
                    em.RemoveComponent<DisableRendering>(entity);
                }
                else if (!near && !disabled)
                {
                    em.AddComponent<DisableRendering>(entity);
                }

                if (near)
                {
                    visible++;
                }
            }

            if (visible != _visible)
            {
                _visible = visible;
                Debug.Log($"[AI003][ECS-STREAM] chunks visible {visible}/{_chunks.Count} (view {ViewDistance:0} m)");
            }
        }
    }
}
