using UnityEngine;

namespace AI003.Player
{
    /// <summary>
    /// Feeds movement state into the Kenney character animator:
    /// Speed is normalised to the sprint speed, Grounded switches the jump state.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class PlayerAnimation : MonoBehaviour
    {
        public ThirdPersonController Controller;

        private Animator _animator;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        private void Update()
        {
            if (Controller == null || _animator == null)
            {
                return;
            }

            float planarSpeed = Controller.PlanarSpeed;
            float normalized = Mathf.Clamp01(planarSpeed / Mathf.Max(Controller.SprintSpeed, 0.01f));

            _animator.SetFloat("Speed", normalized);
            _animator.SetBool("Grounded", Controller.IsGrounded);

            // Play the run cycle faster when sprinting, slower when walking.
            bool moving = planarSpeed > 0.2f && !Controller.IsSwimming;
            _animator.speed = moving ? Mathf.Clamp(planarSpeed / 6f, 0.6f, 1.5f) : 1f;
        }
    }
}
