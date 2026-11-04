using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Visual proxy for another player. The server sends authoritative positions
    /// at 10 Hz, so the transform is smoothed towards the latest snapshot and the
    /// animator is driven by the server-derived animation name.
    ///
    /// Combat feedback (attack lunge, hit flash, death collapse) is triggered from
    /// the room's events, not from the snapshot itself.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RemoteAvatar : MonoBehaviour
    {
        private const float Smoothing = 12f;
        private const float AttackSeconds = 0.18f;
        private const float HitSeconds = 0.15f;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public Vector3 TargetPosition { get; private set; }
        public float TargetYaw { get; private set; }
        public string Anim { get; private set; } = "idle";
        public bool Alive { get; private set; } = true;

        private Animator _animator;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private float _speedParam;
        private float _playRate = 1f;
        private bool _grounded = true;
        private float _attackUntil;
        private float _hitUntil;
        private float _deathAt;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
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

        public void TriggerAttack() => _attackUntil = Time.time + AttackSeconds;

        public void TriggerHit() => _hitUntil = Time.time + HitSeconds;

        public void SetAlive(bool alive)
        {
            if (!alive && Alive) _deathAt = Time.time;
            Alive = alive;
        }

        private void Update()
        {
            float t = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);

            Vector3 target = TargetPosition;
            if (Time.time < _attackUntil)
            {
                float progress = 1f - (_attackUntil - Time.time) / AttackSeconds;
                target += Quaternion.Euler(0f, TargetYaw, 0f) * Vector3.forward
                          * (Mathf.Sin(progress * Mathf.PI) * 0.35f);
            }
            transform.position = Vector3.Lerp(transform.position, target, t);

            if (Alive)
            {
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.Euler(0f, TargetYaw, 0f), t);
            }
            else
            {
                float collapse = Mathf.Clamp01((Time.time - _deathAt) / 0.25f);
                transform.rotation = Quaternion.Slerp(
                    Quaternion.Euler(0f, TargetYaw, 0f),
                    Quaternion.Euler(90f, TargetYaw, 0f),
                    Mathf.SmoothStep(0f, 1f, collapse));
            }

            UpdateColor();

            if (_animator == null) return;
            _animator.SetFloat("Speed", Alive ? _speedParam : 0f);
            _animator.SetBool("Grounded", _grounded);
            _animator.speed = Alive ? _playRate : 0f;
        }

        private void UpdateColor()
        {
            Color color = Color.white;
            if (!Alive)
            {
                color = new Color(0.35f, 0.35f, 0.35f, 1f);
            }
            else if (Time.time < _hitUntil)
            {
                color = new Color(1f, 0.28f, 0.22f, 1f);
            }

            _block.SetColor(BaseColorId, color);
            if (_renderers == null) return;
            foreach (Renderer renderer in _renderers)
            {
                if (renderer != null) renderer.SetPropertyBlock(_block);
            }
        }
    }
}
