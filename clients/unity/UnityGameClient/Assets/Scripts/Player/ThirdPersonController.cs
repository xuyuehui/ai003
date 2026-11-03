using UnityEngine;
using UnityEngine.InputSystem;
using AI003.World;

namespace AI003.Player
{
    /// <summary>
    /// Third person character: gravity, terrain collision, slope limits, jump
    /// and a simple buoyancy mode when the water is deep enough to swim in.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class ThirdPersonController : MonoBehaviour
    {
        public Transform CameraTransform;
        public WorldData World;
        public float SeaLevel;

        public float WalkSpeed = 6f;
        public float SprintSpeed = 10f;
        public float SwimSpeed = 3.2f;
        public float JumpHeight = 1.35f;
        public float Gravity = -24f;
        public float TurnSpeed = 14f;

        public bool IsGrounded { get; private set; }
        public bool IsSwimming { get; private set; }
        public float CurrentSpeed => IsSwimming ? SwimSpeed : (IsSprinting ? SprintSpeed : WalkSpeed);
        public bool IsSprinting { get; private set; }

        /// <summary>Actual horizontal speed from the character controller (m/s).</summary>
        public float PlanarSpeed
        {
            get
            {
                if (_controller == null) return 0f;
                Vector3 velocity = _controller.velocity;
                return new Vector2(velocity.x, velocity.z).magnitude;
            }
        }

        /// <summary>Test seam: when true, <see cref="ExternalInput"/> replaces the keyboard.</summary>
        public bool UseExternalInput { get; set; }
        public Vector2 ExternalInput { get; set; }

        private CharacterController _controller;
        private Vector3 _verticalVelocity;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            Vector2 input = ReadInput();
            var keyboard = Keyboard.current;
            IsSprinting = keyboard != null && keyboard.leftShiftKey.isPressed;
            bool jumpPressed = keyboard != null && keyboard.spaceKey.wasPressedThisFrame;

            UpdateWaterState();

            Vector3 forward = Vector3.forward;
            Vector3 right = Vector3.right;
            if (CameraTransform != null)
            {
                forward = Vector3.ProjectOnPlane(CameraTransform.forward, Vector3.up).normalized;
                right = Vector3.ProjectOnPlane(CameraTransform.right, Vector3.up).normalized;
            }

            Vector3 wish = forward * input.y + right * input.x;
            if (wish.sqrMagnitude > 1f)
            {
                wish.Normalize();
            }

            float speed = IsSwimming ? SwimSpeed : (IsSprinting ? SprintSpeed : WalkSpeed);

            if (IsSwimming)
            {
                // Buoyancy spring towards the surface, damped so it settles.
                float targetY = SeaLevel - 0.85f;
                _verticalVelocity.y += (targetY - transform.position.y) * 24f * dt;
                _verticalVelocity.y *= Mathf.Exp(-5f * dt);
                if (jumpPressed)
                {
                    _verticalVelocity.y = 4.5f;
                }
            }
            else
            {
                if (IsGrounded && _verticalVelocity.y < 0f)
                {
                    _verticalVelocity.y = -2f;
                }
                if (jumpPressed && IsGrounded)
                {
                    _verticalVelocity.y = Mathf.Sqrt(2f * JumpHeight * -Gravity);
                }
                _verticalVelocity.y += Gravity * dt;
            }

            Vector3 motion = wish * speed + Vector3.up * _verticalVelocity.y;
            _controller.Move(motion * dt);
            IsGrounded = _controller.isGrounded;

            if (wish.sqrMagnitude > 0.0001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(wish, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, 1f - Mathf.Exp(-TurnSpeed * dt));
            }
        }

        private void UpdateWaterState()
        {
            if (World == null)
            {
                return;
            }

            float ground = World.HeightAt(transform.position.x, transform.position.z);
            float depth = SeaLevel - ground;
            float feet = transform.position.y;

            if (depth > 1.6f && feet < SeaLevel + 0.4f)
            {
                IsSwimming = true;
            }
            else if (depth <= 1.6f || feet > SeaLevel + 0.6f)
            {
                IsSwimming = false;
            }
        }

        private Vector2 ReadInput()
        {
            if (UseExternalInput)
            {
                return ExternalInput;
            }

            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return Vector2.zero;
            }

            float x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            float y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
            return new Vector2(x, y);
        }
    }
}
