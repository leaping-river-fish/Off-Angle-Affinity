// =============================================================================
// MovementStateMachine — owns the active state and routes engine callbacks.
//
// See IMovementState.cs for the full movement interaction philosophy that
// governs how states chain, share momentum, and transition to one another.
//
// HOW TO ADD A NEW DEDICATED STATE (e.g. a future WallRunningState):
//   1. Add a MovementStateId entry in IMovementState.cs.
//   2. Create the state class in Scripts/Movement/States/.
//   3. Call Register(new YourState()) in Initialize() below.
//   4. Add a TransitionTo(MovementStateId.YourState) call in the appropriate
//      source state.
//   No other files need to change.
//
// HOW TO ADD A NEW ABILITY WITHOUT A DEDICATED STATE (dash, grapple, blink,
// Affinity-specific movement, ...):
//   Implement IAbilityMovementDriver and call BeginAbilityMovement(driver)
//   below - no enum entry, no new state class, no registration needed. See
//   IAbilityMovementDriver.cs for the full contract.
//
// REUSABLE HOOKS (see IAbilityMovementDriver.cs and MovementStateContext.cs
// for the fields these wrap):
//   ApplyImpulse             - directional velocity kick
//   BeginAbilityMovement     - hand full locomotion control to a driver
//   InterruptCurrentAction   - cancel an active ability or end a slide early
//   CanStartMovementAction   - query before starting a new action
//   SetGravityMultiplier / SetSpeedMultiplier / SetInputLocked
//                            - temporary modifiers every built-in state respects
//   AddMaxJumpsBonus / RemoveMaxJumpsBonus / RestoreJump
//                            - jump-count and airborne-action hooks for
//                              Affinities/perks/buffs
//
// MULTIPLAYER NOTE:
//   In a networked game, Initialize() should only be called on the owning
//   client. Remote players replicate StateId and Velocity; their state
//   machine is driven by network data, not local input.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using OffAngle.Movement.States;

namespace OffAngle.Movement
{
    public class MovementStateMachine : MonoBehaviour
    {
        private MovementStateContext                        _ctx;
        private IMovementState                              _current;
        private Dictionary<MovementStateId, IMovementState> _states;

        // ------------------------------------------------------------------
        // Initialization — called by PlayerController.Awake()
        // ------------------------------------------------------------------

        public void Initialize(MovementStateContext ctx)
        {
            _ctx    = ctx;
            _states = new Dictionary<MovementStateId, IMovementState>();

            // ── Phase 1: fully implemented states ────────────────────────
            Register(new GroundedState());
            Register(new AirborneState());
            Register(new CrouchingState());

            // ── Phase 2: fully implemented states ────────────────────────
            Register(new SlidingState());
            Register(new AbilityMovementState());

            // ── Phase 3+: uncomment each line when the class is created ──
            // Register(new WallRunningState());
            // Register(new GrapplingState());
            // Register(new ZiplineState());

            // Wire input events to pending flags on context.
            // States poll these flags each Tick rather than subscribing
            // individually. This prevents missed events during transitions
            // and keeps state classes free of subscription management.
            ctx.Input.JumpStarted         += () => ctx.JumpPending         = true;
            ctx.Input.CrouchSlideStarted  += () =>
            {
                ctx.CrouchSlidePending = true;
                ctx.IsCrouchSlideHeld  = true;
            };
            ctx.Input.CrouchSlideCanceled += () => ctx.IsCrouchSlideHeld = false;

            // Set initial jump budget and enter the starting state
            ctx.RemainingJumps = ctx.EffectiveMaxJumps;
            _current = _states[MovementStateId.Grounded];
            _current.Enter(_ctx);
        }

        // ------------------------------------------------------------------
        // State transition
        // ------------------------------------------------------------------

        /// <summary>
        /// Requests a transition to the target state. Ignored if already in
        /// that state or if the target has not been registered yet (rather
        /// than throwing — callers can request future Phase 2/3 states safely).
        /// </summary>
        public void TransitionTo(MovementStateId nextId)
        {
            if (_current != null && _current.StateId == nextId)
                return;

            if (!_states.TryGetValue(nextId, out var nextState))
                return;

            _current?.Exit(_ctx);
            _current = nextState;
            _current.Enter(_ctx);
        }

        // ------------------------------------------------------------------
        // Engine routing
        // ------------------------------------------------------------------

        private void Update()
        {
            _current?.Tick(_ctx, Time.deltaTime);
        }

        private void FixedUpdate()
        {
            _current?.FixedTick(_ctx, Time.fixedDeltaTime);
        }

        // ------------------------------------------------------------------
        // Public accessors
        // ------------------------------------------------------------------

        /// <summary>The ID of the currently active movement state.</summary>
        public MovementStateId CurrentStateId => _current?.StateId ?? MovementStateId.Grounded;

        /// <summary>
        /// Normalized crouch progress (0 = standing, 1 = fully crouched).
        /// Presentation/networking layers (CameraCrouchOffset, NetworkPlayerCrouch)
        /// poll this instead of reaching into MovementStateContext directly -
        /// this is the one seam movement exposes outward for crouch consumers.
        /// </summary>
        public float CrouchAmount => _ctx?.CrouchAmount ?? 0f;

