using System.Collections;
using AI003.Ecs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    /// <summary>
    /// Verifies that the ECS bridge creates a fighter entity and that
    /// FighterViewSystem writes the entity position back to the Transform.
    /// </summary>
    public sealed class FighterEcsTests
    {
        [UnityTest]
        public IEnumerator LocalEntityDrivesTransform()
        {
            var target = new GameObject("[test] ecs target");
            var bridge = RoomEcsBridge.EnsureExists();
            bridge.Clear();

            try
            {
                bridge.EnsureLocal(target.transform, null, null);
                bridge.SetLocal(new Vector3(3f, 1.5f, 4f), 90f, 0f, true);

                yield return null;
                yield return null;

                Assert.AreEqual(3f, target.transform.position.x, 0.01f, "x");
                Assert.AreEqual(1.5f, target.transform.position.y, 0.01f, "y");
                Assert.AreEqual(4f, target.transform.position.z, 0.01f, "z");
                Assert.AreEqual(90f, target.transform.eulerAngles.y, 0.5f, "yaw");
            }
            finally
            {
                bridge.Clear();
                Object.Destroy(target);
            }
        }
        [UnityTest]
        public IEnumerator LocalPredictionStepsInMillimetres()
        {
            var target = new GameObject("[test] prediction target");
            var bridge = RoomEcsBridge.EnsureExists();
            bridge.Clear();

            try
            {
                bridge.EnsureLocal(target.transform, null, null);
                bridge.ConfigureLocal(0, 0, 20000, 250, 177);
                bridge.SetServerSnapshot(0, 0, 100);
                bridge.SetInput(1, 0, false);

                float started = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - started < 0.45f)
                {
                    yield return null;
                }

                Vector3 position = bridge.GetLocalPositionMeters();
                Assert.GreaterOrEqual(position.x, 0.5f, "predicted x should advance");
                Assert.LessOrEqual(position.x, 1.0f, "prediction should stop 3 ticks ahead");
                Assert.AreEqual(0f, position.z, 0.001f, "no drift on z");
                Assert.Greater(bridge.LocalTick, 100, "prediction tick should advance");
            }
            finally
            {
                bridge.Clear();
                Object.Destroy(target);
            }
        }
        [UnityTest]
        public IEnumerator RemoteSmoothingJobBeatsManagedLoop()
        {
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            var bench = world.CreateSystemManaged<AI003.Ecs.FighterBenchmarkSystem>();
            world.GetExistingSystemManaged<Unity.Entities.SimulationSystemGroup>().AddSystemToUpdateList(bench);
            bench.EntityCount = 4000;
            bench.Iterations = 10;

            try
            {
                float started = Time.realtimeSinceStartup;
                while (!bench.Done && Time.realtimeSinceStartup - started < 10f)
                {
                    yield return null;
                }

                Debug.Log($"[AI003][ECS] smoothing 4000 remote fighters: job {bench.JobMs:F3} ms, managed {bench.ManagedMs:F3} ms");
                Assert.IsTrue(bench.Done, "benchmark did not finish");
                Assert.Greater(bench.JobMs, 0.0, "job time should be measured");
                Assert.Less(bench.JobMs, bench.ManagedMs, "Burst job should beat the managed loop");
            }
            finally
            {
                bench.Enabled = false;
            }
        }
    }
}