using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Visual proxy for another player. The server sends authoritative positions
    /// at 10 Hz, so the transform is smoothed towards the latest snapshot and the
    /// animator is driven by the server-derived animation name.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RemoteAvatar : MonoBehaviour
    {
        private const float Smoothing = 12f;

        public Vector3 TargetPosition { get; private set; }
        public float TargetYaw { get; private set; }
        public string Anim { get; private set; } = "idle";

        private Animator _animator;
        private float _speedParam;
        private float _playRate = 1f;
        private bool _grounded = true;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        /// <summary>Stores the newest snapshot values; smoothing happens in Update.</summary>
        public void Apply(Vector3 position, float yaw, string anim)
        {
            TargetPosition = position;
            TargetYaw = yaw;
            Anim = string.IsNullOrEmpty(anim) ? "idle" : anim;

            _grounded = Anim != "jump";
            switch (Anim)
            {
                case "run":
                    _speedParam = 1f;
                    _playRate = 1f;
                    break;
                case "walk":
                    _speedParam = 0.35f;
                    _playRate = 0.7f;
                    break;
                case "swim":
                    _speedParam = 0.3f;
                    _playRate = 0.6f;
                    break;
                case "jump":
                    _speedParam = 0.4f;
                    _playRate = 1f;
                    break;
                default:
                    _speedParam = 0f;
                    _playRate = 1f;
                    break;
            }
        }

        private void Update()
        {
            float t = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, TargetPosition, t);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, Quaternion.Euler(0f, TargetYaw, 0f), t);

            if (_animator == null) return;
            _animator.SetFloat("Speed", _speedParam);
            _animator.SetBool("Grounded", _grounded);
            _animator.speed = _playRate;
        }
    }
}
