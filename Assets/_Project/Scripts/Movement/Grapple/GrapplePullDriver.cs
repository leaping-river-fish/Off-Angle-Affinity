// =============================================================================
// GrapplePullDriver — IAbilityMovementDriver that pulls the player toward a
// fixed anchor point (the surface a GrappleHook attached to), Titanfall/Just
// Cause style: an instant snap to GrappleMaxPullSpeed toward the anchor, not
// a ramped acceleration or a pendulum swing. Driven through
// MovementStateMachine.BeginAbilityMovement() / MovementStateId.AbilityMovement
// - see IAbilityMovementDriver.cs.
//
// NO FISHNET DEPENDENCY:
// Like every other file under Scripts/Movement/, this is a plain C# class.
// OffAngle.Networking.PlayerGrapple owns the hook's networked lifecycle
// (spawning, collision resolution, RPCs) and only ever hands this class a
// world-space anchor point plus a completion callback - see the onExit
// parameter below.
//
// MOMENTUM / "SLINGSHOT" CONTRACT:
// This driver never zeroes ctx.Velocity, on completion OR interruption -
// exactly the same contract every other state in this project already
// follows (see IMovementState.cs's MOMENTUM CHAIN REFERENCE). Whatever
// velocity Tick() last set toward the anchor is exactly what the player
// carries into Grounded/Airborne (Interrupted/TimedOut/Obstructed) or into
// GrappleWallHoldDriver (Arrived) the instant this driver ends - see
// GrapplePullExitReason below and PlayerGrapple.HandlePullExited for how
// each reason is routed. Cancelling mid-pull (PlayerGrapple.HandleGrappleCanceled
// calling MovementStateMachine.InterruptCurrentAction()) is the "grapple and
// cancel but keep the momentum" slingshot mechanic - no special case is
// needed here to make cancelling feel different from arriving.
// =============================================================================

using System;
using OffAngle.Movement;
using UnityEngine;

namespace OffAngle.Movement.Grapple
{
    /// <summary>
    /// Why a GrapplePullDriver activation ended - passed to its onExit
    /// callback so PlayerGrapple can tell an actual wall arrival (which
    /// should begin the wall-hold phase) apart from every other way the
    /// pull can end (which should release immediately, exactly like before).
    /// </summary>
    public enum GrapplePullExitReason
    {
        /// <summary>Cancelled early via MovementStateMachine.InterruptCurrentAction() (the slingshot cancel) or a death/respawn reset.</summary>
        Interrupted,
        /// <summary>Reached within GrappleReleaseDistance of the anchor under its own power - the only reason that begins GrappleWallHoldDriver.</summary>
        Arrived,
        /// <summary>Hit GrappleMaxDuration before arriving (e.g. an anchor unreachable because of intervening geometry).</summary>
        TimedOut,
        /// <summary>GrappleAutoReleaseOnObstruction ended the pull early because something stood between the player and the anchor.</summary>
        Obstructed,
    }

    public class GrapplePullDriver : IAbilityMovementDriver
    {
        private readonly Vector3 _anchor;
        private readonly Action<GrapplePullExitReason> _onExit;

        private float _elapsed;
        private bool _complete;
        private GrapplePullExitReason _completionReason;

        /// <param name="anchorPoint">World-space point the hook attached to.</param>
        /// <param name="onExit">
        /// Invoked exactly once from Exit(), with the reason this activation
        /// ended. PlayerGrapple uses this as its sole signal for what to do
        /// next - begin the wall hold (Arrived) or despawn the now-obsolete
        /// GrappleHook and reset its own state back to Idle (everything
        /// else) - see PlayerGrapple.HandlePullExited.
        /// </param>
        public GrapplePullDriver(Vector3 anchorPoint, Action<GrapplePullExitReason> onExit)
        {
            _anchor = anchorPoint;
            _onExit = onExit;
        }

        // ------------------------------------------------------------------
        // IAbilityMovementDriver implementation
        // ------------------------------------------------------------------

