using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using AI003.World;

namespace AI003.Tests.EditMode
{
    /// <summary>
    /// Pins the shared world contract that Unity consumes: the same four files the
    /// server generates, the signed heightmap hash, coherent metadata, and the
    /// failure modes of <see cref="WorldData.Load"/>.
    ///
    /// The hash is the group's "the world is not re-signed" rule: if worldgen ever
    /// regenerates different terrain, this test is what notices on the Unity side.
    /// </summary>
    public class WorldDataContractTests
    {
        private const string SignedHeightmapSha256 =
            "B4B27C6DCC1F14B168E3E5415767821EAC52C801F77607ECDF1E6819A2DDFACA";

        // worldgen output is signed: the six biome palettes and the steppe/forest split
        // are authored against these exact bytes, and the group rule is that the world
        // is never re-signed. The Unity copy must stay bit-identical.
        private const string SignedColorsSha256 =
            "64BD435C2957A0F83F225BBE8FFAE90734005CFD6AF1EFBEF59F3A9092343804";

        private const string SignedBiomesSha256 =
            "8365607800C79DBEEA09EF3AEBA8D455D5547D7D592A533647ACD4B6EEC132C3";

        private const string SignedMetadataSha256 =
            "CFF4D9E10347F2758F42BEC507D267CEB465126F08F9ECE59AB48682723D87ED";

        private const int ExpectedResolution = 2049;
        private const int ExpectedBiomeCount = 6; // ocean..snow, ids 0..5
        private const string HeightmapFile = "heightmap.f32";
        private const string ColorsFile = "colors.rgba8";
        private const string BiomesFile = "biomes.r8";
        private const string MetaFile = "world.json";

        private static string WorldDirectory => Path.Combine(Application.streamingAssetsPath, "world");

        private static WorldData _world;

        [OneTimeSetUp]
        public void LoadWorldOnce()
        {
            _world = WorldData.Load(WorldDirectory);
        }

        [Test]
        public void StreamingAssetsWorldIsTheSignedWorld()
        {
            // heightmap: the terrain the six palettes are authored against.
            AssertHash(HeightmapFile, SignedHeightmapSha256);
            // colors: the group rule forbids changing it; forest/steppe are separated
            // by Unity recovering moisture FROM these bytes.
            AssertHash(ColorsFile, SignedColorsSha256);
            AssertHash(BiomesFile, SignedBiomesSha256);
            AssertHash(MetaFile, SignedMetadataSha256);
        }

        private static void AssertHash(string file, string expected)
        {
            string path = Path.Combine(WorldDirectory, file);
            Assert.IsTrue(File.Exists(path), $"{file} is missing at {path}");
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                Assert.AreEqual(expected, actual,
                    $"{file} changed: the world was re-signed. The six biomes are authored " +
                    "against these bytes, so regenerate/notify before changing it.");
            }
        }

        [Test]
        public void WorldLoadsWithConsistentArrays()
        {
            Assert.IsNotNull(_world, "WorldData.Load returned null");
            Assert.AreEqual(ExpectedResolution, _world.Resolution, "resolution");

            int vertices = ExpectedResolution * ExpectedResolution;
            Assert.AreEqual(vertices, _world.Heights.Length, "heights");
            Assert.AreEqual(vertices, _world.Colors.Length, "colors");
            Assert.AreEqual(vertices, _world.Biomes.Length, "biomes");

            for (int i = 0; i < _world.Heights.Length; i++)
            {
                float h = _world.Heights[i];
                if (float.IsNaN(h) || float.IsInfinity(h))
                {
                    Assert.Fail($"height[{i}] is {h}; a NaN/Inf vertex breaks mesh and normal building");
                }
            }
        }

        [Test]
        public void MetadataIsCoherentWithTheFiles()
        {
            WorldMetadata meta = _world.Meta;

            Assert.AreEqual(1, meta.version, "metadata version");
            Assert.AreEqual(ExpectedResolution, meta.resolution, "metadata resolution");
            Assert.Greater(meta.sizeMeters, 0f, "sizeMeters");
            Assert.Greater(meta.cellSize, 0f, "cellSize");

            float expectedCell = meta.sizeMeters / (meta.resolution - 1);
            Assert.AreEqual(expectedCell, meta.cellSize, 1e-4f, "cellSize must be size/(res-1)");

            Assert.Less(meta.minHeight, meta.maxHeight, "minHeight < maxHeight");
            Assert.Greater(meta.maxHeight, 300f, "the highest peak is ~396 m; metadata must reflect it");

            long expectedBytes = (long)meta.resolution * meta.resolution;
            Assert.AreEqual(expectedBytes * 4, new FileInfo(Path.Combine(WorldDirectory, HeightmapFile)).Length,
                $"{HeightmapFile} size");
            Assert.AreEqual(expectedBytes * 4, new FileInfo(Path.Combine(WorldDirectory, ColorsFile)).Length,
                $"{ColorsFile} size");
            Assert.AreEqual(expectedBytes, new FileInfo(Path.Combine(WorldDirectory, BiomesFile)).Length,
                $"{BiomesFile} size");
        }

