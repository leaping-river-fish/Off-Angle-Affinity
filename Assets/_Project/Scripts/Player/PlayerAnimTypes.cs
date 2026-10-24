// =============================================================================
// PlayerAnimTypes — shared data types for the third-person animation pipeline.
//
//   PlayerAnimFlags         persistent visual state bits (networked later as one byte)
//   PlayerAnimSnapshot      one coherent per-frame view of the player for the Animator writer
//   IPlayerAnimStateSource  FishNet-free seam so the animator can read networked flags
//
// Presentation only. Nothing here decides gameplay state.
// =============================================================================

using System;

namespace OffAngle.Player
{
    [Flags]
    public enum PlayerAnimFlags : byte
    {
        None          = 0,
        Crouched      = 1 << 1,
        Airborne      = 1 << 2,
        Sliding       = 1 << 3,
        WallRunning   = 1 << 4,
        AbilityMoving = 1 << 5,
    }

    /// <summary>Plain data, rebuilt every frame by ThirdPersonLocomotionAnimator.</summary>
    [Serializable]
    public struct PlayerAnimSnapshot
    {
        public float MoveX, MoveY;
        public float PlanarSpeed, VerticalSpeed;
        /// <summary>Locomotion playback-speed multiplier (1 = walk, ~1.43 = sprint).</summary>
        public float LocomotionSpeed;
        public bool IsCrouching, IsGrounded, IsAirborne;
        public bool IsSliding, IsWallRunning, IsAbilityMoving;
    }

    /// <summary>
    /// Implemented by the network flags component so the animator never
    /// references FishNet. Returns the current flags on every peer (composed
    /// locally on the owner, received on remotes). Returns false only before
    /// the client has started.
    /// </summary>
    public interface IPlayerAnimStateSource
    {
        bool TryGetFlags(out PlayerAnimFlags flags);
    }
}