        public void Enter(MovementStateContext ctx)
        {
            _elapsed = 0f;
            _complete = false;
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            _elapsed += deltaTime;

            Vector3 toAnchor = _anchor - ctx.PlayerTransform.position;
            float distance = toAnchor.magnitude;

            // Arrival check happens BEFORE moving this Tick so the player
            // never overshoots past the anchor and out the other side.
            if (distance <= ctx.Settings.GrappleReleaseDistance)
            {
                _completionReason = GrapplePullExitReason.Arrived;
                _complete = true;
                return;
            }

            if (_elapsed >= ctx.Settings.GrappleMaxDuration)
            {
                _completionReason = GrapplePullExitReason.TimedOut;
                _complete = true;
                return;
            }

            if (ctx.Settings.GrappleAutoReleaseOnObstruction && IsObstructed(ctx, toAnchor, distance))
            {
                _completionReason = GrapplePullExitReason.Obstructed;
                _complete = true;
                return;
            }

            Vector3 direction = toAnchor / Mathf.Max(distance, 0.0001f);

            // Instant snap to GrappleMaxPullSpeed toward the anchor every
            // Tick - no ramp-up. Partial gravity (not full suppression) is
            // then layered on top so the tow still reads as a believable arc
            // instead of a perfectly flat zipline - tune via
            // GrappleGravityScale (0 = zipline, 1 = full gravity).
            ctx.Velocity = direction * ctx.Settings.GrappleMaxPullSpeed;
            ctx.Velocity.y -= ctx.Settings.Gravity * ctx.Settings.GrappleGravityScale * deltaTime;

            // Safety clamp: gravity's contribution above can push the
            // resulting magnitude slightly past GrappleMaxPullSpeed - this
            // keeps that hard cap true even though the anchor-ward component
            // is no longer ramped.
            float speed = ctx.Velocity.magnitude;
            if (speed > ctx.Settings.GrappleMaxPullSpeed)
                ctx.Velocity = ctx.Velocity * (ctx.Settings.GrappleMaxPullSpeed / speed);

            ctx.Controller.Move(ctx.Velocity * deltaTime);
        }

        public bool IsComplete(MovementStateContext ctx) => _complete;

        public void Exit(MovementStateContext ctx, bool wasInterrupted)
        {
            // Intentionally does NOT touch ctx.Velocity - see the MOMENTUM /
            // "SLINGSHOT" CONTRACT note above. Both the natural-completion
            // path (AbilityMovementState.Tick) and the interrupted path
            // (MovementStateMachine.InterruptCurrentAction) call this exactly
            // once, satisfying the IAbilityMovementDriver contract. A caller
            // interrupting always wins over whatever _completionReason Tick()
            // last recorded.
            GrapplePullExitReason reason = wasInterrupted ? GrapplePullExitReason.Interrupted : _completionReason;
            _onExit?.Invoke(reason);
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Casts a line from roughly chest height toward the anchor. If it
        /// hits something closer than the anchor itself, the player would
        /// otherwise grind against that surface for the rest of the pull
        /// (e.g. swinging around a corner) - ending the pull early there
        /// hands back full player control (and full current velocity)
        /// instead. Excludes the player's own layer so the capsule/hitbox
        /// never self-blocks the cast.
        /// </summary>
        private bool IsObstructed(MovementStateContext ctx, Vector3 toAnchor, float distance)
        {
            int selfLayer = ctx.PlayerTransform.gameObject.layer;
            int mask = ~(1 << selfLayer);

            Vector3 origin = ctx.PlayerTransform.position + Vector3.up * (ctx.Controller.height * 0.5f);
            Vector3 anchorFromOrigin = _anchor - origin;
            float castDistance = anchorFromOrigin.magnitude;

            if (castDistance <= 0.01f)
                return false;

            // Stop just short of the anchor itself so the surface the hook
            // is embedded in doesn't count as its own obstruction.
            float tolerance = Mathf.Min(0.5f, castDistance * 0.5f);
            if (Physics.Raycast(origin, anchorFromOrigin.normalized, out RaycastHit hit, castDistance - tolerance, mask, QueryTriggerInteraction.Ignore))
                return true;

            return false;
        }
    }
}
