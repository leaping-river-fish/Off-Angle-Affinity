// =============================================================================
// AbilityMovementState — generic host for any IAbilityMovementDriver.
// Infrastructure: implemented now; no concrete driver ships with this state.
//
// This state deliberately contains ZERO gameplay logic of its own. It exists
// so future movement abilities (dash, wall run, grapple, blink, Affinity
// moves) can take over locomotion without MovementStateMachine or any other
// state needing to know about them ahead of time - see IAbilityMovementDriver.cs
// for the full contract and MovementStateMachine.BeginAbilityMovement().
//
// ENTERING / EXITING:
//   Entered only via MovementStateMachine.BeginAbilityMovement(driver), which
//   assigns ctx.ActiveAbilityDriver before transitioning here.
//   Exits back to Grounded or Airborne (whichever matches
//   Controller.isGrounded) once the driver reports IsComplete(), or
//   immediately if interrupted via MovementStateMachine.InterruptCurrentAction().
//
// DEFENSIVE FALLBACK:
//   If ctx.ActiveAbilityDriver is ever null while this state is active (e.g.
//   MovementStateMachine.ResetTransientInput() cleared it on a mid-ability
//   respawn), Tick() immediately falls back to Grounded/Airborne instead of
//   throwing or freezing - the same "self-heal on the next Tick" pattern
//   already relied on by Crouching/Sliding for the same respawn scenario.
// =============================================================================

namespace OffAngle.Movement.States
{
    public class AbilityMovementState : IMovementState
    {
        public MovementStateId StateId => MovementStateId.AbilityMovement;

        public void Enter(MovementStateContext ctx)
        {
            ctx.ActiveAbilityDriver?.Enter(ctx);
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            IAbilityMovementDriver driver = ctx.ActiveAbilityDriver;
            if (driver == null)
            {
                ExitToLocomotion(ctx);
                return;
            }

            driver.Tick(ctx, deltaTime);

            if (driver.IsComplete(ctx))
            {
                ctx.ActiveAbilityDriver = null;
                driver.Exit(ctx, wasInterrupted: false);
                ExitToLocomotion(ctx);
            }
        }

        public void FixedTick(MovementStateContext ctx, float fixedDeltaTime)
        {
            // Physics-query-style driver needs (e.g. a grapple line-of-sight
            // recheck) belong here, mirroring the other states' FixedTick use.
        }

        public void Exit(MovementStateContext ctx)
        {
            // Intentionally empty. Driver cleanup happens explicitly at the
            // two call sites that end this state (Tick()'s completion branch
            // and MovementStateMachine.InterruptCurrentAction()) so Exit(bool)
            // always receives an accurate wasInterrupted value - this
            // parameterless IMovementState.Exit() can't express that.
        }

        private void ExitToLocomotion(MovementStateContext ctx)
        {
            ctx.StateMachine.TransitionTo(
                ctx.Controller.isGrounded ? MovementStateId.Grounded : MovementStateId.Airborne);
        }
    }
}
