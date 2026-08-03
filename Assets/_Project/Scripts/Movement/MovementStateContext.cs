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
//   DoubleJump      : always overrides ctx.Velocity.y; horizontal preserved
//                     only if held direction ~matches current travel, else
//                     redirected + reduced (see AirborneState.ApplyDoubleJumpRedirect)
//
// MOMENTUM PRESERVATION (see GroundMomentum.cs for the full model):
//   Any time horizontal speed exceeds MovementSettings.NormalMaxSpeed (the
//   walk/sprint cap - not a separate hardcoded number), Airborne always
//   hands off to GroundMomentum's shared decay+steer routine instead of
//   directly setting velocity to the input-driven target speed - this is
//   what lets a grapple release, a slide-jump, or a bunny-hop chain carry
//   speed across a state transition without normal movement input instantly
//   cancelling it. Steering only ever changes DIRECTION, never magnitude -
//   speed out of that routine can only hold or decay, never increase.
//
//   Grounded/Crouching do the same, but ONLY while
//   GroundMomentum.ShouldPreserveGroundMomentum(ctx) is true - a short
//   bunny-hop grace window stamped on landing (GroundMomentumGraceEndTime,
//   tuned via GroundMomentumGraceDuration), or ctx.OnIcyGround (a reserved
//   hook for a future ice terrain type - see that field's doc comment).
//   Outside the grace window (and not on ice), excess ground momentum
//   hard-stops instantly instead of gradually decaying - "stop fully" is
//   the default; the grace window is what makes precisely-timed bunny
//   hopping the rewarded exception, not the default.
// =============================================================================

