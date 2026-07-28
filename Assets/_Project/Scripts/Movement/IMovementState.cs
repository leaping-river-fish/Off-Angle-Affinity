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
//   Grapple pull → Release → WallRun
//     ctx.Velocity at the moment of release (natural arrival OR an early
//     slingshot cancel via MovementStateMachine.InterruptCurrentAction) is
//     whatever GrapplePullDriver had accelerated it to - see
//     GrapplePullDriver.cs's MOMENTUM CONTRACT. AirborneState detects wall
//     contact and enters WallRunningState from there, same as any other
//     airborne momentum.
//
//   Grapple pull → Release → DoubleJump
//     Released early (slingshot cancel) or on arrival; double jump fires to
//     extend reach further. Horizontal velocity from the pull is preserved
//     exactly like a slide-jump.
//
//   Zipline → Jump
//     Jump exit: ctx.Velocity = zipline tangent × zipline speed + upward impulse.
//     Do not zero XZ in ZiplineState.Exit().
//
//   Slide → Grapple fire
//     Grapple cast is allowed during slide by default (gated by
//     MovementStateMachine.CanStartMovementAction(), which reads
//     Settings.AllowAbilitiesDuringSlide - same seam every future ability
//     uses, see PlayerGrapple.HandleGrappleStarted). If pull direction
//     matches slide direction, the vectors stack; GrapplePullDriver's
//     GrappleMaxPullSpeed is the speed cap that prevents that from
//     runaway-exploiting.
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
//   Airborne    full gravity × GravityMultiplier, AirControlMultiplier ×
//               AirAcceleration × SpeedMultiplier
//   Sliding     flat ground holds speed CONSTANT (no ambient friction);
//               slope adds/removes speed via SlideSlopeAcceleration
//               (downhill assists, uphill resists); steering input nudges
//               direction without killing speed
//   AbilityMovement fully delegated to the active IAbilityMovementDriver -
//               no built-in gravity/friction/input model of its own
//   WallRunning WallRunGravityScale × gravity, input locked to wall axis
//   Grappling   implemented as GrapplePullDriver (Scripts/Movement/Grapple/)
//               driven through AbilityMovement, not a dedicated state -
//               straight-line acceleration toward the anchor point
//               (GrapplePullAcceleration, capped at GrappleMaxPullSpeed),
//               partial gravity via GrappleGravityScale (0 = zipline-flat,
//               1 = full arc), no direct player input during the pull
//               (steering while grappling is a documented future extension,
//               not built - see GrapplePullDriver.cs)
//   Zipline     gravity suppressed, path-locked, no player input
//
//   GravityMultiplier / SpeedMultiplier / InputLocked (on MovementStateContext,
//   set via MovementStateMachine.SetGravityMultiplier/SetSpeedMultiplier/
//   SetInputLocked) are reusable hooks every Phase-1/2 state already respects -
//   future abilities can lean on them instead of each reimplementing a
//   temporary gravity/speed/input override.
//
// ───────────────────────────────────────────────────────────────────────────
// EDGE CASES TO DESIGN AGAINST (implement mitigations in each state)
// ───────────────────────────────────────────────────────────────────────────
//   Infinite wall run      Per-wall cooldown tag on collider. Must touch ground
//                          to re-enter wall run on the same surface.
//   Slope slide exploit    Apply terminal velocity cap. Uphill decelerates;
//                          downhill accelerates up to cap.
//   Grapple spam           Owner-local + server-revalidated cooldown between
//                          casts (PlayerGrapple's _cooldown/_serverCooldownGrace,
//                          same shape as PlayerWeaponController's fire-rate
//                          gate) - no dedicated GrapplingState needed.
//   Double jump cheat      ctx.RemainingJumps must be server-authoritative.
//   Slide off ledge        Preserve horizontal velocity into Airborne. This is
//                          intentional — not an exploit.
//   Grapple through geo    Structurally impossible rather than a raycast
//                          check: GrappleHook only reports a hit from its own
//                          OnTriggerEnter, i.e. only once the hook has
//                          physically flown to and touched the surface (see
//                          GrappleHook.cs). GrapplePullDriver additionally
//                          auto-releases early if a raycast toward the
//                          anchor finds something in the way mid-pull
//                          (GrappleAutoReleaseOnObstruction), so the player
//                          can't grind through/into geometry either.
//   Wall kick vs double    Wall kick is free; wall run + jump consumes a charge.
//   Bunny hop momentum     INTENTIONAL, not an exploit: GroundedState/
//                          CrouchingState preserve speed above the walk/
//                          sprint/crouch cap instead of clamping it away (see
//                          GroundMomentum.cs) and Airborne applies no speed
//                          decay at all - only BunnyHopMomentumDecay bleeds
//                          it off, and only while grounded. Chaining jumps is
//                          deliberately the strictly better choice once above
//                          normal speed.
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
//   Grapple          — implemented (GrapplePullDriver, driven through
//                      AbilityMovement - no dedicated state). The hook's
//                      flight/collision is server-authoritative
//                      (Scripts/Movement/Grapple/GrappleHook.cs, spawned by
//                      OffAngle.Networking.PlayerGrapple); the resolved
//                      anchor point is delivered to the OWNER ONLY via a
//                      TargetRpc (other peers don't run this player's
//                      MovementStateMachine at all, see
//                      NetworkPlayerController), and the pull itself then
//                      follows the same client-authoritative model as every
//                      other movement state.
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

        // ── Phase 2: implemented ─────────────────────────────────────────
        Sliding,

        /// <summary>
        /// Generic host state for any IAbilityMovementDriver (dash, wall run,
        /// grapple, blink, altered gravity, Affinity-specific movement, ...).
        /// The state itself has no gameplay logic - see IAbilityMovementDriver.cs
        /// and MovementStateMachine.BeginAbilityMovement().
        /// </summary>
        AbilityMovement,

        // ── Phase 3: enum slots reserved ─────────────────────────────────
        // These will likely be implemented as IAbilityMovementDriver classes
        // driven through AbilityMovement rather than as dedicated
        // MovementStateId entries - kept here as placeholders until that
        // decision is made concrete.
        WallRunning,

        /// <summary>
        /// UNUSED placeholder - Grapple is implemented as GrapplePullDriver
        /// (Scripts/Movement/Grapple/GrapplePullDriver.cs) driven through
        /// AbilityMovement, exactly the pattern predicted above. During an
        /// active grapple, MovementStateMachine.CurrentStateId reports
        /// AbilityMovement, never this value - query
        /// OffAngle.Networking.PlayerGrapple.IsGrappling instead if a system
        /// specifically needs to know "is the player grappling right now".
        /// Kept here only so a future dedicated GrapplingState remains a
        /// non-breaking option if the pull-based design ever needs one.
        /// </summary>
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
