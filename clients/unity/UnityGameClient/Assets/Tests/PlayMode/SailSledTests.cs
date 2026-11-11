using System.Collections;
using AI003.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AI003.Tests
{
    /// <summary>
    /// The sail-sled is the physics showcase: it must accelerate under wind and
    /// keep its line (the skids grip sideways) instead of sliding like a puck.
    /// </summary>
    public class SailSledTests
    {
        [UnityTest]
        public IEnumerator SailSledAcceleratesAndHoldsItsLine()
        {
            // Far outside the 2 km world: the terrain chunks have colliders and
            // would swallow the test plane at the origin.
            Vector3 origin = new Vector3(4000f, 0f, 4000f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "SledGround";
            ground.transform.localScale = new Vector3(40f, 1f, 40f);
            ground.transform.position = origin;

            var sledObject = new GameObject("Sled");
            sledObject.transform.position = origin + new Vector3(0f, 0.9f, 0f);
            var box = sledObject.AddComponent<BoxCollider>();
            box.size = new Vector3(2.4f, 0.8f, 4.2f);
            var body = sledObject.AddComponent<Rigidbody>();
            body.mass = 260f;
            body.linearDamping = 0.02f;
            body.angularDamping = 1.2f;
            var sled = sledObject.AddComponent<SailSled>();

            // Let it drop onto the plane and settle.
            for (int i = 0; i < 60; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            Vector3 start = sledObject.transform.position;
            Assert.Less(Mathf.Abs(start.y), 1.5f, "sled did not settle on the ground");

            // Four seconds of wind, straight ahead.
            float deadline = Time.time + 4f;
            while (Time.time < deadline)
            {
                yield return new WaitForFixedUpdate();
            }

            Vector3 delta = sledObject.transform.position - start;
            float forward = Vector3.Dot(delta, Vector3.forward);
            float lateral = Mathf.Abs(Vector3.Dot(delta, Vector3.right));
            Debug.Log($"[SLEDTEST] grounded={sled.DebugGrounded} hits={sled.DebugGroundHits} " +
                      $"ticks={sled.DebugFixedTicks} speed={sled.Speed:F2} pos={sledObject.transform.position}");

            Assert.Greater(forward, 4f, $"sled only travelled {forward:0.0} m under wind");
            Assert.Less(lateral, Mathf.Max(1.0f, forward * 0.4f),
                $"sled slid sideways {lateral:0.0} m over {forward:0.0} m forward");

            // Steering must turn it: hold right for a second and check the heading changed.
            Vector3 before = sledObject.transform.forward;
            sled.SetInput(1f);
            float steerDeadline = Time.time + 1.5f;
            while (Time.time < steerDeadline)
            {
                yield return new WaitForFixedUpdate();
            }
            sled.SetInput(0f);
            float turned = Vector3.Angle(before, sledObject.transform.forward);
            Assert.Greater(turned, 12f, $"sled only turned {turned:0.0} degrees");

            Object.Destroy(sledObject);
            Object.Destroy(ground);
            yield return null;
        }
    }
}