        /// <summary>True while GroundedState is the active state.</summary>
        public bool IsGrounded => CurrentStateId == MovementStateId.Grounded;

        /// <summary>True while AirborneState is the active state.</summary>
        public bool IsAirborne => CurrentStateId == MovementStateId.Airborne;

        /// <summary>True while CrouchingState is the active state.</summary>
        public bool IsCrouching => CurrentStateId == MovementStateId.Crouching;

        /// <summary>True while SlidingState is the active state.</summary>
        public bool IsSliding => CurrentStateId == MovementStateId.Sliding;

        /// <summary>True while an IAbilityMovementDriver is actively driving movement.</summary>
        public bool IsInAbilityMovement => CurrentStateId == MovementStateId.AbilityMovement;

        /// <summary>True while MovementStateMachine.SetInputLocked(true) is in effect.</summary>
        public bool IsInputLocked => _ctx?.InputLocked ?? false;

        // ------------------------------------------------------------------
        // Read-only query surface for OTHER systems (weapons, future ability
        // activation code, etc.) — see MovementSettings' "Slide Restrictions"
        // header for the tunables these read. Deliberately NOT enforced by
        // Gun/PlayerWeaponController today; exposed here so those systems
        // can opt in without movement code reaching into weapon behavior.
        // ------------------------------------------------------------------

        /// <summary>True if firing is currently allowed by movement state (false only while sliding with AllowFireDuringSlide disabled).</summary>
        public bool CanFire => !IsSliding || (_ctx?.Settings.AllowFireDuringSlide ?? true);

        /// <summary>True if reloading is currently allowed by movement state (false only while sliding with AllowReloadDuringSlide disabled).</summary>
        public bool CanReload => !IsSliding || (_ctx?.Settings.AllowReloadDuringSlide ?? true);

        /// <summary>True if a movement ability may be used right now - false while another ability is already active, or while sliding with AllowAbilitiesDuringSlide disabled.</summary>
        public bool CanUseMovementAbilities => !IsInAbilityMovement && (!IsSliding || (_ctx?.Settings.AllowAbilitiesDuringSlide ?? true));

        /// <summary>Query before starting any new movement action (dash, grapple, ...). Equivalent to CanUseMovementAbilities; kept as a separate method to match the "querying whether another movement action can currently begin" hook explicitly.</summary>
        public bool CanStartMovementAction() => CanUseMovementAbilities;

        /// <summary>
        /// Clears input carried over while this component was disabled.
        /// JumpPending/CrouchSlidePending are set by input event subscriptions
        /// that run regardless of this component's enabled state (Unity's
        /// enabled flag only pauses Update/FixedUpdate, not manual delegate
        /// subscriptions) - a press during death would otherwise sit pending
        /// and fire as a surprise action the instant movement resumes.
        /// PlayerLifecycleController calls this on respawn, before re-enabling
        /// this component.
        /// </summary>
        public void ResetTransientInput()
        {
            if (_ctx == null) return;
            _ctx.JumpPending = false;
            _ctx.CrouchSlidePending = false;
            _ctx.Velocity = Vector3.zero;

            // Force back to standing on respawn - a player who died mid-crouch
            // must not spawn with a shrunk capsule. IsCrouchSlideHeld is also
            // cleared so a key still physically held at the moment of death
            // does not immediately re-trigger Crouching on the fresh spawn.
            _ctx.IsCrouchSlideHeld = false;
            _ctx.CrouchAmount = 0f;
            _ctx.NextCrouchAllowedTime = 0f;

            // Slide bookkeeping: SlideTimer = 0 means a state frozen mid-slide
            // by death self-heals into Grounded/Crouching on its very first
            // Tick after respawn (see SlidingState.Tick()'s timeExpired check) -
            // no separate "force exit" call needed. Cooldown is also cleared
            // so death/respawn never leaves a lingering slide lockout.
            _ctx.SlideTimer = 0f;
            _ctx.NextSlideAllowedTime = 0f;

            // Ability-driven movement and any temporary modifiers must not
            // survive a death. Interrupting the driver (rather than just
            // discarding it) lets it release its own resources first (e.g. a
            // hooked grapple point) before AbilityMovementState's next Tick
            // would otherwise self-heal via its null-driver fallback anyway.
            if (_ctx.ActiveAbilityDriver != null)
            {
                var driver = _ctx.ActiveAbilityDriver;
                _ctx.ActiveAbilityDriver = null;
                driver.Exit(_ctx, wasInterrupted: true);
            }
            _ctx.InputLocked = false;
            _ctx.SpeedMultiplier = 1f;
            _ctx.GravityMultiplier = 1f;

            // BonusMaxJumps is intentionally left untouched - it represents a
            // persistent perk/Affinity grant, not a transient in-run effect.

            if (_ctx.Controller != null)
            {
                _ctx.Controller.height = _ctx.StandingHeight;
                _ctx.Controller.center = _ctx.StandingCenter;
            }
        }

