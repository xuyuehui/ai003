using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace AI003.World
{
    /// <summary>Height field shared with ECS physics systems.</summary>
    public static class TerrainHeightField
    {
        public static NativeArray<float> Heights;
        public static int Resolution;
        public static float CellSize;
        public static float SizeMeters;
        public static float Half;

        public static bool IsValid => Heights.IsCreated && Resolution > 1;

        public static void Register(float[] heights, int resolution, float cellSize, float sizeMeters)
        {
            Unregister();
            Heights = new NativeArray<float>(heights, Allocator.Persistent);
            Resolution = resolution;
            CellSize = cellSize;
            SizeMeters = sizeMeters;
            Half = sizeMeters * 0.5f;
        }

        public static void Unregister()
        {
            if (Heights.IsCreated)
            {
                Heights.Dispose();
            }
        }

        public static float Sample(float x, float z)
        {
            if (!IsValid)
            {
                return 0f;
            }
            return SampleHeight(Heights, Resolution, CellSize, SizeMeters, x, z);
        }

        public static float SampleHeight(NativeArray<float> heights, int resolution, float cell, float size, float x, float z)
        {
            float half = size * 0.5f;
            float fx = math.clamp((x + half) / cell, 0f, resolution - 1.001f);
            float fz = math.clamp((z + half) / cell, 0f, resolution - 1.001f);
            int x0 = (int)fx;
            int z0 = (int)fz;
            float tx = fx - x0;
            float tz = fz - z0;
            int x1 = math.min(x0 + 1, resolution - 1);
            int z1 = math.min(z0 + 1, resolution - 1);
            float h00 = heights[z0 * resolution + x0];
            float h10 = heights[z0 * resolution + x1];
            float h01 = heights[z1 * resolution + x0];
            float h11 = heights[z1 * resolution + x1];
            return math.lerp(math.lerp(h00, h10, tx), math.lerp(h01, h11, tx), tz);
        }
    }

    /// <summary>Marks entities that should be clamped to the terrain.</summary>
    public struct TerrainBody : IComponentData
    {
        public float Radius;
    }

    [BurstCompile]
    public partial struct TerrainClampJob : IJobEntity
    {
        [Unity.Collections.ReadOnly] public NativeArray<float> Heights;
        public int Resolution;
        public float CellSize;
        public float SizeMeters;

        public void Execute(ref LocalTransform transform, in TerrainBody body)
        {
            float height = TerrainHeightField.SampleHeight(
                Heights, Resolution, CellSize, SizeMeters, transform.Position.x, transform.Position.z) + body.Radius;
            if (transform.Position.y < height)
            {
                transform.Position.y = height;
            }
        }
    }

    /// <summary>
    /// ECS-side terrain collision: keeps tagged bodies above the height field.
    /// This is the ECS counterpart of the GameObject character controller.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class TerrainClampSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            if (!TerrainHeightField.IsValid)
            {
                return;
            }
            new TerrainClampJob
            {
                Heights = TerrainHeightField.Heights,
                Resolution = TerrainHeightField.Resolution,
                CellSize = TerrainHeightField.CellSize,
                SizeMeters = TerrainHeightField.SizeMeters
            }.ScheduleParallel();
        }
    }
}
