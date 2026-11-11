using UnityEngine;
using UnityEngine.InputSystem;

namespace AI003.Player
{
    /// <summary>Orbit camera with terrain collision; drives the movement direction.</summary>
    public sealed class OrbitCamera : MonoBehaviour
    {
        public Transform Target;
        public float Distance = 6.5f;
        /// <summary>Chest height; the camera looks slightly above it so the head stays in frame.</summary>
        public float PivotHeight = 1.5f;
        public float AimOffset = 0.22f;
        public float Sensitivity = 0.12f;
        /// <summary>Keep the orbit from dropping under the ground: that was clipping the head.</summary>
        public float MinPitch = -12f;
        public float MaxPitch = 70f;
        public float MinDistance = 2.2f;

        public float Yaw => _yaw;

        private float _yaw = 160f;
        private float _pitch = 16f;
        private bool _cursorInitialised;

        private void Awake()
        {
            // Adopt the authored rotation so a camera placed in the scene keeps the
            // framing it was saved with instead of snapping to the code defaults.
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), MinPitch, MaxPitch);
        }

        private void Start()
        {
            LockCursor();
            Snap();
        }

        public void Snap()
        {
            if (Target == null)
            {
                return;
            }
            Vector3 pivot = Target.position + Vector3.up * PivotHeight;
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.position = pivot - rotation * Vector3.forward * Distance;
            transform.rotation = Quaternion.LookRotation((pivot + Vector3.up * AimOffset) - transform.position);
        }

        private void LateUpdate()
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (Cursor.lockState == CursorLockMode.Locked)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    _yaw += delta.x * Sensitivity;
                    _pitch -= delta.y * Sensitivity;
                    _pitch = Mathf.Clamp(_pitch, MinPitch, MaxPitch);
                }

                if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
                {
                    LockCursor();
                }
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (Target == null)
            {
                return;
            }

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 pivot = Target.position + Vector3.up * PivotHeight;
            Vector3 desired = pivot - rotation * Vector3.forward * Distance;

            // Pull the camera in when terrain would occlude the player, but never
            // closer than MinDistance — that is what used to cut the head in half.
            if (Physics.Linecast(pivot, desired, out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore))
            {
                Vector3 pulled = hit.point + hit.normal * 0.35f;
                if ((pulled - pivot).magnitude < MinDistance)
                {
                    pulled = pivot + (pulled - pivot).normalized * MinDistance;
                }
                desired = pulled;
            }

            // Never let the orbit sink into a slope: that is what turned "look up"
            // into a wall of dark sand instead of sky.
            if (Physics.Raycast(desired + Vector3.up * 20f, Vector3.down, out RaycastHit ground,
                    40f, ~0, QueryTriggerInteraction.Ignore))
            {
                float floor = ground.point.y + 0.6f;
                if (desired.y < floor)
                {
                    desired.y = floor;
                }
            }

            float blend = 1f - Mathf.Exp(-14f * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, blend);
            Vector3 aim = pivot + Vector3.up * AimOffset;
            transform.rotation = Quaternion.LookRotation(aim - transform.position);
        }

        private void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _cursorInitialised = true;
        }
    }
}
