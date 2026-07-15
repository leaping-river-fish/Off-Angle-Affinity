// =============================================================================
// IMovementState — the contract every movement state must implement.
//
// ═══════════════════════════════════════════════════════════════════════════
// MOVEMENT INTERACTION PHILOSOPHY — READ BEFORE ADDING A NEW STATE
// ═══════════════════════════════════════════════════════════════════════════
//
// This is NOT a collection of independent abilities. It is a chain of
// momentum transforms. Each state is responsible for:
//   (a) Preserving velocity carried in from the previous state, OR
//   (b) Intentionally transforming it, with a documented reason.
//
// CORE RULE:
//   Momentum is the player's reward for skilled movement. Every ability must
//   either GENERATE momentum, TRANSFORM it into a new form, or CONSUME it
//   deliberately. No state may silently zero ctx.Velocity without a clear
//   gameplay reason stated in a comment.
//
// ───────────────────────────────────────────────────────────────────────────
// MUTUALLY EXCLUSIVE PAIRS (enforced by single-active-state machine)
// ───────────────────────────────────────────────────────────────────────────
//   Slide ↔ Crouch        same key; velocity at press decides which fires
//   WallRun ↔ Slide       slide is ground-only; wall run is wall-only
//   Grapple ↔ Zipline     only one attachment point allowed at a time
//   Any ground ↔ Airborne enforced by CharacterController.isGrounded
//
// ───────────────────────────────────────────────────────────────────────────
// CHAINABLE SEQUENCES (momentum MUST be preserved across these)
// ───────────────────────────────────────────────────────────────────────────
//   Sprint → Slide
//     Entry speed = sprint speed. Slide decelerates from there via friction.
//
//   Slide → Jump
//     Full horizontal velocity is preserved + vertical impulse added.
//     Feels like a launch pad. Do not zero XZ in SlidingState.Exit().
//
//   Jump → WallRun
//     AirborneState.FixedTick() detects qualifying wall via raycasts.
//     Auto-enters WallRunningState if speed >= WallRunMinSpeed.
//
//   WallRun → Jump (wall kick)
//     Exit velocity = wall-perpendicular direction × kick speed
//                   + wall-parallel component × preserved run speed
//                   + upward impulse.
//     Wall kick is "free" — does NOT consume RemainingJumps.
//
//   WallRun → DoubleJump
//     Player presses Jump while wall running (without a wall kick).
//     Consumes one RemainingJumps charge. Fires upward + away from wall.
//
//   Grapple swing → Release → WallRun
//     ctx.Velocity at release = swing direction × exit speed.
//     AirborneState detects wall contact and enters WallRunningState.
//
//   Grapple swing → Release → DoubleJump
//     Released at swing apex; double jump fires to extend reach further.
//     Horizontal velocity from swing is preserved.
//
//   Zipline → Jump
//     Jump exit: ctx.Velocity = zipline tangent × zipline speed + upward impulse.
//     Do not zero XZ in ZiplineState.Exit().
//
//   Slide → Grapple fire
//     Grapple cast is allowed during slide. If pull direction matches slide
//     direction, the vectors stack (implement a speed cap to prevent exploit).
//
// ───────────────────────────────────────────────────────────────────────────
// SIMULTANEOUS ABILITIES (not exclusive; handled by separate systems)
// ───────────────────────────────────────────────────────────────────────────
//   Sprint + Grapple fire   movement unchanged until grapple connects
//   Crouch + camera pitch   PlayerCameraController always runs; unaffected
//   Crouch + camera height  CameraCrouchOffset (separate component, Camera
//                           Pivot) reads ctx.CrouchAmount via
//                           MovementStateMachine.CrouchAmount; movement code
//                           never reaches into the camera
//   Any locomotion + weapon Weapon system is entirely separate
//
// ───────────────────────────────────────────────────────────────────────────
// PHYSICS MODIFIERS PER STATE
// ───────────────────────────────────────────────────────────────────────────
//   Grounded    normal gravity (held with -2 press), full XZ input control
//   Crouching   normal gravity, speed capped to CrouchSpeed, capsule height
//               smoothly Lerp'd via CrouchAmount (see MovementCapsuleUtility)
//   Airborne    full gravity, AirControlMultiplier × AirAcceleration
//   Sliding     slope-sensitive gravity, near-zero input influence (friction)
//   WallRunning WallRunGravityScale × gravity, input locked to wall axis
//   Grappling   swing physics override, pendulum motion, minimal direct input
//   Zipline     gravity suppressed, path-locked, no player input
//
// ───────────────────────────────────────────────────────────────────────────
// EDGE CASES TO DESIGN AGAINST (implement mitigations in each state)
// ───────────────────────────────────────────────────────────────────────────
//   Infinite wall run      Per-wall cooldown tag on collider. Must touch ground
//                          to re-enter wall run on the same surface.
//   Slope slide exploit    Apply terminal velocity cap. Uphill decelerates;
//                          downhill accelerates up to cap.
//   Grapple spam           0.2s cooldown between grapple casts (GrapplingState).
//   Double jump cheat      ctx.RemainingJumps must be server-authoritative.
//   Slide off ledge        Preserve horizontal velocity into Airborne. This is
//                          intentional — not an exploit.
//   Grapple through geo    Require line-of-sight raycast before attaching.
//   Wall kick vs double    Wall kick is free; wall run + jump consumes a charge.
//
// ───────────────────────────────────────────────────────────────────────────
// MULTIPLAYER SYNC NOTES
// ───────────────────────────────────────────────────────────────────────────
//   Architecture: client-predictive, server-authoritative (aspirational).
//   Current reality: movement is client-authoritative via NetworkTransform;
//   CrouchingState follows that same model rather than the future ideal below.
//   ctx.Velocity     — primary replicated value; StateId is inferred from it.
//   RemainingJumps   — server-authoritative (anti-cheat critical path).
//   CrouchAmount     — NOT itself replicated. NetworkPlayerCrouch syncs a
//                      single owner-writable SyncVar<bool> (IsCrouching) via
//                      WritePermission.ClientUnsynchronized +
//                      ReadPermission.ExcludeOwner, and every peer locally
//                      re-derives a smooth scale from that bool. Keeps
//                      bandwidth to one RPC per crouch/stand transition
//                      instead of streaming a float every frame.
//   GrapplingState   — requires NetworkVariable<Vector3> for attach point.
//   WallRunningState — server-side surface detection must match client.
//   ZiplineState     — server owns zipline path position; clients interpolate.
//   Input gate       — PlayerInputReader.enabled = IsOwner; disable for remotes.
//
// ═══════════════════════════════════════════════════════════════════════════
// HOW TO ADD A NEW STATE
// ═══════════════════════════════════════════════════════════════════════════
//   1. Add an entry to MovementStateId below (slot may already exist).
//   2. Create MyNewState.cs in Scripts/Movement/States/.
//   3. Register(new MyNewState()) in MovementStateMachine.Initialize().
//   4. Add TransitionTo(MovementStateId.MyNew) in the appropriate source state.
//   No other files need to change.
// =============================================================================

