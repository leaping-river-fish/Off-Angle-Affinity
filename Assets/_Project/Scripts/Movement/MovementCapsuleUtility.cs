// =============================================================================
// MovementCapsuleUtility — shared low-profile capsule math.
//
// Extracted so every state that shrinks the CharacterController (Crouching now;
// Sliding/Prone later - see IMovementState.cs Phase 2/3 notes) resizes the
// capsule and checks overhead clearance identically. Without this, each new
// low-profile state would grow its own slightly-different copy of this math.
//
// SAFETY CONTRACT:
//   Shrinking the capsule (going toward CrouchHeight) is always safe - a
//   smaller capsule cannot newly intersect geometry that the larger one
//   didn't already clear. Growing back to standing is the only direction
//   that requires a check, which is why HasHeadroom() exists and callers
//   must gate growth on it every Tick (not just once on key release).
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement
{
    public static class MovementCapsuleUtility
    {
        /// <summary>
        /// True if there is enough vertical space to grow the capsule from its
        /// CURRENT height back to full standing height.
        ///
        /// Only tests the region being newly grown into - from the capsule's
        /// current top up to where the top would sit at StandingHeight - never
        /// the region near the feet. The capsule shrinks/grows from the top
        /// with the bottom anchored at the feet (see ApplyCrouchHeight), so
        /// that's the only region that isn't already legally occupied. This
        /// also means the query never comes near the floor, so it can't
        /// false-positive against the ground itself.
        /// </summary>
        public static bool HasHeadroom(MovementStateContext ctx)
        {
            CharacterController controller = ctx.Controller;

            float currentTop  = controller.height;
            float standingTop = ctx.StandingHeight;

            // Already at (or somehow above) standing height - nothing to grow
            // into, so there is nothing that could possibly block it.
            if (standingTop <= currentTop)
                return true;

            // Trim by skin width so the query doesn't false-positive against
            // geometry the controller is already normally allowed to touch.
            float radius = Mathf.Max(0.01f, controller.radius - controller.skinWidth);

            Vector3 bottom = ctx.PlayerTransform.position + Vector3.up * (currentTop  - radius);
            Vector3 top    = ctx.PlayerTransform.position + Vector3.up * (standingTop - radius);

            return !Physics.CheckCapsule(bottom, top, radius, ctx.Settings.StandCheckMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Applies ctx.CrouchAmount (0 = standing, 1 = fully crouched) to the
        /// CharacterController's height/center. Center is always recomputed as
        /// height * 0.5 so the capsule shrinks from the top with feet planted -
        /// matching the "center.y = height/2, root at feet" convention already
        /// used for the standing capsule (see PlayerController.cs header).
        /// </summary>
        public static void ApplyCrouchHeight(MovementStateContext ctx)
        {
            float height = Mathf.Lerp(ctx.StandingHeight, ctx.Settings.CrouchHeight, ctx.CrouchAmount);
            ctx.Controller.height = height;
            ctx.Controller.center = new Vector3(0f, height * 0.5f, 0f);
        }
    }
}
