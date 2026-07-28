// =============================================================================
// GrapplePullDriver — IAbilityMovementDriver that pulls the player toward a
// fixed anchor point (the surface a GrappleHook attached to), Titanfall/Just
// Cause style: a straight acceleration toward the anchor, not a pendulum
// swing. Driven through MovementStateMachine.BeginAbilityMovement() /
// MovementStateId.AbilityMovement - see IAbilityMovementDriver.cs.
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
// velocity has been built up by Tick()'s acceleration is exactly what the
// player carries into Grounded/Airborne the instant this driver ends,
// whether that's a natural arrival, a timeout, an obstruction cutoff, or
// PlayerGrapple.HandleGrappleCanceled() calling
// MovementStateMachine.InterruptCurrentAction() early. That is the whole
// "grapple and cancel but keep the momentum" slingshot mechanic - no special
// case is needed here to make cancelling feel different from arriving.
// =============================================================================

using System;
using OffAngle.Movement;
using UnityEngine;

namespace OffAngle.Movement.Grapple
{
    public class GrapplePullDriver : IAbilityMovementDriver
    {
        private readonly Vector3 _anchor;
        private readonly Action<bool> _onExit;

        private float _elapsed;
        private bool _complete;

        /// <param name="anchorPoint">World-space point the hook attached to.</param>
        /// <param name="onExit">
        /// Invoked exactly once from Exit(), with the same wasInterrupted value
        /// Exit() itself received. PlayerGrapple uses this as its sole signal
        /// to despawn the now-obsolete GrappleHook and reset its own state
        /// back to Idle - see PlayerGrapple.HandlePullExited.
        /// </param>
        public GrapplePullDriver(Vector3 anchorPoint, Action<bool> onExit)
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
                _complete = true;
                return;
            }

            if (_elapsed >= ctx.Settings.GrappleMaxDuration)
            {
                _complete = true;
                return;
            }

            if (ctx.Settings.GrappleAutoReleaseOnObstruction && IsObstructed(ctx, toAnchor, distance))
            {
                _complete = true;
                return;
            }

            Vector3 direction = toAnchor / Mathf.Max(distance, 0.0001f);

            // Partial gravity (not full suppression) keeps a believable arc
            // instead of a perfectly flat zipline-style tow - tune via
            // GrappleGravityScale (0 = zipline, 1 = full gravity).
            ctx.Velocity.y -= ctx.Settings.Gravity * ctx.Settings.GrappleGravityScale * deltaTime;

            // Accelerate toward the anchor, then clamp the RESULTING speed
            // (not just the anchor-ward component) so gravity's contribution
            // is included in the cap - this is what stops a long vertical
            // pull from quietly exceeding GrappleMaxPullSpeed.
            ctx.Velocity += direction * ctx.Settings.GrapplePullAcceleration * deltaTime;
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
            // once, satisfying the IAbilityMovementDriver contract.
            _onExit?.Invoke(wasInterrupted);
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