        [Test]
        public void BakedColoursAreOpaque()
        {
            for (int i = 0; i < _world.Colors.Length; i++)
            {
                if (_world.Colors[i].a != 255)
                {
                    Assert.Fail($"colors.rgba8 alpha[{i}] = {_world.Colors[i].a}, expected 255");
                }
            }
        }

        [Test]
        public void BiomeIdsStayInsideTheDocumentedRange()
        {
            for (int i = 0; i < _world.Biomes.Length; i++)
            {
                byte b = _world.Biomes[i];
                if (b >= ExpectedBiomeCount)
                {
                    Assert.Fail($"biomes.r8[{i}] = {b}, outside the documented 0..{ExpectedBiomeCount - 1} range");
                }
            }
        }

        [Test]
        public void SpawnSitsOnTheTerrain()
        {
            Vector3 spawn = _world.Spawn;
            float half = _world.SizeMeters * 0.5f;

            Assert.IsTrue(Mathf.Abs(spawn.x) <= half && Mathf.Abs(spawn.z) <= half,
                $"spawn ({spawn.x},{spawn.z}) is outside the {_world.SizeMeters} m square world");
            Assert.Greater(spawn.y, _world.SeaLevel + 1f, "spawn must be dry land");

            float sampled = _world.HeightAt(spawn.x, spawn.z);
            Assert.AreEqual(spawn.y, sampled, 2f, "spawn.y must match the heightmap");
        }

        [Test]
        public void HeightAtSamplesGridVerticesExactlyAndFallsBackOutsideTheMap()
        {
            float half = _world.SizeMeters * 0.5f;
            float cell = _world.CellSize;
            int res = _world.Resolution;

            int ix = res / 3;
            int iz = res / 4;
            float gridHeight = _world.Heights[iz * res + ix];
            float sampled = _world.HeightAt(-half + ix * cell, -half + iz * cell);
            Assert.AreEqual(gridHeight, sampled, 1e-3f, "a grid vertex must sample its own height");

            float outside = _world.HeightAt(1e6f, 1e6f);
            Assert.AreEqual(_world.SeaLevel - 50f, outside, 1e-3f, "off-map samples must fall back to deep water");
        }

        [Test]
        public void NormalAtPointsUp()
        {
            int res = _world.Resolution;
            Vector3 normal = _world.NormalAt(res / 2, res / 2);
            Assert.Greater(normal.y, 0f, "surface normal must point up");
            Assert.AreEqual(1f, normal.magnitude, 1e-3f, "surface normal must be unit length");
        }

        [Test]
        public void LoadWithoutMetadataThrowsFileNotFound()
        {
            string empty = CreateTempWorldDirectory();
            try
            {
                Assert.Throws<FileNotFoundException>(() => WorldData.Load(empty));
            }
            finally
            {
                Directory.Delete(empty, true);
            }
        }

        [Test]
        public void LoadRejectsTruncatedArrays()
        {
            string dir = CreateTempWorldDirectory();
            try
            {
                File.WriteAllText(Path.Combine(dir, MetaFile), File.ReadAllText(Path.Combine(WorldDirectory, MetaFile)));

                // A heightmap one float short of the advertised resolution.
                int res = ExpectedResolution;
                using (var stream = new FileStream(Path.Combine(dir, HeightmapFile), FileMode.Create, FileAccess.Write))
                {
                    stream.SetLength((long)res * res * 4 - 4);
                }
                Assert.Throws<InvalidDataException>(() => WorldData.Load(dir),
                    "a short heightmap must be rejected, not silently skewed");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static string CreateTempWorldDirectory()
        {
            string dir = Path.Combine(Application.temporaryCachePath,
                "ai003-editmode-world-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
