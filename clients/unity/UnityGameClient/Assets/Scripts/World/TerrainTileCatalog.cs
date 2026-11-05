using UnityEngine;

namespace AI003.World
{
    /// <summary>Catalog of baked terrain tiles used by runtime ECS streaming.</summary>
    public sealed class TerrainTileCatalog : ScriptableObject
    {
        public string[] Guids = new string[0];
        public Vector2Int[] Coords = new Vector2Int[0];
        public int TilesPerAxis = 4;
        public float TileSizeMeters = 512f;
    }
}
