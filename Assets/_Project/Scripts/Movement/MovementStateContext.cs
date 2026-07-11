// =============================================================================
// MovementStateContext — the shared data bag for all movement states.
//
// RULES FOR ALL CONTRIBUTORS:
//   1. ctx.Velocity is carried into every state's Enter() unmodified.
//      Never zero Velocity on Enter() unless that is an explicit gameplay
//      decision (e.g. ZiplineState overrides locomotion with path velocity).
//
//   2. States do NOT reference PlayerController, sibling MonoBehaviours, or
//      anything outside this context. If a state needs new data, add a field
//      here and initialize it in PlayerController.Awake().
//
//   3. Pending flags (JumpPending, CrouchSlidePending) are set by the
//      MovementStateMachine's input event subscriptions and consumed once per
//      Tick by the active state. Do not reset them in Enter() or Exit().
//
// MOMENTUM CHAIN REFERENCE (see IMovementState.cs for full details):
//   Sprint → Slide  : entry speed = sprint speed; slide decelerates from there
//   Slide  → Jump   : horizontal velocity preserved + vertical impulse
//   Jump   → WallRun: auto-enter on qualifying wall contact at speed threshold
//   WallRun→ Jump   : perpendicular wall kick + preserve wall-parallel speed
//   Grapple→ Release: ctx.Velocity = swing direction × exit speed at moment
//   Zipline→ Jump   : ctx.Velocity = zipline tangent × zipline speed at jump
//   DoubleJump      : overrides ctx.Velocity.y only; horizontal unchanged
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement
{
    public class MovementStateContext
    {
        // ------------------------------------------------------------------
        // Immutable references — set once in PlayerController.Awake()
        // ------------------------------------------------------------------

        public CharacterController         Controller      { get; set; }
        public OffAngle.Core.PlayerInputReader Input        { get; set; }
        public Transform                   PlayerTransform { get; set; }
        public MovementStateMachine        StateMachine    { get; set; }
        public MovementSettings            Settings        { get; set; }

        // ------------------------------------------------------------------
        // Mutable frame data — states read and write freely each Tick
        // ------------------------------------------------------------------

        /// <summary>
        /// The player's current velocity in world space.
        /// States own this value: they read it, update it, and pass it to
        /// CharacterController.Move(). Never silently zero it on state entry.
        /// </summary>
        public Vector3 Velocity;

        /// <summary>
        /// Set true by MovementStateMachine when JumpStarted fires.
        /// The active state consumes it (sets false) once per Tick.
        /// </summary>
        public bool JumpPending;

        /// <summary>
        /// Set true by MovementStateMachine when CrouchSlideStarted fires.
        /// GroundedState reads this and decides: Sliding vs Crouching.
        /// </summary>
        public bool CrouchSlidePending;

        /// <summary>
        /// Tracks whether the CrouchSlide key is currently held.
        /// Set false by MovementStateMachine when CrouchSlideCanceled fires.
        /// Used by future CrouchingState and SlidingState for exit conditions.
        /// </summary>
        public bool IsCrouchSlideHeld;

        /// <summary>
        /// Remaining jumps in the current airborne bout.
        /// Phase 1: MaxJumps = 1, so this is always 0 while airborne.
        /// Phase 2: set MaxJumps = 2 in MovementSettings to unlock double jump.
        /// Must be server-authoritative in multiplayer to prevent cheat injection.
        /// </summary>
        public int RemainingJumps;
    }

    // World scale reference: 1 Unity unit = 1 meter. Defaults below match the
    // project baseline in Assets/_Project/Docs/PlayerScaleReference.md — treat
    // that doc as the source of truth if these ever drift apart.
    [System.Serializable]
    public class MovementSettings
    {
        [Header("Ground Movement")]
        [Tooltip("Walk speed in meters/second.")]
        public float WalkSpeed   = 4.5f;
        [Tooltip("Sprint speed in meters/second.")]
        public float SprintSpeed = 7f;
        public float CrouchSpeed = 2.5f;   // PHASE 2: used by CrouchingState

        [Header("Jumping")]
        [Tooltip("Apex height in meters. Drives jump velocity via v = sqrt(2 * h * g).")]
        public float JumpHeight  = 0.9f;
        [Tooltip("Downward acceleration in m/s². Increase for snappier feel.")]
        public float Gravity     = 20f;
        [Tooltip("1 = single jump. Set to 2 to enable double jump (Phase 2).")]
        public int   MaxJumps    = 1;

        [Header("Air Movement")]
        [Range(0f, 1f)]
        [Tooltip("Scales how strongly player input steers while airborne.")]
        public float AirControlMultiplier = 0.35f;
        [Tooltip("Horizontal acceleration rate (m/s²) applied toward wish direction in air.")]
        public float AirAcceleration      = 15f;

        [Header("Slide / Crouch")]
        [Tooltip("Horizontal speed (m/s) required at Left Ctrl press to enter Slide instead of Crouch. Tune in playtesting.")]
        public float SlideEntrySpeedThreshold = 4f;
        public float SlideDeceleration        = 5f;   // PHASE 2: m/s² friction during slide
        public float SlideDuration            = 1.2f; // PHASE 2: max seconds in slide

        [Header("Wall Run")]
        [Tooltip("Fraction of normal gravity applied during wall run (0 = floaty, 1 = full fall).")]
        public float WallRunGravityScale = 0.2f;      // PHASE 3
        public float WallRunMinSpeed     = 3f;         // PHASE 3: exit if speed drops below this
        public float WallRunDuration     = 2f;         // PHASE 3: max seconds per wall surface
    }
}
