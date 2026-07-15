// =============================================================================
// MovementStateMachine — owns the active state and routes engine callbacks.
//
// See IMovementState.cs for the full movement interaction philosophy that
// governs how states chain, share momentum, and transition to one another.
//
// HOW TO ADD A NEW STATE (e.g. SlidingState):
//   1. Add MovementStateId.Sliding to the enum in IMovementState.cs (already there).
//   2. Create SlidingState.cs in Scripts/Movement/States/.
//   3. Call Register(new SlidingState()) in Initialize() below.
//   4. In GroundedState.HandleCrouchSlide(), uncomment TransitionTo(Sliding).
//   No other files need to change.
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

            // ── Phase 2+: uncomment each line when the class is created ──
            // Register(new SlidingState());
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
            ctx.RemainingJumps = ctx.Settings.MaxJumps;
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

        /// <summary>True while CrouchingState is the active state.</summary>
        public bool IsCrouching => CurrentStateId == MovementStateId.Crouching;

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
            if (_ctx.Controller != null)
            {
                _ctx.Controller.height = _ctx.StandingHeight;
                _ctx.Controller.center = _ctx.StandingCenter;
            }
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
