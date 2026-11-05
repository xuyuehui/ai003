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
    }
}
