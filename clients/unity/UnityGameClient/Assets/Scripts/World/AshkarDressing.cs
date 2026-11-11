using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Places the Ashkar set dressing around the spawn so Play mode looks like the
    /// key frames: Well One, a wellspire on the ridge, a sail-sled and two figures.
    /// All procedural, no imported assets.
    /// </summary>
    public static class AshkarDressing
    {
        public static void Build(WorldBootstrap boot)
        {
            if (boot == null || boot.Data == null)
            {
                return;
            }

            WorldData data = boot.Data;
            Vector3 spawn = data.Spawn;
            Vector3 forward = new Vector3(0.72f, 0f, 0.69f);
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);

            var root = new GameObject("[AI003] Dressing");
            root.transform.SetParent(boot.transform, false);

            var brick = AshkarProps.Material("SaltBrick", new Color(0.40f, 0.38f, 0.34f), 0.18f);
            var wood = AshkarProps.Material("WeatheredWood", new Color(0.20f, 0.16f, 0.12f), 0.12f);
            var bell = AshkarProps.Material("BellBronze", new Color(0.35f, 0.28f, 0.16f), 0.55f, 0.7f);
            var body = AshkarProps.Material("BasaltBody", new Color(0.12f, 0.10f, 0.09f), 0.25f);
            var trim = AshkarProps.Material("Brass", new Color(0.42f, 0.31f, 0.16f), 0.45f, 0.6f);
            var robe = AshkarProps.Material("Robes", new Color(0.09f, 0.07f, 0.06f), 0.1f);
            var sail = AshkarProps.Material("SailCloth", new Color(0.62f, 0.48f, 0.34f), 0.15f);

            Vector3 plaza = Ground(data, spawn + forward * 16f);
            AshkarProps.WellOne(plaza, 20f, brick, wood, bell).transform.SetParent(root.transform, true);

            Vector3 spire = Ground(data, spawn + forward * 95f + right * 55f);
            AshkarProps.Wellspire(spire, 80f, body, trim).transform.SetParent(root.transform, true);

            Vector3 sled = Ground(data, spawn - forward * 6f + right * 7f);
            AshkarProps.SailSled(sled, 35f, body, sail, trim).transform.SetParent(root.transform, true);

            AshkarProps.Figure(Ground(data, spawn + forward * 8f + right * 9f), 200f, 1.8f, robe)
                .transform.SetParent(root.transform, true);
            AshkarProps.Figure(Ground(data, spawn + forward * 11f + right * 12f), 210f, 1.75f, robe)
                .transform.SetParent(root.transform, true);
        }

        private static Vector3 Ground(WorldData data, Vector3 at)
        {
            at.y = data.HeightAt(at.x, at.z);
            return at;
        }
    }
}