using UnityEngine;
using UnityEngine.Serialization;

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
        /// Remaining jumps in the current airborne bout. Reset to
        /// EffectiveMaxJumps on every landing (see AirborneState.Tick()) and
        /// refreshed to at least EffectiveMaxJumps - 1 when entering a wall
        /// run (see WallRunningState.Enter()) so a wall kick can be followed
        /// by a redirect double jump. Decremented by one per jump (ground
        /// jump or airborne jump alike - see GroundedState/CrouchingState/
        /// AirborneState/SlidingState). Wall kicks do not consume a charge.
        /// Must be server-authoritative in multiplayer to prevent cheat
        /// injection - not yet enforced since movement overall is still
        /// client-authoritative (see MovementStateMachine header).
        /// </summary>
        public int RemainingJumps;

        /// <summary>
        /// Additive modifier to Settings.MaxJumps, set via
        /// MovementStateMachine.AddMaxJumpsBonus/RemoveMaxJumpsBonus. This is
        /// the seam future Affinities/perks/buffs use to grant extra jumps
        /// without ever touching the serialized base value or hardcoding
        /// around a specific jump count. Not reset on respawn - treated as a
        /// persistent loadout/perk value, not a transient effect.
        /// </summary>
        public int BonusMaxJumps;

        /// <summary>The jump budget after applying BonusMaxJumps, clamped to at least 1.</summary>
        public int EffectiveMaxJumps => Mathf.Max(1, Settings.MaxJumps + BonusMaxJumps);

        /// <summary>
        /// Normalized crouch progress: 0 = fully standing, 1 = fully crouched.
        /// Owned and smoothed exclusively by CrouchingState via Mathf.MoveTowards.
        /// This is the single source of truth every crouch consumer reads from -
        /// CameraCrouchOffset (camera pivot height) and NetworkPlayerCrouch
        /// (third-person capsule scale) both poll this instead of duplicating
        /// their own crouch/stand timers. Being continuous rather than a bool
        /// also lets future systems (movement noise, stealth) scale smoothly
        /// with crouch depth instead of snapping.
        /// </summary>
        public float CrouchAmount;

        /// <summary>
        /// CharacterController.height captured once at startup (PlayerController.Awake),
        /// before any crouch logic runs. This is the single source of truth for
        /// "standing" - authored directly on the CharacterController in the
        /// Inspector rather than duplicated as a separate tunable.
        /// </summary>
        public float StandingHeight;

        /// <summary>
        /// CharacterController.center captured once at startup, paired with
        /// StandingHeight above.
        /// </summary>
        public Vector3 StandingCenter;

        /// <summary>
        /// Time.time value before which a fresh crouch press is refused.
        /// Set by CrouchingState.Exit() (i.e. the moment the player is fully
        /// back to standing) to CrouchCooldown seconds in the future. Only
        /// gates NEW crouch presses in GroundedState.HandleCrouchSlide() -
        /// it never delays releasing the key to stand back up.
        /// </summary>
        public float NextCrouchAllowedTime;

        /// <summary>
        /// Seconds remaining in the current slide. Set to Settings.SlideDuration
        /// by SlidingState.Enter(), counted down in SlidingState.Tick(). Reset
        /// to 0 by SlidingState.Exit() and by MovementStateMachine.ResetTransientInput()
        /// so a state frozen mid-slide by death self-heals into Grounded/Crouching
        /// on its very next Tick after respawn.
        /// </summary>
        public float SlideTimer;

        /// <summary>
        /// Time.time value before which a fresh slide attempt is refused.
        /// Stamped by SlidingState.Exit() to Time.time + Settings.SlideCooldown.
        /// Deliberately separate from NextCrouchAllowedTime so slide-spam
        /// prevention can be tuned independently of crouch-spam prevention -
        /// see GroundedState.HandleCrouchSlide().
        /// </summary>
        public float NextSlideAllowedTime;

        /// <summary>
        /// Time.time value until which GroundedState/CrouchingState still
        /// gradually decay/steer excess ground momentum instead of hard-
        /// stopping it - the bunny-hop timing window. Stamped by
        /// GroundMomentum.OnLanded() at the moment of an actual landing
        /// (see AirborneState.Tick() and AbilityMovementState.ExitToLocomotion),
        /// to Time.time + Settings.GroundMomentumGraceDuration. Read via
        /// GroundMomentum.ShouldPreserveGroundMomentum(). Deliberately NOT
        /// refreshed by GroundedState/CrouchingState.Enter() - only a real
        /// air-to-ground landing should grant a fresh window, or a player
        /// could refresh it indefinitely (e.g. toggling crouch) without ever
        /// leaving the ground.
        /// </summary>
        public float GroundMomentumGraceEndTime;

        /// <summary>
        /// RESERVED HOOK for a future ice terrain type - nothing sets this
        /// yet. While true, GroundMomentum.ShouldPreserveGroundMomentum()
        /// treats the player as permanently within the bunny-hop grace
        /// window, so ground momentum is never hard-stopped: standing on ice
        /// should automatically preserve momentum the way today's momentum
        /// system already does for a limited time after landing. Intended to
        /// be set every Tick by a future terrain-detection system (e.g. a
        /// ground raycast checking a PhysicMaterial/tag) rather than latched,
        /// so leaving the icy surface immediately restores normal grace/
        /// hard-stop behavior.
        /// </summary>
        public bool OnIcyGround;

        // ------------------------------------------------------------------
        // Reusable movement-ability hooks — infrastructure for future
        // Affinity/perk/buff-driven movement (dashes, wall runs, grapples,
        // blinks, altered gravity, ...). Every Phase-1/2 state already reads
        // SpeedMultiplier/GravityMultiplier/InputLocked, so a future ability
        // can lean on these instead of re-implementing its own temporary
        // override. Mutate these ONLY through MovementStateMachine's public
        // setters (SetSpeedMultiplier, SetGravityMultiplier, SetInputLocked,
        // BeginAbilityMovement) rather than reaching into this context
        // directly - that keeps a single seam for debugging.
        // ------------------------------------------------------------------

        /// <summary>Multiplies ground/air/slide speed calculations. 1 = unmodified.</summary>
        public float SpeedMultiplier = 1f;

        /// <summary>Multiplies gravity accumulation in AirborneState. 1 = unmodified.</summary>
        public float GravityMultiplier = 1f;

        /// <summary>
        /// While true, states treat MoveInput as Vector2.zero and discard
        /// JumpPending/CrouchSlidePending without acting on them - the input
        /// events themselves still fire (PlayerInputReader is unaffected),
        /// so nothing queues up and fires as a surprise the instant this
        /// unlocks. Intended for abilities that need to fully own locomotion
        /// (e.g. a channelled grapple) without disabling the whole
        /// MovementStateMachine the way death does.
        /// </summary>
        public bool InputLocked;

        /// <summary>
        /// The ability currently driving MovementStateId.AbilityMovement, or
        /// null when no ability is active. Set by
        /// MovementStateMachine.BeginAbilityMovement(); cleared by
        /// AbilityMovementState on natural completion or by
        /// MovementStateMachine.InterruptCurrentAction()/ResetTransientInput()
        /// on an early exit. See IAbilityMovementDriver.cs.
        /// </summary>
        public IAbilityMovementDriver ActiveAbilityDriver;

        // ------------------------------------------------------------------
        // Wall-run bookkeeping — owned by WallRunningState / AirborneState's
        // entry gate. Cleared by MovementStateMachine.ResetTransientInput()
        // on respawn. See WallDetection.cs for identity rules.
        // ------------------------------------------------------------------

        /// <summary>
        /// Collider of the wall surface the player most recently wall-ran on
        /// (or is currently wall-running on). Used by WallDetection.IsSameWall
        /// so reattaching to / transferring across the same continuous collider
        /// does NOT reset WallRunElapsedTime - only a genuinely different wall
        /// does. Cleared on respawn.
        /// </summary>
        public Collider WallRunLastCollider;

        /// <summary>
        /// World-space contact point paired with WallRunLastCollider. Lets
        /// connected-but-separate colliders (two angled panels butted
        /// together) still count as the same wall when the new contact is
        /// within Settings.SameWallContactTolerance - see WallDetection.IsSameWall.
        /// </summary>
        public Vector3 WallRunLastContactPoint;

        /// <summary>
        /// Seconds elapsed on the CURRENT wall surface. Reset only when the
        /// player transfers to a genuinely different wall (see
        /// WallDetection.IsSameWall). Compared against Settings.WallRunDuration
        /// each Tick by WallRunningState.
        /// </summary>
        public float WallRunElapsedTime;

        /// <summary>
        /// Time.time stamped by WallRunningState.Exit() for every exit path
        /// (jump, timeout, min-speed, lost contact). AirborneState uses this
        /// + Settings.WallReattachCooldown to block immediate reattachment to
        /// the SAME wall after jumping away - a different wall is never gated
        /// by this field alone (see WallRunExitLockEndTime for the universal
        /// post-exit lock).
        /// </summary>
        public float WallRunExitTime;

        /// <summary>
        /// Time.time until which AirborneState refuses ALL wall-run entry
        /// (any wall). Stamped by WallRunningState.Exit() to
        /// Time.time + Settings.WallJumpExitLock - the tutorial "exitingWall"
        /// window that makes wall jumps feel clean instead of instantly
        /// re-sticking. Cleared on respawn.
        /// </summary>
        public float WallRunExitLockEndTime;

        /// <summary>
        /// Which side the active wall run is on (Left/Right), or None when
        /// not wall running. Set by WallRunningState for presentation
        /// (CameraWallRunEffects tilt sign); cleared on Exit / respawn.
        /// </summary>
        public WallSide WallRunSide;

        /// <summary>
        /// Reserved hook for future Affinity perks that enable vertical wall
        /// movement. Range expected [-1, 1]: negative = down the wall,
        /// positive = up, 0 = default horizontal-only wall run (gravity via
        /// WallRunGravityScale still applies). Mutate ONLY through
        /// MovementStateMachine.SetWallVerticalInput so Affinity code never
        /// reaches into this context directly. Nothing sets this yet.
        /// </summary>
        public float WallVerticalInput;
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

        /// <summary>
        /// The single global speed (m/s) above which a player is considered
        /// to be carrying preserved momentum (grapple, slide-jump, bunny-hop
        /// chain, ...) rather than just walking or sprinting - see
        /// GroundMomentum.cs. Deliberately a computed property rather than a
        /// separate serialized field: reuses the existing WalkSpeed/
        /// SprintSpeed tunables instead of introducing a second, driftable
        /// source of truth for "normal top speed". Today this evaluates to
        /// SprintSpeed (7 m/s) since Sprint is always faster than Walk.
        /// </summary>
        public float NormalMaxSpeed => Mathf.Max(WalkSpeed, SprintSpeed);

        [Header("Momentum Preservation")]
        [Tooltip("How much excess speed (m/s²) above NormalMaxSpeed decays per second while GROUNDED or CROUCHED - applies identically whether or not movement input is held, so releasing input never causes a sudden change in decay behavior. Tuned much stronger than AirMomentumDecay so a fast landing or slide bleeds off within roughly a second instead of sliding across the ground indefinitely. Never reduces speed below NormalMaxSpeed - normal walk/sprint/crouch speed is completely unaffected.")]
        public float GroundMomentumDecay = 20f;
        [Tooltip("How much excess speed (m/s²) above NormalMaxSpeed decays per second while AIRBORNE. Deliberately much weaker than GroundMomentumDecay - this is what makes a grapple release or a slide-jump feel committed to its trajectory instead of losing speed at the same rate as touching the ground. Never reduces speed below NormalMaxSpeed.")]
        public float AirMomentumDecay = 1.5f;
        [Tooltip("Acceleration (m/s²) used to steer (not replace) velocity direction while GROUNDED/CROUCHED with speed above NormalMaxSpeed. Input nudges the momentum vector toward the wish direction over time rather than instantly snapping to it - the higher this is, the more responsive steering feels at the cost of momentum being easier to redirect.")]
        public float GroundMomentumSteerAcceleration = 12f;
        [Tooltip("Acceleration (m/s²) used to steer (not replace) velocity direction while AIRBORNE with speed above NormalMaxSpeed. Independent of GroundMomentumSteerAcceleration so air steering while carrying momentum can be tuned separately from ground steering.")]
        public float AirMomentumSteerAcceleration = 6f;
        [Tooltip("Small buffer (m/s) added to NormalMaxSpeed when deciding whether the player is in momentum mode. Prevents floating-point jitter right at the threshold from flickering between normal movement and momentum decay on consecutive frames.")]
        public float MomentumTolerance = 0.05f;
        [Tooltip("Hard safety ceiling (m/s) on any horizontal speed preserved by the momentum system, regardless of source (grapple, slide-jump, external forces, stacked abilities). Should sit comfortably above GrappleMaxPullSpeed and SlideMaxSpeed so it never interferes with normal ability use - it only exists to bound worst-case exploits/stacking.")]
        public float MaxPreservedSpeed = 60f;
        [Tooltip("Seconds after landing during which GroundedState/CrouchingState still gradually decay/steer excess ground momentum (GroundMomentumDecay/GroundMomentumSteerAcceleration) instead of hard-stopping it immediately - the bunny-hop timing window. A jump pressed within this window (or one pressed the same Tick as landing, which never even reaches this check) chains speed forward; letting the window expire while still grounded snaps momentum straight down to normal walk/sprint speed. Ignored entirely while ctx.OnIcyGround is true - see MovementStateContext.OnIcyGround and GroundMomentum.ShouldPreserveGroundMomentum.")]
        public float GroundMomentumGraceDuration = 0.15f;

        [Header("Slope Movement")]
        [Range(0f, 1f)]
        [Tooltip("Speed multiplier applied when walking/sprinting/crouch-walking straight UPHILL at the steepest walkable angle (Controller.slopeLimit). Scales down linearly with slope angle and with how directly the wish direction points uphill - strafing across a slope is unaffected. 1 = uphill never slows you down. Applied as an instant cap scale (see SlopeUtility.GetSlopeSpeedMultiplier), matching the same responsive feel as flat-ground walk/sprint - does not affect momentum-preserving speed above NormalMaxSpeed or SlidingState (which has its own slope model via SlideSlopeAcceleration).")]
        public float SlopeUphillSpeedMultiplier = 0.7f;
        [Tooltip("Speed multiplier applied when walking/sprinting/crouch-walking straight DOWNHILL at the steepest walkable angle. Same angle/direction scaling as SlopeUphillSpeedMultiplier. 1 = downhill never speeds you up; values above 1 grant a downhill speed boost.")]
        public float SlopeDownhillSpeedMultiplier = 1.3f;
        [Tooltip("Maximum gap (m) between the capsule's bottom and the ground below it that Grounded/Crouching/Sliding will automatically close each Tick via an extra CharacterController.Move() call - see SlopeUtility.SnapToGround. This is what keeps a fast downhill slide (or normal ground movement over uneven/faceted terrain) glued to the surface instead of intermittently losing Controller.isGrounded for a frame and visibly bouncing into AirborneState and back (the \"bumpy slide\" symptom). Set to 0 to disable snapping entirely. Keep well below CharacterController.stepOffset so this only ever corrects tiny approximation error, never silently teleports the player down a real step or ledge.")]
        public float GroundSnapDistance = 0.3f;

        [Header("Jumping")]
        [Tooltip("Apex height in meters. Drives jump velocity via v = sqrt(2 * h * g).")]
        public float JumpHeight  = 0.9f;
        [Tooltip("Downward acceleration in m/s². Increase for snappier feel.")]
        public float Gravity     = 20f;
        [Tooltip("Base number of jumps before the player must land again. 1 = single jump, 2 = double jump, etc. Not hardcoded to any specific count - runtime bonuses stack on top via MovementStateMachine.AddMaxJumpsBonus (see MovementStateContext.EffectiveMaxJumps), so Affinities/perks/buffs can grant extra jumps without touching this value.")]
        public int   MaxJumps    = 1;

        /// <summary>
        /// Vertical exit speed for a jump of JumpHeight under Gravity
        /// (v = sqrt(2 * h * g)). Single source of truth for every jump
        /// impulse (ground jump, crouch-jump, slide-jump, double jump) -
        /// mirrors the EffectiveMaxJumps computed-property pattern on
        /// MovementStateContext instead of each state re-deriving it.
        /// </summary>
        public float JumpVelocity => Mathf.Sqrt(2f * JumpHeight * Gravity);

        [Header("Double Jump Redirect")]
        [Range(-1f, 1f)]
        [Tooltip("Dot product between current horizontal travel direction and the held input direction at the moment of a double jump. At or above this value the held direction is \"close enough\" and full horizontal momentum is preserved (old behavior). Below it, the jump is treated as a direction change - see DoubleJumpReversalRetention. 1 = only a perfectly straight double jump keeps full speed; -1 = redirect never reduces momentum.")]
        public float DoubleJumpRedirectDotThreshold = 0.6f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of current horizontal speed kept when a double jump redirect is a full 180° reversal (dot = -1). Redirects between the threshold above and a full reversal scale linearly between 1.0 and this value, and velocity direction snaps to the newly held direction rather than just slowing down along the old path. 1 = redirecting never costs speed.")]
        public float DoubleJumpReversalRetention = 0.35f;

        [Header("Air Movement")]
        [Range(0f, 1f)]
        [Tooltip("Scales how strongly player input steers while airborne.")]
        public float AirControlMultiplier = 0.35f;
        [Tooltip("Horizontal acceleration rate (m/s²) applied toward wish direction in air.")]
        public float AirAcceleration      = 15f;

        [Header("Slide / Crouch")]
        [Tooltip("Horizontal speed (m/s) required at Left Ctrl press to enter Slide instead of Crouch. Must be greater than SlideMinSpeed, or the slide will end on the very first Tick. Tune in playtesting.")]
        public float SlideEntrySpeedThreshold = 4f;
        [Tooltip("Maximum seconds a single slide can last before it automatically ends (independent of speed). Does NOT apply while sliding meaningfully downhill - see SlidingState's DURATION note - only flat ground and uphill slides are bound by this timer.")]
        public float SlideDuration            = 1.2f;
        [Tooltip("Hard speed ceiling for sliding, in m/s. Entry speed above this is clamped down; entry speed below this is preserved as-is (see SlidingState.Enter). Does NOT cap sliding meaningfully downhill - that instead accelerates continuously up to MaxPreservedSpeed, so a long slope keeps rewarding speed instead of plateauing here almost immediately. Applies to flat ground and uphill sliding.")]
        public float SlideMaxSpeed            = 9f;
        [Tooltip("Horizontal speed (m/s) below which an active slide automatically ends. Should be lower than SlideEntrySpeedThreshold.")]
        public float SlideMinSpeed            = 2.5f;
        [Tooltip("Seconds after a slide ends before another slide can begin. Independent of CrouchCooldown so slide-spam prevention can be tuned separately from crouch-spam prevention.")]
        public float SlideCooldown            = 0.5f;
        [Tooltip("Acceleration (m/s²) applied toward the player's held move direction while sliding. Lets the player steer without killing slide speed the way ground movement would.")]
        public float SlideSteerAcceleration   = 10f;
        [Tooltip("Acceleration (m/s²) applied along the slope's downhill direction while sliding, and subtracted while sliding uphill. Flat ground has none of this and holds speed constant. Set to 0 to disable slope sensitivity entirely - speed then never changes on its own while sliding.")]
        public float SlideSlopeAcceleration   = 6f;

        [Header("Slide Restrictions")]
        [Tooltip("If true, pressing Jump during a slide launches the player (slide-jump: full horizontal speed preserved + vertical impulse). If false, Jump presses during a slide are ignored.")]
        public bool AllowJumpDuringSlide      = true;
        [Tooltip("Not enforced by movement code - exposed via MovementStateMachine.CanFire for the weapon system to query if/when it integrates this restriction.")]
        public bool AllowFireDuringSlide      = true;
        [Tooltip("Not enforced by movement code - exposed via MovementStateMachine.CanReload for the weapon system to query if/when it integrates this restriction.")]
        public bool AllowReloadDuringSlide    = true;
        [Tooltip("Not enforced by movement code - exposed via MovementStateMachine.CanUseMovementAbilities for future ability systems to query.")]
        public bool AllowAbilitiesDuringSlide = true;

        [Tooltip("CharacterController height while fully crouched, in meters. Center is derived automatically as height/2 so the capsule always shrinks from the top with feet planted.")]
        public float CrouchHeight = 1.0f;
        [Tooltip("Seconds to fully transition between standing and crouching. Keep NetworkPlayerCrouch's transition duration equal to this so the third-person capsule matches the owner's local capsule.")]
        public float CrouchTransitionDuration = 0.15f;
        [Tooltip("Minimum seconds after standing back up before a fresh crouch press is accepted again. Prevents rapid spam-tapping from re-triggering the transition mid-cycle. Does not delay releasing the key to stand.")]
        public float CrouchCooldown = 0.2f;
        [Tooltip("Layers checked for overhead obstructions before allowing a return to standing. Must exclude the player's own layer (e.g. \"Player\") to avoid self-collision false positives.")]
        public LayerMask StandCheckMask = ~0;

        [Header("Wall Run")]
        [Tooltip("Horizontal speed (m/s) required to ENTER a wall run from Airborne. Also needs a modest along-wall component (see WallDetection.MeetsEntrySpeed) so head-on rams fail. Should be >= WallRunMinSpeed.")]
        public float WallRunEntrySpeed = 3.5f;
        [Tooltip("Wall-tangential speed (m/s) below which an active wall run ends and the player falls. Should be lower than WallRunEntrySpeed.")]
        public float WallRunMinSpeed = 2.5f;
        [Tooltip("Maximum seconds a wall run can last on a SINGLE wall surface before the player detaches. Transfers to a genuinely different wall reset this timer; reattaching to the same wall (or following a curve on the same collider) does NOT.")]
        public float WallRunDuration = 2.5f;
        [Tooltip("Acceleration (m/s²) applied along the wall-tangent direction while wall running, toward WallRunMaxSpeed.")]
        public float WallRunAcceleration = 14f;
        [Tooltip("Hard speed ceiling (m/s) for wall-tangential speed while wall running. Entry speed above this is clamped down; below this, entry is floored to WallRunEntrySpeed so attachment feels committed.")]
        public float WallRunMaxSpeed = 10f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of normal gravity applied during wall run while WallVerticalInput is 0. 0 = stuck at attach height (recommended default). Raise above 0 only if you want a slow downward drift.")]
        public float WallRunGravityScale = 0f;
        [Tooltip("Detection reach (m) from the capsule center for left/right/into-wall casts. Slightly larger than Controller.radius; too large keeps you stuck past wall ends and flings you along the wall.")]
        public float WallDetectionDistance = 0.95f;
        [Tooltip("Maximum degrees a surface normal may deviate from vertical (90° from world-up) and still count as a wall. Filters floors, ceilings, and shallow ramps. 25 ≈ walls within ~65–115° of world-up.")]
        public float WallAngleTolerance = 25f;
        [Tooltip("Max distance (m) between consecutive wall contacts that still count as the SAME wall when the collider identity differs (e.g. two angled panels butted together). Same-collider contacts always count as the same wall regardless of this value.")]
        public float SameWallContactTolerance = 1.5f;
        [Tooltip("Seconds after leaving a wall run during which reattaching to the SAME wall is refused. A different wall is never gated by this alone - see WallJumpExitLock for the short universal post-exit block.")]
        public float WallReattachCooldown = 0.25f;
        [Tooltip("Seconds after ANY wall-run exit during which NO wall run may begin (any wall). Tutorial-style exitingWall lock - keeps wall jumps from instantly re-sticking. Keep short so wall-to-wall transfers after a kick still work.")]
        public float WallJumpExitLock = 0.2f;
        [Tooltip("Max degrees/second the tracked wall normal may rotate toward a newly sampled hit normal. Smooths faceted/curved meshes so wall-run direction and wall-jump do not jitter or launch the player.")]
        public float WallNormalSmoothingSpeed = 540f;
        [Tooltip("Horizontal launch speed (m/s) used as a floor for wall-jump velocity. Jump direction follows camera look (player forward); a small outward peel only applies if look is nearly into/along the wall.")]
        public float WallJumpOutwardForce = 10f;
        [Tooltip("Upward impulse (m/s) on Jump during a wall run. Independent of JumpVelocity so wall kicks feel like a powerful kick-off.")]
        public float WallJumpUpwardForce = 9f;
        [Tooltip("Vertical speed (m/s) applied along the wall when ctx.WallVerticalInput is non-zero (future Affinity perk hook). Default player never sets WallVerticalInput, so this has no effect until a perk enables it via MovementStateMachine.SetWallVerticalInput.")]
        public float WallVerticalMoveSpeed = 4f;

        [Header("Grapple")]
        [Tooltip("Speed (m/s) the player is instantly set to toward the grapple anchor point every Tick while being pulled - no ramp-up, this is a hard snap-to-speed tow, not an acceleration.")]
        public float GrappleMaxPullSpeed = 28f;
        [Tooltip("Distance (m) from the anchor at which an active pull auto-completes (treated as \"arrived\", beginning the wall hold). Also acts as a buffer so the player doesn't collide with the surface they just grappled.")]
        public float GrappleReleaseDistance = 2f;
        [Tooltip("Safety timeout (seconds) - an active pull auto-completes if it hasn't arrived or been cancelled by then (e.g. an anchor that's unreachable because of intervening geometry).")]
        public float GrappleMaxDuration = 3f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of normal gravity applied while being pulled (0 = fully suppressed like a zipline, 1 = full gravity arcs the pull like a real cable).")]
        public float GrappleGravityScale = 0.15f;
        [Tooltip("If true, a raycast from the player toward the anchor that hits something else first auto-completes the pull early, preventing the player from grinding into a wall corner instead of swinging around it.")]
        public bool GrappleAutoReleaseOnObstruction = true;
        [Tooltip("Seconds the player is held frozen in place at the wall after a grapple pull arrives (GrapplePullExitReason.Arrived only - a cancelled, timed-out, or obstructed pull skips the hold entirely). Ends early if the player presses Jump. See GrappleWallHoldDriver.")]
        public float GrappleWallHoldDuration = 5f;
    }
}
