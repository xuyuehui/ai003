using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Passive presentation shell for a remote fighter. Position, rotation,
    /// animation and hit/death feedback are owned by the ECS systems; this
    /// component only exposes the managed references the bridge needs.
    ///
    /// The visual model is instantiated as a child with the same 180-degree
    /// offset the local player uses (the Kenney rig faces -Z), so ECS yaw can be
    /// applied to the wrapper root and the avatar faces its movement direction.
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

        /// <summary>Builds a remote avatar wrapper with a correctly oriented model.</summary>
        public static RemoteAvatar Create(GameObject prefab, Vector3 position, float yaw, string name)
        {
            var wrapper = new GameObject(name);
            wrapper.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

            GameObject model = Instantiate(prefab, wrapper.transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            model.transform.localScale = Vector3.one * 0.45f;

            foreach (var collider in model.GetComponentsInChildren<Collider>())
            {
                Destroy(collider);
            }

            var avatar = wrapper.AddComponent<RemoteAvatar>();
            avatar.Configure(model.GetComponent<Animator>(), model.GetComponentInChildren<Renderer>());
            return avatar;
        }
    }
}