        // ------------------------------------------------------------------
        // Reusable movement-ability hooks
        // Infrastructure for future Affinities/perks/buffs (dashes, wall
        // runs, grapples, blinks, altered gravity, ...). See
        // IAbilityMovementDriver.cs and MovementStateContext.cs for the
        // fields these wrap.
        // ------------------------------------------------------------------

        /// <summary>Adds a directional velocity impulse (e.g. a dash kick, explosion knockback, launch pad).</summary>
        public void ApplyImpulse(Vector3 impulse)
        {
            if (_ctx == null) return;
            _ctx.Velocity += impulse;
        }

        /// <summary>
        /// Hands full locomotion control to <paramref name="driver"/> by
        /// transitioning into MovementStateId.AbilityMovement. See
        /// IAbilityMovementDriver.cs for the driver contract.
        ///
        /// CHAINING NOTE: a driver's Exit() can call this again from inside
        /// its own onExit callback to hand off straight into a follow-up
        /// driver (e.g. GrapplePullDriver's arrival chaining into
        /// GrappleWallHoldDriver) - CurrentStateId is already AbilityMovement
        /// at that point, so TransitionTo() below would treat it as a no-op
        /// and silently skip Enter() (the state itself isn't changing).
        /// Detect that case and call the new driver's Enter() directly
        /// instead, otherwise AbilityMovementState.Tick()'s caller falls
        /// through to Grounded/Airborne right after this returns and the new
        /// driver is orphaned - never ticked, never exited, never releasing
        /// whatever resource it holds (see AbilityMovementState.Tick()'s
        /// matching post-Exit() check for the other half of this fix).
        /// </summary>
        public void BeginAbilityMovement(IAbilityMovementDriver driver)
        {
            if (_ctx == null || driver == null) return;
            _ctx.ActiveAbilityDriver = driver;

            if (CurrentStateId == MovementStateId.AbilityMovement)
            {
                driver.Enter(_ctx);
                return;
            }

            TransitionTo(MovementStateId.AbilityMovement);
        }

        /// <summary>
        /// Ends whatever movement action is currently overriding normal
        /// locomotion: cancels an active AbilityMovement driver (Exit called
        /// with wasInterrupted = true) or ends an active Sliding state early.
        /// No-op if neither is active. Falls back to Grounded/Airborne based
        /// on Controller.isGrounded, same as a natural exit would.
        /// </summary>
        public void InterruptCurrentAction()
        {
            if (_ctx == null) return;

            if (CurrentStateId == MovementStateId.AbilityMovement)
            {
                var driver = _ctx.ActiveAbilityDriver;
                _ctx.ActiveAbilityDriver = null;
                driver?.Exit(_ctx, wasInterrupted: true);
                TransitionTo(_ctx.Controller.isGrounded ? MovementStateId.Grounded : MovementStateId.Airborne);
            }
            else if (CurrentStateId == MovementStateId.Sliding)
            {
                TransitionTo(_ctx.Controller.isGrounded ? MovementStateId.Grounded : MovementStateId.Airborne);
            }
        }

        /// <summary>Temporarily scales gravity accumulation in AirborneState. Pass 1 to restore normal gravity.</summary>
        public void SetGravityMultiplier(float multiplier)
        {
            if (_ctx != null) _ctx.GravityMultiplier = multiplier;
        }

        /// <summary>Temporarily scales ground/air/slide speed calculations. Pass 1 to restore normal speed.</summary>
        public void SetSpeedMultiplier(float multiplier)
        {
            if (_ctx != null) _ctx.SpeedMultiplier = multiplier;
        }

        /// <summary>Locks or unlocks player-driven movement input (see MovementStateContext.InputLocked for exactly what this affects).</summary>
        public void SetInputLocked(bool locked)
        {
            if (_ctx != null) _ctx.InputLocked = locked;
        }

        /// <summary>Grants extra jump charges (adds to Settings.MaxJumps via MovementStateContext.EffectiveMaxJumps). Callers should pair this with RemoveMaxJumpsBonus when the source buff/perk ends.</summary>
        public void AddMaxJumpsBonus(int amount)
        {
            if (_ctx != null) _ctx.BonusMaxJumps += amount;
        }

        /// <summary>Removes a previously granted jump-count bonus. Pass the same amount given to AddMaxJumpsBonus.</summary>
        public void RemoveMaxJumpsBonus(int amount)
        {
            if (_ctx != null) _ctx.BonusMaxJumps -= amount;
        }

        /// <summary>
        /// Restores (but never exceeds EffectiveMaxJumps) airborne jump
        /// charges without waiting for a landing - e.g. an air-dash pickup or
        /// an Affinity effect that refunds a jump mid-air.
        /// </summary>
        public void RestoreJump(int count = 1)
        {
            if (_ctx == null) return;
            _ctx.RemainingJumps = Mathf.Min(_ctx.EffectiveMaxJumps, _ctx.RemainingJumps + count);
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private void Register(IMovementState state)
        {
            _states[state.StateId] = state;
        }
    }
}
