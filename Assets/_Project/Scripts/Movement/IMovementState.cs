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
//     Horizontal speed is raised to at least SlideMaxSpeed on entry (the
//     sprint-into-slide boost). Faster entries (grapple, downhill,
//     leftover momentum) keep their speed; never snapped down to
//     SlideMaxSpeed. Flat/uphill then decelerate from that speed;
//     downhill accelerates from it.
//
//   Slide → Jump
//     Full horizontal velocity is preserved + vertical impulse added.
//     Feels like a launch pad. Do not zero XZ in SlidingState.Exit().
//
//   Jump → WallRun
//     AirborneState.FixedTick() detects qualifying wall via WallDetection.
//     Auto-enters WallRunningState if wall-tangential speed >= WallRunEntrySpeed
//     (and same-wall reattach cooldown is not blocking).
//
//   WallRun → Jump (wall kick)
//     Exit velocity = wall-perpendicular direction × kick speed
//                   + wall-parallel component × preserved run speed
//                   + upward impulse.
//     Wall kick is "free" — does NOT consume RemainingJumps.
//     Entering WallRunningState refreshes RemainingJumps to at least
//     EffectiveMaxJumps - 1 so this kick can be followed by a redirect
//     double jump (see WallRunningState.Enter).
//
//   WallRun → DoubleJump
//     After a wall kick (or any soft exit into Airborne), JumpPending
//     while airborne consumes one RemainingJumps charge as usual.
//
//   Grapple pull → Release (cancel/timeout/obstruction) → WallRun
//     ctx.Velocity at the moment of release (an early slingshot cancel via
//     MovementStateMachine.InterruptCurrentAction, a timeout, or an
//     obstruction cutoff - NOT a real arrival, see GrapplePullExitReason) is
//     whatever GrapplePullDriver last snapped it to - see
//     GrapplePullDriver.cs's MOMENTUM CONTRACT. AirborneState detects wall
//     contact and enters WallRunningState from there, same as any other
//     airborne momentum.
//
//   Grapple pull → Release (cancel/timeout/obstruction) → DoubleJump
//     Released early (slingshot cancel), timed out, or obstructed; double
//     jump fires to extend reach further. Horizontal velocity from the pull
//     is preserved exactly like a slide-jump.
//
//   Grapple pull → Arrived at a WALL/CEILING → WallHold → Release
//     A pull that actually reaches GrappleReleaseDistance of a WALL/CEILING
//     anchor (GrapplePullExitReason.Arrived, surface normal mostly
//     horizontal) does NOT release immediately - it begins
//     GrappleWallHoldDriver, which zeroes ctx.Velocity and freezes the
//     player in place for GrappleWallHoldDuration seconds (look/aim/fire
//     still work; only movement is locked). Pressing Jump during the hold
//     ends it immediately with a normal jump impulse (a wall-jump-style
//     cancel); otherwise the hold releases into Airborne/Grounded with
//     ctx.Velocity left at zero once the duration elapses. See
//     GrappleWallHoldDriver.cs and PlayerGrapple.HandlePullExited/
//     HandleHoldExited for the full Pulling → Holding → Idle chain.
//
//   Grapple pull → Arrived at the GROUND → Release (launch forward)
//     An arrival at a GROUND anchor (surface normal mostly vertical, see
//     PlayerGrapple's _groundAnchorNormalThreshold) skips GrappleWallHoldDriver
//     entirely - GrapplePullDriver never touches ctx.Velocity on exit, so
//     releasing immediately (exactly like a manual slingshot cancel) carries
//     the pull's full tow speed/direction straight into Airborne/Grounded.
//     This is what makes "hook the ground, release, launch forward" work -
//     see PlayerGrapple.cs's GROUND ANCHORS header note.
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
//   Grounded    normal gravity (held with -2 press), full XZ input control,
//               walk/sprint speed cap instantly scaled by slope steepness +
//               heading via SlopeUtility.GetSlopeSpeedMultiplier (uphill
//               slows down, downhill speeds up - see
//               MovementSettings.SlopeUphillSpeedMultiplier/
//               SlopeDownhillSpeedMultiplier)
//   Crouching   normal gravity, speed capped to CrouchSpeed (same slope
//               scaling as Grounded), capsule height smoothly Lerp'd via
//               CrouchAmount (see MovementCapsuleUtility)
//   Airborne    full gravity × GravityMultiplier, AirControlMultiplier ×
//               AirAcceleration × SpeedMultiplier
//   Sliding     flat ground decelerates at SlideFlatDeceleration; sliding
//               meaningfully downhill accelerates continuously up to
//               MaxPreservedSpeed (not capped at SlideMaxSpeed - see
//               SlidingState's SPEED MODEL note); uphill stacks the same
//               SlideSlopeAcceleration resistance on top of that flat drag;
//               the slide lasts at least SlideDuration then continues while
//               speed stays >= SlideMinSpeed; steering input nudges direction
//               without raising speed; move vector is solved to stay tangent
//               to the slope (see SlidingState's GROUND CONTACT note) and any
//               tiny leftover gap is folded into that same Move() call via
//               SlopeUtility.MoveWithGroundSnap (MovementSettings.
//               GroundSnapDistance) - together these are what prevent a fast
//               downhill slide from bouncing between Sliding and Airborne
//               every frame or two
//   AbilityMovement fully delegated to the active IAbilityMovementDriver -
//               no built-in gravity/friction/input model of its own
//   WallRunning WallRunGravityScale × gravity (or WallVerticalMoveSpeed ×
//               ctx.WallVerticalInput when a future Affinity perk drives
//               vertical wall movement), locomotion locked to the wall-
//               tangent axis, free wall-kick on Jump (does not consume
//               RemainingJumps). Entered only from AirborneState.FixedTick
//               via WallDetection - see WallRunningState.cs
//   Grappling   implemented as GrapplePullDriver + GrappleWallHoldDriver
//               (Scripts/Movement/Grapple/) driven through AbilityMovement,
//               not a dedicated state. The pull instantly snaps to
//               GrappleMaxPullSpeed toward the anchor point every Tick (no
//               ramp-up), with partial gravity via GrappleGravityScale
//               (0 = zipline-flat, 1 = full arc); no direct player input
//               during the pull (steering while grappling is a documented
//               future extension, not built - see GrapplePullDriver.cs). On
//               a real arrival, GrappleWallHoldDriver takes over: ctx.Velocity
//               is zeroed and the player is frozen at the wall (look/aim/fire
//               still work) for GrappleWallHoldDuration seconds, or until
//               Jump is pressed (an immediate wall-jump-style cancel) - see
//               GrappleWallHoldDriver.cs
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
//   Infinite wall run      Per-wall WallRunDuration timer keyed to collider
//                          identity (WallDetection.IsSameWall). Same-wall
//                          reattach cooldown after exit. Duration resets only
//                          on a genuinely different wall.
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
//   Wall kick vs double    Wall kick is free; entering a wall run refreshes
//                          the air-jump budget so a redirect double jump
//                          after the kick is always available.
//   Bunny hop momentum     INTENTIONAL, not an exploit: AirborneState always
//                          preserves speed above MovementSettings.NormalMaxSpeed
//                          (the global momentum threshold - see
//                          GroundMomentum.cs) instead of clamping it away.
//                          GroundedState/CrouchingState do too, but ONLY
//                          within a short grace window after landing
//                          (Settings.GroundMomentumGraceDuration, stamped by
//                          GroundMomentum.OnLanded - see AirborneState.Tick()'s
//                          landing block) or while ctx.OnIcyGround is true
//                          (see MovementStateContext.OnIcyGround) - outside
//                          both, ground momentum HARD-STOPS instantly instead
//                          of decaying (see "Ground momentum outliving its
//                          grace" below). A jump pressed the same Tick as
//                          landing never even reaches the ground decay math
//                          (PerformJump only overrides Velocity.y), so
//                          precisely-timed hopping preserves speed almost
//                          losslessly; hopping a little late still preserves
//                          it via the grace window's gradual decay; waiting
//                          too long snaps straight back to normal. This is
//                          what makes chaining jumps the deliberate,
//                          rewarded way to sustain speed instead of just
//                          walking, while bounding worst-case speed growth
//                          (see "Momentum steering never gains speed" below).
//   Momentum steering      Steering (Settings.GroundMomentumSteerAcceleration /
//   never gains speed      AirMomentumSteerAcceleration) only ever changes
//                          the momentum vector's DIRECTION, never its
//                          magnitude - see GroundMomentum.ApplyMomentumDecay's
//                          DIRECTION VS MAGNITUDE note. An earlier version
//                          added the steer vector directly onto velocity and
//                          decayed the resulting (larger) magnitude, which
//                          let holding a direction close to current travel
//                          gain speed every Tick whenever steerAccel exceeded
//                          decayRate (true in the air) - compounding without
//                          bound across a grapple-into-bunny-hop chain and
//                          letting a player go arbitrarily fast if the map
//                          allowed enough jumps. Speed out of the momentum
//                          system can now only hold or decay, never increase.
//   Ground momentum        Outside the bunny-hop grace window (and not on
//   outliving its grace    ice), GroundedState/CrouchingState treat excess
//                          ground momentum exactly like normal walk/sprint
//                          input - an instant snap to the normal cap, or to
//                          zero with no input - rather than gradually
//                          decaying it. This is the "player should stop
//                          fully on the ground" behavior: gradual decay is
//                          reserved for the deliberate skill window right
//                          after landing (or for ice), not for casually
//                          walking around at grapple/slide speed.
//   Momentum cancelled     Below MovementSettings.NormalMaxSpeed, holding
//   by normal input        movement input still directly sets velocity to
//                          the walk/sprint/crouch cap (unchanged, fully
//                          responsive feel). Above that threshold and within
//                          momentum-preserving conditions (always true in
//                          air; grace window or ice on the ground), input
//                          only STEERS the momentum vector's direction via
//                          acceleration - it never replaces velocity
//                          outright, so holding a direction that happens to
//                          roughly match the player's current heading (the
//                          overwhelmingly common case right after a grapple
//                          release or slide-jump) can no longer instantly
//                          snap speed back down to normal.
//   Momentum on landing    Landing (or releasing input) at or below
//   with no input          NormalMaxSpeed still stops instantly - normal
//                          feel, unchanged. Bonus momentum above that
//                          threshold decays smoothly toward NormalMaxSpeed
//                          (never zeroing in one frame, identically whether
//                          or not input is held) for as long as momentum-
//                          preserving conditions hold - always true in the
//                          air (AirMomentumDecay), or on the ground within
//                          the bunny-hop grace window/on ice
//                          (GroundMomentumDecay) - so touching ground (or
//                          releasing input mid-air) never erases the reward
//                          instantly. Once grounded AND past the grace
//                          window (and not on ice), it instead hard-stops -
//                          see "Ground momentum outliving its grace" above.
//   Future ice terrain     ctx.OnIcyGround (MovementStateContext) is a
//                          reserved hook nothing sets yet. Once a future
//                          terrain-detection system sets it true while
//                          standing on an icy surface,
//                          GroundMomentum.ShouldPreserveGroundMomentum()
//                          treats the player as permanently within the
//                          grace window - ice automatically and continuously
//                          preserves ground momentum, with no other change
//                          needed to GroundedState/CrouchingState/GroundMomentum.
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
//   Grapple          — implemented (GrapplePullDriver + GrappleWallHoldDriver,
//                      driven through AbilityMovement - no dedicated state).
//                      The hook's flight/collision is server-authoritative
//                      (Scripts/Movement/Grapple/GrappleHook.cs, spawned by
//                      OffAngle.Networking.PlayerGrapple); the resolved
//                      anchor point is delivered to the OWNER ONLY via a
//                      TargetRpc (other peers don't run this player's
//                      MovementStateMachine at all, see
//                      NetworkPlayerController), and the pull/hold themselves
//                      then follow the same client-authoritative model as
//                      every other movement state.
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

        // ── Phase 3: WallRunning implemented as a dedicated state ────────
        // (AirborneState.FixedTick → WallRunningState). Grappling stays an
        // AbilityMovement driver; Zipline remains a reserved slot.
        WallRunning,

        /// <summary>
        /// UNUSED placeholder - Grapple is implemented as GrapplePullDriver +
        /// GrappleWallHoldDriver (Scripts/Movement/Grapple/) driven through
        /// AbilityMovement. During an active grapple (pulling OR held at the
        /// wall), MovementStateMachine.CurrentStateId reports AbilityMovement,
        /// never this value - query OffAngle.Networking.PlayerGrapple.IsGrappling
        /// instead if a system specifically needs to know "is the player
        /// grappling right now" (true for both the pull and the wall hold).
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
