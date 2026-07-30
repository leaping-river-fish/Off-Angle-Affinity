// =============================================================================
// GrappleWallHoldDriver — IAbilityMovementDriver that freezes the player in
// place at the wall for GrappleWallHoldDuration seconds after a
// GrapplePullDriver arrives (GrapplePullExitReason.Arrived). Driven through
// MovementStateMachine.BeginAbilityMovement() / MovementStateId.AbilityMovement,
// exactly like GrapplePullDriver - see IAbilityMovementDriver.cs.
//
// WHY A SEPARATE DRIVER:
// The pull and the hold are two distinct movement behaviors chained
// together by PlayerGrapple.HandlePullExited (Pulling → Holding), each with
// its own single responsibility - same "small focused driver" pattern
// GrapplePullDriver itself already established.
//
// "FROZEN BUT NOT BLIND" CONTRACT:
// This driver owns ctx.Velocity/Controller.Move like any IAbilityMovementDriver,
// but deliberately does nothing else - it never touches camera look, aim, or
// firing, none of which are gated by movement state in this project (see
// MovementStateMachine.CanFire, which only ever checks IsSliding). The player
// can look around and shoot while held; they simply cannot move.
//
// EARLY EXIT — THE ONLY WAY OUT BESIDES WAITING:
// Pressing Jump during the hold ends it immediately with a normal vertical
// jump impulse (same formula AirborneState.Tick() uses), functioning as a
// wall-jump-style cancel. Releasing/re-pressing the Grapple input does
// nothing while held - see PlayerGrapple.HandleGrappleCanceled, which
// intentionally does not special-case GrappleState.Holding. Death/respawn
// still force-ends the hold via MovementStateMachine.InterruptCurrentAction()
// (see PlayerGrapple.CancelGrapple), same as it does for an active pull.
// =============================================================================

using System;
using OffAngle.Movement;
using UnityEngine;

namespace OffAngle.Movement.Grapple
{
    public class GrappleWallHoldDriver : IAbilityMovementDriver
    {
        private readonly Action _onExit;

        private float _elapsed;
        private bool _complete;

        /// <param name="onExit">
        /// Invoked exactly once from Exit(), regardless of whether the hold
        /// ended naturally (duration elapsed), was cut short by a Jump
        /// press, or was interrupted (death/respawn). PlayerGrapple reacts
        /// identically in every case - despawn the now-obsolete GrappleHook
        /// and reset its own state back to Idle - so no reason parameter is
        /// needed here, unlike GrapplePullDriver's onExit. See
        /// PlayerGrapple.HandleHoldExited.
        /// </param>
        public GrappleWallHoldDriver(Action onExit)
        {
            _onExit = onExit;
        }

        // ------------------------------------------------------------------
        // IAbilityMovementDriver implementation
        // ------------------------------------------------------------------

        public void Enter(MovementStateContext ctx)
        {
            _elapsed = 0f;
            _complete = false;

            // Explicit gameplay decision (see MovementStateContext.cs's Rule
            // 1) - whatever speed the pull left the player at is discarded
            // the instant they're pinned to the wall, so they don't appear
            // to keep drifting while "frozen".
            ctx.Velocity = Vector3.zero;
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            // Jump is the one input this driver ever reads - a deliberate
            // early exit, not a general input passthrough. It always fires
            // a full jump impulse, overriding the frozen velocity outright,
            // exactly like AirborneState's own jump handling.
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;
                float jumpVelocity = Mathf.Sqrt(2f * ctx.Settings.JumpHeight * ctx.Settings.Gravity);
                ctx.Velocity = Vector3.up * jumpVelocity;
                _complete = true;
                return;
            }

            _elapsed += deltaTime;
            if (_elapsed >= ctx.Settings.GrappleWallHoldDuration)
            {
                _complete = true;
                return;
            }

            // Zero move keeps Controller.isGrounded fresh (e.g. a moving
            // platform arriving underneath mid-hold) without letting the
            // player drift - ctx.Velocity stays zero for the whole hold.
            ctx.Controller.Move(Vector3.zero);
        }

        public bool IsComplete(MovementStateContext ctx) => _complete;

        public void Exit(MovementStateContext ctx, bool wasInterrupted)
        {
            // Intentionally does NOT touch ctx.Velocity - the natural-timeout
            // path leaves it at zero (a clean drop into Airborne/Grounded),
            // the Jump path already set it to the jump impulse above, and an
            // interruption (death/respawn) preserves whatever it already was.
            _onExit?.Invoke();
        }
    }
}
