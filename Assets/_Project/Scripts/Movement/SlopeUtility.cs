// =============================================================================
// SlopeUtility — shared slope-detection and slope-speed math for every state
// that cares about the ground surface beneath the player (SlidingState today;
// GroundedState/CrouchingState for their instant slope speed-cap scaling).
// Extracted so the ground-normal raycast has one implementation instead of
// each state growing its own slightly-different copy - mirroring why
// MovementCapsuleUtility/GroundMomentum exist.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement
{
    public static class SlopeUtility
    {
        // Extra distance beyond the capsule's half-height to cast the
        // ground-normal ray - generous enough to still hit gentle downslopes
        // without the ray starting inside the capsule itself.
        private const float GroundRayExtraDistance = 0.5f;

        /// <summary>
        /// Raycasts straight down from the player's approximate capsule
        /// center to find the ground surface normal beneath them. Returns
        /// false (normal = Vector3.up) if nothing is hit within range.
        /// </summary>
        public static bool TryGetGroundNormal(MovementStateContext ctx, out Vector3 normal)
        {
            float halfHeight = ctx.Controller.height * 0.5f;
            float castDistance = halfHeight + GroundRayExtraDistance;
            Vector3 origin = ctx.PlayerTransform.position + Vector3.up * halfHeight;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, castDistance,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                normal = hit.normal;
                return true;
            }

            normal = Vector3.up;
            return false;
        }

        /// <summary>
        /// Instant speed multiplier for the given normalized wish direction,
        /// based on the slope beneath the player. Returns 1 (no change) on
        /// flat ground, when no ground is found, or when there is no wish
        /// direction to evaluate. Used by GroundedState/CrouchingState to
        /// scale their walk/sprint/crouch speed cap the same responsive,
        /// instant way flat-ground speed already works - see
        /// MovementSettings.SlopeUphillSpeedMultiplier/SlopeDownhillSpeedMultiplier.
        ///
        /// Effect strength scales with two independent factors, multiplied
        /// together:
        ///   - Slope steepness, normalized against Controller.slopeLimit (0 at
        ///     flat ground, full effect at the steepest walkable angle - reuses
        ///     the existing CharacterController tunable instead of adding a
        ///     duplicate "max slope angle" setting).
        ///   - How directly wishDirNormalized points down/up the slope (dot
        ///     product with the slope's downhill direction) - strafing across
        ///     a slope face is unaffected; running straight down/up it gets
        ///     the full configured multiplier.
        /// </summary>
        public static float GetSlopeSpeedMultiplier(MovementStateContext ctx, Vector3 wishDirNormalized)
        {
            if (wishDirNormalized.sqrMagnitude < 0.0001f || !TryGetGroundNormal(ctx, out Vector3 normal))
                return 1f;

            float angleFactor = Mathf.Clamp01(Vector3.Angle(Vector3.up, normal) / Mathf.Max(1f, ctx.Controller.slopeLimit));
            if (angleFactor <= 0f)
                return 1f;

            Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, normal).normalized;
            float downhillDot = Vector3.Dot(wishDirNormalized, downhill); // -1 uphill .. +1 downhill

            float targetMultiplier = downhillDot >= 0f
                ? Mathf.Lerp(1f, ctx.Settings.SlopeDownhillSpeedMultiplier, downhillDot)
                : Mathf.Lerp(1f, ctx.Settings.SlopeUphillSpeedMultiplier, -downhillDot);

            return Mathf.Lerp(1f, targetMultiplier, angleFactor);
        }

        /// <summary>
        /// Moves the controller by <paramref name="motion"/>, folding in an
        /// extra downward nudge to close any small vertical gap between the
        /// capsule's bottom and the ground - in a SINGLE
        /// CharacterController.Move() call. Every ground-family state
        /// (Grounded/Crouching/Sliding) computes motion from a velocity that
        /// only ever APPROXIMATES the real ground surface (a single raycast
        /// sample per Tick, projected/solved against as one flat plane) - it
        /// can't perfectly track a curved or faceted slope every frame,
        /// especially at high slide speed. Any leftover gap would otherwise
        /// read as Controller.isGrounded == false on the very next Tick,
        /// which immediately bounces the state machine into AirborneState
        /// and back out again the moment ground is re-detected - a visible
        /// "bump" even though the velocity math itself was only off by a
        /// tiny amount.
        ///
        /// MUST be a single Move() call rather than a separate corrective
        /// Move() afterwards: CharacterController.velocity is derived from
        /// whatever the MOST RECENT Move() call did that frame, divided by
        /// Time.deltaTime. A second, tiny, deltaTime-independent snap
        /// distance passed to its own Move() call would overwrite that
        /// reading with garbage (e.g. a purely-vertical snap of 0.02m over a
        /// 0.016s frame reports as "1.25 m/s straight down, 0 horizontal" -
        /// exactly what broke SpeedHeightHUD, which reads Controller.velocity
        /// directly). Folding the snap into the same motion vector keeps
        /// that reading correct.
        ///
        /// The gap is measured from the PREDICTED post-move XZ position
        /// (current position + motion.x/z), not the current position, so a
        /// fast slide snaps against the ground it's about to be over, not
        /// the ground behind it.
        ///
        /// Leaves the gap untouched (Controller.isGrounded reports honestly)
        /// when there is no gap, when the gap exceeds
        /// <paramref name="maxSnapDistance"/> (an actual ledge/drop - falling
        /// off it is correct, not a bug - see SlidingState's TRANSITIONS OUT),
        /// or while ctx.Velocity.y is positive (a deliberate jump/launch
        /// impulse that snapping must never fight).
        /// </summary>
        public static void MoveWithGroundSnap(MovementStateContext ctx, Vector3 motion, float maxSnapDistance)
        {
            if (maxSnapDistance > 0f && ctx.Velocity.y <= 0f)
            {
                CharacterController controller = ctx.Controller;
                float skin = controller.skinWidth;

                // Slightly smaller than the real capsule radius (same trim as
                // MovementCapsuleUtility.HasHeadroom) so the cast doesn't
                // false-positive against geometry the controller is already
                // normally allowed to touch.
                float queryRadius = Mathf.Max(0.01f, controller.radius - skin);
                Vector3 predictedPosition = ctx.PlayerTransform.position + new Vector3(motion.x, 0f, motion.z);
                Vector3 origin = predictedPosition + Vector3.up * controller.radius;

                if (Physics.SphereCast(origin, queryRadius, Vector3.down, out RaycastHit hit,
                        maxSnapDistance + skin, ~0, QueryTriggerInteraction.Ignore))
                {
                    // hit.distance is measured against the trimmed query
                    // radius, so it reads slightly larger than the capsule's
                    // true gap by roughly `skin` - subtract it back out.
                    float gap = hit.distance - skin;
                    if (gap > 0.001f && gap <= maxSnapDistance)
                        motion.y -= gap;
                }
            }

            ctx.Controller.Move(motion);
        }
    }
}
