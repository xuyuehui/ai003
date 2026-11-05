using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Passive presentation shell for a remote fighter. Position, rotation,
    /// animation and hit/death feedback are owned by the ECS systems; this
    /// component only exposes the managed references the bridge needs.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RemoteAvatar : MonoBehaviour
    {
        public Animator Animator { get; private set; }
        public Renderer Renderer { get; private set; }

        private void Awake()
        {
            Animator = GetComponent<Animator>();
            Renderer = GetComponentInChildren<Renderer>();
        }

        public void Configure(Animator animator, Renderer renderer)
        {
            Animator = animator;
            Renderer = renderer;
        }
    }
}