namespace OffAngle.Movement
{
    /// <summary>
    /// All current and future movement states. Add IDs here before implementing
    /// the state class — the enum drives state machine routing.
    /// </summary>
    public enum MovementStateId
    {
        // ── Phase 1: implemented ─────────────────────────────────────────
        Grounded,
        Airborne,
        Crouching,

        // ── Phase 2: enum slots reserved; classes not yet created ────────
        Sliding,

        // ── Phase 3: enum slots reserved ─────────────────────────────────
        WallRunning,
        Grappling,
        Zipline,
    }

    /// <summary>
    /// Contract for all movement states. States are plain C# classes (not
    /// MonoBehaviours) for testability and network portability.
    /// </summary>
    public interface IMovementState
    {
        MovementStateId StateId { get; }

        /// <summary>
        /// Called once when this state becomes active.
        /// Do NOT zero ctx.Velocity here unless explicitly required by gameplay.
        /// </summary>
        void Enter(MovementStateContext ctx);

        /// <summary>
        /// Called every Update frame. Handle input consumption, velocity
        /// accumulation, CharacterController.Move(), and transition checks.
        /// </summary>
        void Tick(MovementStateContext ctx, float deltaTime);

        /// <summary>
        /// Called every FixedUpdate frame. Use for physics queries such as
        /// SphereCasts and surface-detection raycasts. Leave empty if unused.
        /// </summary>
        void FixedTick(MovementStateContext ctx, float fixedDeltaTime);

        /// <summary>Called once when this state is deactivated.</summary>
        void Exit(MovementStateContext ctx);
    }
}
