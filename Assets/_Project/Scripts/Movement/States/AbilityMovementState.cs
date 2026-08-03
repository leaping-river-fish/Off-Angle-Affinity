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
// DRIVER CHAINING (a driver handing off to a follow-up driver):
//   A driver's Exit() can call BeginAbilityMovement() again from inside its
//   own onExit callback to hand off straight into a new driver without ever
//   leaving AbilityMovement - e.g. GrapplePullDriver's arrival chains into
//   GrappleWallHoldDriver (see PlayerGrapple.HandlePullExited). Tick()'s
//   completion branch below checks ctx.ActiveAbilityDriver again right after
//   calling Exit() specifically to support this: if it's non-null, a new
//   driver was just chained in and already Entered (see
//   MovementStateMachine.BeginAbilityMovement's CHAINING NOTE), so Tick()
//   stays in AbilityMovement instead of falling through to Grounded/Airborne
//   and orphaning it. Getting either half of this wrong silently strands the
//   new driver forever - never ticked, IsComplete() never polled, Exit()
//   never called, and whatever resource it holds (e.g. a still-attached
//   GrappleHook) never released.
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

                // Exit()'s onExit callback may have chained straight into a
                // follow-up driver via MovementStateMachine.BeginAbilityMovement()
                // (e.g. GrapplePullDriver's arrival -> GrappleWallHoldDriver -
                // see BeginAbilityMovement's CHAINING NOTE). If so,
                // ctx.ActiveAbilityDriver is non-null again and already
                // Entered - stay in AbilityMovement and tick it next frame
                // instead of falling through to Grounded/Airborne and
                // orphaning it (never ticked, never exited again).
                if (ctx.ActiveAbilityDriver != null)
                    return;

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
            bool isGrounded = ctx.Controller.isGrounded;

            // A driver (e.g. GrapplePullDriver arriving/releasing at a
            // ground anchor) can end while already touching the ground,
            // bypassing AirborneState.Tick()'s landing block entirely - this
            // is the other "just touched down" point that needs to stamp a
            // fresh bunny-hop grace window, same reasoning as that block -
            // see GroundMomentum.OnLanded.
            if (isGrounded)
                GroundMomentum.OnLanded(ctx);

            ctx.StateMachine.TransitionTo(isGrounded ? MovementStateId.Grounded : MovementStateId.Airborne);
        }
    }
}
