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

    /// <summary>Integer-millimetre prediction state for the local fighter.</summary>
    public struct PredictionState : IComponentData
    {
        public int2 PositionMm;
        public int FacingX;
        public int FacingZ;
        public int Tick;
        public float Accumulator;
        public int2 CenterMm;
        public int HalfExtentMm;
        public int SpeedMm;
        public int DiagonalMm;
    }

    /// <summary>Latest player command, written by the network bridge each frame.</summary>
    public struct PredictionInput : IComponentData
    {
        public int DX;
        public int DZ;
        public bool Attack;
    }

    /// <summary>Newest authoritative snapshot, applied by the prediction system.</summary>
    public struct ServerSnapshot : IComponentData
    {
        public int2 PositionMm;
        public int Tick;
        public bool Pending;
    }

    /// <summary>One predicted tick; the network layer drains unsent steps.</summary>
    public struct PredictionStep : IBufferElementData
    {
        public int Tick;
        public int DX;
        public int DZ;
        public bool Attack;
        public bool Sent;
    }

    /// <summary>Predicted position per tick, kept for rollback/replay.</summary>
    public struct PredictionHistoryElement : IBufferElementData
    {
        public int Tick;
        public int2 PositionMm;
    }

    /// <summary>Latest authoritative body positions used to predict separation.</summary>
    public struct SeparationBody : IBufferElementData
    {
        public int2 PositionMm;
        public bool Alive;
    }
}
