using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AI003.Ecs
{
    /// <summary>Authoritative-enough state of one fighter inside the ECS world.</summary>
    public struct FighterState : IComponentData
    {
        public float3 Position;
        public float3 TargetPosition;
        public float Yaw;
        public float TargetYaw;
        public float LungeRemaining;
        public float LungeDuration;
        public float HitRemaining;
        public float Collapse;
        public bool Alive;
        public bool Remote;
    }

    /// <summary>Marks the locally controlled fighter (predicted position).</summary>
    public struct LocalFighterTag : IComponentData
    {
    }

    /// <summary>Marks a remotely replicated fighter.</summary>
    public struct RemoteFighterTag : IComponentData
    {
        public int Key;
    }

    /// <summary>Managed presentation handles driven by <see cref="FighterViewSystem"/>.</summary>
    public struct FighterView : IComponentData
    {
        public UnityObjectRef<Transform> Transform;
        public UnityObjectRef<Animator> Animator;
        public UnityObjectRef<Renderer> Renderer;
    }

    /// <summary>Animation intent mirrored from the network layer.</summary>
    public struct FighterAnim : IComponentData
    {
        public float Speed;
        public bool Grounded;
        public float PlayRate;
    }
}
