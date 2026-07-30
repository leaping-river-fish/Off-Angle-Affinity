// =============================================================================
// IAbilityMovementDriver — the seam future movement abilities (dash, wall run,
// grapple, blink, altered gravity, Affinity-specific movement) implement to
// take over locomotion for a bounded duration, without any changes to
// MovementStateMachine or the other states.
//
// USAGE:
//   1. Implement this interface in a plain C# class (or a MonoBehaviour that
//      also implements it - either works, since Enter/Tick/Exit only ever
//      receive MovementStateContext).
//   2. Call MovementStateMachine.BeginAbilityMovement(driver) to start it.
//      This transitions into MovementStateId.AbilityMovement, which owns
//      Velocity/Controller.Move for as long as the driver reports !IsComplete.
//   3. Once IsComplete(ctx) returns true, AbilityMovementState calls
//      Exit(ctx, wasInterrupted: false) and hands control back to
//      Grounded/Airborne (based on Controller.isGrounded).
//   4. MovementStateMachine.InterruptCurrentAction() can end the driver early
//      (Exit(ctx, wasInterrupted: true)) - e.g. on death/respawn, or when a
//      higher-priority ability needs to take over.
//
// CONTRACT:
//   - Enter()/Tick() own ctx.Velocity and are responsible for calling
//     ctx.Controller.Move() themselves, exactly like any other IMovementState -
//     AbilityMovementState does not apply gravity, friction, or input on the
//     driver's behalf.
//   - Do not assume Grounded/Airborne semantics; query ctx.Controller.isGrounded
//     directly if the driver cares.
//   - Exit() is called exactly once per activation - either path (natural
//     completion or interruption), never both.
//   - CHAINING: a driver's Exit()/onExit callback may call
//     MovementStateMachine.BeginAbilityMovement(nextDriver) to hand off
//     straight into a follow-up driver without ever leaving AbilityMovement
//     (e.g. GrapplePullDriver's arrival chaining into GrappleWallHoldDriver -
//     see PlayerGrapple.HandlePullExited). BeginAbilityMovement() detects
//     this re-entrant case and calls nextDriver.Enter() directly, and
//     AbilityMovementState.Tick() checks for it right after calling Exit()
//     so it stays in AbilityMovement instead of falling through to
//     Grounded/Airborne - see both call sites' comments for the full
//     mechanics. A driver that does NOT chain needs no awareness of this at
//     all; it only matters to drivers that do.
// =============================================================================

namespace OffAngle.Movement
{
    public interface IAbilityMovementDriver
    {
        /// <summary>Called once when MovementStateMachine.BeginAbilityMovement(this) takes effect.</summary>
        void Enter(MovementStateContext ctx);

        /// <summary>Called every Update frame while this driver is active.</summary>
        void Tick(MovementStateContext ctx, float deltaTime);

        /// <summary>Polled every Tick after Tick() runs. Once true, the state machine exits AbilityMovement and calls Exit(ctx, false).</summary>
        bool IsComplete(MovementStateContext ctx);

        /// <summary>
        /// Called exactly once: either after IsComplete() returns true
        /// (wasInterrupted = false) or when cut short by
        /// MovementStateMachine.InterruptCurrentAction() / a respawn reset
        /// (wasInterrupted = true). Use this to clean up (release a grapple
        /// point, restore a modified camera FOV, etc.).
        /// </summary>
        void Exit(MovementStateContext ctx, bool wasInterrupted);
    }
}
