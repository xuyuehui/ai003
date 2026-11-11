using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Three-skid sail-sled. Deliberately small and deterministic: anisotropic
    /// friction (grip sideways, glide forward), a wind thrust along the heading,
    /// and a steering torque that scales with speed. No wheel colliders, so the
    /// behaviour is identical on the 4 m terrain mesh and in a test plane.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class SailSled : MonoBehaviour
    {
        [Header("Motion")]
        [Tooltip("Forward acceleration from the sail, m/s^2 at full wind.")]
        public float windThrust = 9f;

        [Tooltip("Forward drag coefficient (v * k). 0.35 caps the sled near 25 m/s.")]
        public float forwardDrag = 0.35f;

        [Tooltip("Sideways grip: how hard lateral velocity is killed.")]
        public float sideGrip = 3.2f;

        [Tooltip("Yaw rate at full steering input, degrees per second.")]
        public float steerRate = 70f;

        [Header("Ground")]
        public float probeHeight = 1.6f;
        public float probeDistance = 4f;
        public LayerMask groundMask = ~0;

        private Rigidbody _body;
        private float _steer;

        public float Speed => _body != null ? _body.linearVelocity.magnitude : 0f;
        public Rigidbody Body => _body;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // A wind-driven sled must never fall asleep: AddForce on a sleeping
            // body is ignored, which silently kills the demo and the test.
            _body.sleepThreshold = 0f;

            // Skids glide: the collider itself must not add sand friction, or it
            // fights the anisotropic model (and the sled never moves).
            var collider = GetComponent<Collider>();
            if (collider != null && collider.sharedMaterial == null)
            {
                collider.sharedMaterial = new PhysicsMaterial("SailSledSkid")
                {
                    dynamicFriction = 0.02f,
                    staticFriction = 0.02f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            }
        }

        /// <summary>Steering input in [-1, 1]; test code uses this instead of a keyboard.</summary>
        public void SetInput(float steer)
        {
            _steer = Mathf.Clamp(steer, -1f, 1f);
        }

        public bool DebugGrounded { get; private set; }
        public int DebugGroundHits { get; private set; }
        public int DebugFixedTicks { get; private set; }

        private static readonly RaycastHit[] HitBuffer = new RaycastHit[8];

        /// <summary>Ground probe that ignores the sled's own colliders.</summary>
        private bool TryGround(out RaycastHit ground)
        {
            int count = Physics.RaycastNonAlloc(transform.position + Vector3.up * probeHeight,
                Vector3.down, HitBuffer, probeDistance, groundMask, QueryTriggerInteraction.Ignore);
            DebugGroundHits = count;
            for (int i = 0; i < count; i++)
            {
                Collider other = HitBuffer[i].collider;
                if (other != null && !other.transform.IsChildOf(transform))
                {
                    ground = HitBuffer[i];
                    return true;
                }
            }
            ground = default;
            return false;
        }

        private void FixedUpdate()
        {
            Vector3 forward = transform.forward;
            Vector3 velocity = _body.linearVelocity;

            Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 heading = new Vector3(forward.x, 0f, forward.z).normalized;
            Vector3 lateral = flatVelocity - heading * Vector3.Dot(flatVelocity, heading);

            // Skids: hold the line, glide forward.
            _body.AddForce(-lateral * sideGrip, ForceMode.Acceleration);
            _body.AddForce(-heading * Vector3.Dot(flatVelocity, heading) * forwardDrag, ForceMode.Acceleration);

            DebugFixedTicks++;
            DebugGrounded = TryGround(out RaycastHit hit);
            if (!DebugGrounded)
            {
                return;
            }

            _body.AddForce(heading * windThrust, ForceMode.Acceleration);

            // Small downforce keeps the skids planted on crests.
            _body.AddForce(-hit.normal * 1.5f, ForceMode.Acceleration);

            // Steering authority grows with speed, like a sail.
            float speedFactor = Mathf.Clamp01(flatVelocity.magnitude / 8f);
            _body.AddTorque(Vector3.up * (_steer * steerRate * speedFactor), ForceMode.Acceleration);

            // Align the deck to the slope.
            Vector3 targetUp = Vector3.Slerp(Vector3.up, hit.normal, 0.7f);
            Quaternion target = Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, targetUp).normalized, targetUp);
            Quaternion delta = target * Quaternion.Inverse(_body.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
            {
                angle -= 360f;
            }
            if (axis.sqrMagnitude > 0.0001f)
            {
                _body.AddTorque(axis.normalized * (angle * Mathf.Deg2Rad * 6f), ForceMode.Acceleration);
            }
        }
    }
}
