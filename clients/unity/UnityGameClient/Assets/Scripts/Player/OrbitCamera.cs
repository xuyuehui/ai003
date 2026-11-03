using UnityEngine;
using UnityEngine.InputSystem;

namespace AI003.Player
{
    /// <summary>Orbit camera with terrain collision; drives the movement direction.</summary>
    public sealed class OrbitCamera : MonoBehaviour
    {
        public Transform Target;
        public float Distance = 6.5f;
        public float PivotHeight = 1.55f;
        public float Sensitivity = 0.12f;
        public float MinPitch = -30f;
        public float MaxPitch = 70f;

        public float Yaw => _yaw;

        private float _yaw = 160f;
        private float _pitch = 16f;
        private bool _cursorInitialised;

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
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 pivot = Target.position + Vector3.up * PivotHeight;
            transform.position = pivot - rotation * Vector3.forward * Distance;
            transform.rotation = rotation;
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

            // Pull the camera in when terrain would occlude the player.
            if (Physics.Linecast(pivot, desired, out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore))
            {
                desired = hit.point + hit.normal * 0.35f;
            }

            float blend = 1f - Mathf.Exp(-14f * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, blend);
            transform.rotation = rotation;
        }

        private void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _cursorInitialised = true;
        }
    }
}
