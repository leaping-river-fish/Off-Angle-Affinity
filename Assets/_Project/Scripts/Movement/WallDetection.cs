// =============================================================================
// WallDetection — shared wall-contact queries for AirborneState (entry) and
// WallRunningState (continuous contact). Extracted so both use identical
// raycast / angle / "same wall" rules instead of each growing a slightly
// different copy - mirroring why SlopeUtility/GroundMomentum exist.
//
// WALL IDENTITY:
//   "Same wall" is decided by collider identity first, continuous contact
//   proximity second - NEVER by comparing surface normals alone. A curved
//   mesh keeps one Collider across its entire surface, so following that
//   curve must NOT reset WallRunElapsedTime just because the normal spun.
//   Connected-but-separate colliders (two angled panels butted together)
//   can still count as the same wall when the new contact point is within
//   Settings.SameWallContactTolerance of the last known point.
//
// DETECTION WHILE WALL RUNNING:
//   Prefer casting INTO the wall along the known surface normal
//   (towardNormal). Side rays alone fail on convex curves because the wall
//   falls away from player.right as the surface bends - casting toward the
//   last known normal keeps contact across those bends.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement
{
    public enum WallSide
    {
        None,
        Left,
        Right,
    }

    public struct WallHit
    {
        public Collider Collider;
        public Vector3  Point;
        public Vector3  Normal;
        public WallSide Side;
        public float    Distance;
    }

    public static class WallDetection
    {
        // SphereCast radius as a fraction of the capsule radius - wide enough
        // to forgive glancing contact, small enough not to grab floors/edges
        // past the end of a wall.
        private const float SphereRadiusFraction = 0.25f;

        /// <summary>
        /// Finds a wall-runnable surface near the player.
        /// <paramref name="preferredSide"/> is honored when still valid
        /// (prevents Left↔Right thrash at concave corners).
        /// <paramref name="towardNormal"/> - when non-zero, also casts INTO
        /// that direction (toward the wall). WallRunningState passes the
        /// smoothed wall normal so convex curves keep contact.
        /// </summary>
        public static bool TryFindWall(
            MovementStateContext ctx,
            WallSide preferredSide,
            Vector3 towardNormal,
            out WallHit hit)
        {
            hit = default;

            Transform player = ctx.PlayerTransform;
            float halfHeight = ctx.Controller.height * 0.5f;
            Vector3 origin = player.position + Vector3.up * halfHeight;
            float distance = ctx.Settings.WallDetectionDistance;
            float sphereRadius = Mathf.Max(0.05f, ctx.Controller.radius * SphereRadiusFraction);
            int selfLayer = player.gameObject.layer;
            int mask = ~(1 << selfLayer);
            float angleTol = ctx.Settings.WallAngleTolerance;

            bool leftValid  = TryCastWall(origin, -player.right, distance, sphereRadius, mask, angleTol, out RaycastHit leftHit);
            bool rightValid = TryCastWall(origin,  player.right, distance, sphereRadius, mask, angleTol, out RaycastHit rightHit);

            bool towardValid = false;
            RaycastHit towardHit = default;
            if (towardNormal.sqrMagnitude > 0.0001f)
            {
                // Cast into the wall (opposite the outward normal). Same reach
                // as side casts - a longer cast was grabbing geometry past the
                // end of a wall and keeping the player glued until a fling.
                Vector3 intoWall = -towardNormal.normalized;
                towardValid = TryCastWall(origin, intoWall, distance, sphereRadius, mask, angleTol, out towardHit);
            }

            // Prefer the previously-held side when it is still valid so a
            // concave corner (both casts hit) does not thrash between walls.
            if (preferredSide == WallSide.Left && leftValid)
            {
                hit = BuildHit(leftHit, WallSide.Left);
                return true;
            }

            if (preferredSide == WallSide.Right && rightValid)
            {
                hit = BuildHit(rightHit, WallSide.Right);
                return true;
            }

            // While wall running, a toward-normal hit on the same continuous
            // surface beats a fresh opposite-side cast - keeps convex contact.
            if (towardValid)
            {
                WallSide side = ResolveSide(player, towardHit.normal, preferredSide);
                hit = BuildHit(towardHit, side);
                return true;
            }

            if (leftValid && rightValid)
            {
                hit = leftHit.distance <= rightHit.distance
                    ? BuildHit(leftHit, WallSide.Left)
                    : BuildHit(rightHit, WallSide.Right);
                return true;
            }

            if (leftValid)
            {
                hit = BuildHit(leftHit, WallSide.Left);
                return true;
            }

            if (rightValid)
            {
                hit = BuildHit(rightHit, WallSide.Right);
                return true;
            }

            return false;
        }

        /// <summary>Convenience overload for entry checks (no known wall normal yet).</summary>
        public static bool TryFindWall(MovementStateContext ctx, WallSide preferredSide, out WallHit hit) =>
            TryFindWall(ctx, preferredSide, Vector3.zero, out hit);

        /// <summary>
        /// True if <paramref name="collider"/> / <paramref name="point"/>
        /// represent continuous contact with the wall the player was already
        /// running on (see this file's WALL IDENTITY note).
        /// </summary>
        public static bool IsSameWall(MovementStateContext ctx, Collider collider, Vector3 point)
        {
            if (ctx.WallRunLastCollider == null)
                return false;

            if (collider == ctx.WallRunLastCollider)
                return true;

            float tolerance = ctx.Settings.SameWallContactTolerance;
            return (point - ctx.WallRunLastContactPoint).sqrMagnitude <= tolerance * tolerance;
        }

        /// <summary>
        /// World-space wall-tangent direction (horizontal along the wall)
        /// whose sign best matches <paramref name="travelHint"/>.
        /// </summary>
        public static Vector3 GetWallTangent(Vector3 wallNormal, Vector3 travelHint)
        {
            Vector3 tangent = Vector3.Cross(wallNormal, Vector3.up);
            if (tangent.sqrMagnitude < 0.0001f)
                return Vector3.zero;

            tangent.Normalize();

            Vector3 horizontalHint = new Vector3(travelHint.x, 0f, travelHint.z);
            if (horizontalHint.sqrMagnitude > 0.0001f && Vector3.Dot(tangent, horizontalHint) < 0f)
                tangent = -tangent;

            return tangent;
        }

        /// <summary>
        /// True when horizontal travel is fast enough and sufficiently
        /// alongside the wall to enter a wall run. Uses overall horizontal
        /// speed (forgiving) plus a minimum tangential share so straight-into-
        /// wall rams still fail.
        /// </summary>
        public static bool MeetsEntrySpeed(MovementStateContext ctx, Vector3 wallNormal, Vector3 horizontalVelocity)
        {
            float horizontalSpeed = horizontalVelocity.magnitude;
            if (horizontalSpeed < ctx.Settings.WallRunEntrySpeed)
                return false;

            Vector3 tangent = GetWallTangent(wallNormal, horizontalVelocity);
            if (tangent.sqrMagnitude < 0.0001f)
                return false;

            float tangentialSpeed = Mathf.Abs(Vector3.Dot(horizontalVelocity, tangent));
            // Require at least ~35% of entry speed along the wall so glancing
            // "alongside" approaches still qualify, while head-on rams don't.
            return tangentialSpeed >= ctx.Settings.WallRunEntrySpeed * 0.35f;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private static bool TryCastWall(
            Vector3 origin, Vector3 direction, float distance, float sphereRadius,
            int mask, float angleToleranceDegrees, out RaycastHit hit)
        {
            hit = default;
            if (direction.sqrMagnitude < 0.0001f)
                return false;

            direction.Normalize();

            // SphereCast first (forgiving). Fall back to a thin Raycast so a
            // sphere that starts slightly overlapping geometry still works.
            bool usedSphere = Physics.SphereCast(
                origin, sphereRadius, direction, out hit, distance, mask, QueryTriggerInteraction.Ignore);

            if (!usedSphere)
            {
                if (!Physics.Raycast(origin, direction, out hit, distance, mask, QueryTriggerInteraction.Ignore))
                    return false;
            }

            // Angle between the hit normal and world-up. A true vertical wall
            // is ~90°. Floors (~0°) and ceilings (~180°) are rejected.
            float angleFromUp = Vector3.Angle(hit.normal, Vector3.up);
            float angleFromVertical = Mathf.Abs(90f - angleFromUp);
            if (angleFromVertical > angleToleranceDegrees)
                return false;

            // SphereCast's hit.distance is how far the SPHERE CENTER traveled,
            // not the gap from origin to the surface. Convert to a true
            // origin→surface distance so wall-hug math stays accurate.
            if (usedSphere)
            {
                float surfaceDistance = Vector3.Dot(hit.point - origin, direction);
                hit.distance = Mathf.Max(0f, surfaceDistance);
            }

            return true;
        }

        private static WallSide ResolveSide(Transform player, Vector3 wallNormal, WallSide preferredSide)
        {
            // Outward normal pointing roughly toward the player's left means
            // the wall is on the left (player is to the right of the wall).
            float sideDot = Vector3.Dot(wallNormal, player.right);
            if (Mathf.Abs(sideDot) < 0.1f && preferredSide != WallSide.None)
                return preferredSide;

            return sideDot > 0f ? WallSide.Left : WallSide.Right;
        }

        private static WallHit BuildHit(RaycastHit rayHit, WallSide side) => new WallHit
        {
            Collider = rayHit.collider,
            Point    = rayHit.point,
            Normal   = rayHit.normal,
            Side     = side,
            Distance = rayHit.distance,
        };
    }
}
