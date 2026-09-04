// =============================================================================
// SlidingState — high-speed ground slide with steering, slope-aware friction,
// and a configurable slide-jump launch. Phase 2: implemented.
//
// ENTERING THIS STATE:
//   Via SlideEntry.TryEnterSlideOrCrouch() (see SlideEntry.cs), called from
//   either GroundedState.HandleCrouchSlide() (a fresh press while grounded)
//   or AirborneState's landing block (CrouchSlide already held on landing -
//   e.g. jump → hold slide → land). Both require horizontal speed >=
//   SlideEntrySpeedThreshold. Horizontal speed is then raised to at least
//   SlideMaxSpeed (the sprint-into-slide boost) so a flat slide starts
//   faster than a sprint instead of immediately bleeding off from walk/
//   sprint speed. Fast entries (grapple, downhill carry, bunny-hop) keep
//   their speed; SlideMaxSpeed is never applied as a snap-down. Excess
//   speed then decelerates via flat/uphill drag (or keeps building if the
//   slide is already downhill). MaxPreservedSpeed is still a hard safety
//   ceiling, same role it plays in GroundMomentum.
//
// TRANSITIONS OUT:
//   !Controller.isGrounded            → Airborne  (velocity preserved -
//                                                   sliding off a ledge is
//                                                   intentional, not a bug)
//   JumpPending + AllowJumpDuringSlide → Airborne  (slide-jump: horizontal
//                                                   velocity preserved,
//                                                   vertical impulse added -
//                                                   the "launch pad" feel)
//   horizontal speed < SlideMinSpeed  → Crouching (if key held) else Grounded
//                                        (gated by the minimum-duration timer
//                                        - see DURATION below)
//
// DURATION:
//   SlideTimer is a MINIMUM lock, not a maximum. It always counts down from
//   Settings.SlideDuration and only gates the SlideMinSpeed exit - a slide
//   cannot end for being too slow until the timer elapses. After that the
//   slide continues for as long as horizontal speed stays at or above
//   SlideMinSpeed (downhill, leftover momentum, etc.). Leaving the ground
//   or slide-jumping still exits immediately.
//
// CAPSULE:
//   Deliberately does NOT touch CharacterController height/center. Crouch
//   already owns capsule-shrinking (see MovementCapsuleUtility) and has a
//   documented "ghost hitbox" networking problem for non-owner peers
//   (NetworkPlayerCrouch) that a second shrinking state would reintroduce.
//   Sliding is movement-only; add a network-synced profile change later if
//   a lowered slide silhouette is ever required.
//
// SLIDE RESTRICTIONS:
//   AllowFireDuringSlide/AllowReloadDuringSlide/AllowAbilitiesDuringSlide are
//   NOT enforced here - this state only owns movement. They are exposed as
//   read-only queries on MovementStateMachine (CanFire/CanReload/
//   CanUseMovementAbilities) for the weapon/ability systems to consult
//   themselves, per the "don't reach into other systems" rule.
//
// SPEED MODEL:
//   Flat ground decelerates at SlideFlatDeceleration. A single downward
//   raycast each Tick finds the ground normal beneath the player; the
//   slope's downhill direction then either assists (accelerates) or resists
//   (decelerates) the current slide direction, scaled by
//   SlideSlopeAcceleration - the same angle factor in both directions.
//   Uphill stacks that resistance on top of the flat deceleration, so a
//   steeper climb bleeds speed faster than flat ground, matching how a
//   steeper descent builds speed faster. Set SlideSlopeAcceleration to 0 to
//   disable the slope term (flat deceleration still applies). Set
//   SlideFlatDeceleration to 0 to disable the flat term.
//
//   Sliding meaningfully downhill is NOT capped at SlideMaxSpeed - it
//   accelerates continuously up to MaxPreservedSpeed (the same global
//   momentum safety ceiling GroundMomentum uses for grapples/bunny-hops),
//   so a long slope keeps building speed instead of plateauing almost
//   immediately. SlideMaxSpeed still applies once the player is on flat
//   ground or moving uphill - but never by retroactively clamping speed
//   already earned downhill back down; that speed simply bleeds off via
//   flat/uphill deceleration until it drops below SlideMinSpeed.
//
// GROUND CONTACT:
//   The same ground-normal query also drives the Y component of the applied
//   Move() vector (see ApplyGroundVelocityY): instead of a purely horizontal
//   move + a flat -2 ground-press constant, Y is solved so the move vector
//   stays tangent to the slope surface. Without this, a fast slide down a
//   steep slope would repeatedly outrun the flat -2 press, detach from the
//   ground, and re-snap onto it every frame or two - visible as a "bumpy"
//   slide. Flat ground and uphill slopes are unaffected (clamped to the same
//   -2 as before); only downhill gets the extra pull-down.
//
//   The tangent-Y solve is still only a linear approximation of ONE raycast
//   sample per Tick, so it can leave a tiny leftover gap against a curved or
//   faceted real slope. SlopeUtility.MoveWithGroundSnap() folds a corrective
//   downward nudge into that SAME Move() call (see MovementSettings.
//   GroundSnapDistance) - this is what actually prevents the leftover gap
//   from reading as Controller.isGrounded == false on the NEXT Tick, which
//   would otherwise bounce the state machine into AirborneState and back
//   the instant ground is re-detected (a visible bump on its own, on top of
//   whatever the tangent-Y solve already smoothed over). A real ledge/drop
//   beyond GroundSnapDistance is untouched - isGrounded still correctly
//   goes false and this state exits to Airborne as intended. Must stay a
//   SINGLE Move() call, not a separate corrective one afterwards - see
//   MoveWithGroundSnap's doc comment for why a second call corrupts
//   Controller.velocity (this previously broke SpeedHeightHUD).
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement.States
{
    public class SlidingState : IMovementState
    {
        public MovementStateId StateId => MovementStateId.Sliding;

        // Below this ground-normal.y, the slope is too steep/unreliable to
        // solve the tangent-velocity formula against (near-vertical
        // surfaces blow the divide up) - fall back to the flat ground-press
        // constant instead. CharacterController.slopeLimit already prevents
        // standing on anything this steep in practice.
        private const float MinSlopeNormalYForTangentVelocity = 0.2f;

        // ------------------------------------------------------------------
        // IMovementState implementation
        // ------------------------------------------------------------------

        public void Enter(MovementStateContext ctx)
        {
            ctx.SlideTimer = ctx.Settings.SlideDuration;

            // Same pending-flag hygiene as CrouchingState.Enter() - a fresh
            // press queued the instant before Sliding started must not fire
            // again once this state exits.
            ctx.CrouchSlidePending = false;

            // Raise slow entries up to SlideMaxSpeed so sprint-into-slide
            // is a speed boost, then decelerates from there. Do NOT clamp
            // faster entries down to SlideMaxSpeed; Tick's slope/flat
            // speed model decelerates (or accelerates) from whatever we
            // arrived with. MaxPreservedSpeed is the only hard safety cap.
            Vector3 horizontal = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float speed = horizontal.magnitude;
            float minEntry = ctx.Settings.SlideMaxSpeed * ctx.SpeedMultiplier;
            float maxEntry = ctx.Settings.MaxPreservedSpeed;
            if (minEntry > maxEntry)
                minEntry = maxEntry;

            if (speed < 0.001f)
            {
                Vector3 fallback = ctx.PlayerTransform.forward;
                fallback.y = 0f;
                if (fallback.sqrMagnitude < 0.001f)
                    fallback = Vector3.forward;
                horizontal = fallback.normalized * minEntry;
            }
            else if (speed < minEntry)
            {
                horizontal *= minEntry / speed;
            }
            else if (speed > maxEntry)
            {
                horizontal *= maxEntry / speed;
            }

            ctx.Velocity.x = horizontal.x;
            ctx.Velocity.z = horizontal.z;
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            // A repeated CrouchSlide press while already sliding has nothing
            // to do - consumed unconditionally so it cannot leak into the
            // next state and re-trigger a slide/crouch the instant this ends.
            ctx.CrouchSlidePending = false;

            if (ctx.InputLocked)
                ctx.JumpPending = false;

            // ── 1. Ledge-fall detection ────────────────────────────────────
            if (!ctx.Controller.isGrounded)
            {
                ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
                return;
            }

            // ── 2. Slide-jump ──────────────────────────────────────────────
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;

                if (ctx.Settings.AllowJumpDuringSlide)
                {
                    PerformSlideJump(ctx);
                    return;
                }
                // Disallowed by settings: silently consumed, same "no
                // phantom jump later" contract as AirborneState's
                // exhausted-charges branch.
            }

            // ── 3. Steering ─────────────────────────────────────────────────
            ApplySteering(ctx, deltaTime);

            // Single ground-normal query reused by both the slope speed
            // update (step 4) and the slope-tangent vertical velocity
            // (step 6) - avoids raycasting twice per Tick for the same data.
            bool onSlope = SlopeUtility.TryGetGroundNormal(ctx, out Vector3 groundNormal);

            // ── 4. Slope-aware speed update ───────────────────────────────
            ApplySlopeSpeed(ctx, deltaTime, onSlope, groundNormal);

            // ── 5. Minimum-duration lock + speed exit ─────────────────────
            // SlideDuration is a floor, not a ceiling: the slide always lasts
            // at least that long, then continues until speed drops below
            // SlideMinSpeed (or the player leaves the ground / slide-jumps).
            ctx.SlideTimer -= deltaTime;

            float horizontalSpeed = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z).magnitude;
            bool minDurationElapsed = ctx.SlideTimer <= 0f;
            bool tooSlow = horizontalSpeed < ctx.Settings.SlideMinSpeed;

            // ── 6. Slope-tangent vertical velocity + move ─────────────────
            ApplyGroundVelocityY(ctx, onSlope, groundNormal);

            // Folds a ground-snap correction into this SAME Move() call - see
            // SlopeUtility.MoveWithGroundSnap for why it must not be a
            // separate Move() call (it would corrupt Controller.velocity).
            SlopeUtility.MoveWithGroundSnap(ctx, ctx.Velocity * deltaTime, ctx.Settings.GroundSnapDistance);

            // ── 7. Exit ────────────────────────────────────────────────────
            if (minDurationElapsed && tooSlow)
            {
                ctx.StateMachine.TransitionTo(
                    ctx.IsCrouchSlideHeld ? MovementStateId.Crouching : MovementStateId.Grounded);
            }
        }

        public void FixedTick(MovementStateContext ctx, float fixedDeltaTime)
        {
            // Ground-normal query lives in Tick() (ApplyFriction) rather than
            // here, since CharacterController.Move only runs from Tick() and
            // the normal is only needed at the same cadence.
        }

        public void Exit(MovementStateContext ctx)
        {
            ctx.SlideTimer = 0f;
            ctx.NextSlideAllowedTime = Time.time + ctx.Settings.SlideCooldown;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private void PerformSlideJump(MovementStateContext ctx)
        {
            if (ctx.RemainingJumps <= 0) return;

            // Same JumpVelocity as GroundedState.PerformJump - horizontal
            // slide speed carries fully into the jump arc, giving the
            // "launch pad" feel documented in IMovementState.cs.
            ctx.Velocity.y = ctx.Settings.JumpVelocity;
            ctx.RemainingJumps--;

            ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
        }

        private void ApplySteering(MovementStateContext ctx, float deltaTime)
        {
            Vector3 wishDir = GroundMomentum.GetWishDirection(ctx, out float inputMag);
            if (inputMag * inputMag < 0.001f)
                return;

            // Same acceleration-based steering model as AirborneState's air
            // control - preserves slide speed/momentum while still letting
            // the player nudge direction. Magnitude is not allowed to grow
            // here; speed changes belong to ApplySlopeSpeed (flat/uphill
            // drag, downhill assist). Normalized once and reused instead of
            // calling wishDir.normalized twice.
            float originalSpeed = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z).magnitude;
            Vector3 wishDirNormalized = wishDir.normalized;
            float accel = ctx.Settings.SlideSteerAcceleration * ctx.SpeedMultiplier;
            ctx.Velocity.x += wishDirNormalized.x * accel * deltaTime;
            ctx.Velocity.z += wishDirNormalized.z * accel * deltaTime;

            Vector3 steered = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float steeredSpeed = steered.magnitude;
            if (steeredSpeed > originalSpeed && steeredSpeed > 0.001f)
            {
                steered *= originalSpeed / steeredSpeed;
                ctx.Velocity.x = steered.x;
                ctx.Velocity.z = steered.z;
            }
        }

        /// <summary>Applies flat-ground drag and slope assist/resistance. Any downhill alignment skips the flat drag and raises the speed ceiling.</summary>
        private void ApplySlopeSpeed(MovementStateContext ctx, float deltaTime, bool onSlope, Vector3 groundNormal)
        {
            Vector3 horizontal = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float speed = horizontal.magnitude;
            if (speed < 0.001f) return;

            Vector3 direction = horizontal / speed;
            float slopeAccel = 0f;
            bool isMovingDownhill = false;

            if (ctx.Settings.SlideSlopeAcceleration > 0f && onSlope)
            {
                // Downhill direction of the slope the player is standing on.
                // Positive dot with the slide direction = sliding downhill
                // (assist); negative = sliding uphill (resistance). Magnitude
                // scales with slope angle the same way in both directions.
                Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
                float downhillDot = Vector3.Dot(direction, downhill);
                slopeAccel = downhillDot * ctx.Settings.SlideSlopeAcceleration;
                // Any downhill alignment assists; do not require a dead-zone
                // or shallow descents pick up flat drag and feel like they
                // slow down on the way down.
                isMovingDownhill = downhillDot > 0f;
            }

            // Flat and uphill bleed speed. Any downhill alignment keeps the
            // angle-scaled assist only - no extra SlideFlatDeceleration, so
            // a descent never net-decelerates from the flat term.
            if (!isMovingDownhill)
                slopeAccel -= ctx.Settings.SlideFlatDeceleration;

            // Sliding meaningfully downhill accelerates continuously past
            // SlideMaxSpeed, capped only by MaxPreservedSpeed (the same
            // global safety ceiling GroundMomentum uses for grapples/
            // bunny-hops) - a long slope should keep rewarding the player
            // with more speed instead of hitting SlideMaxSpeed almost
            // immediately and plateauing. Flat ground/uphill still respect
            // SlideMaxSpeed as the normal ceiling. Either way, the ceiling
            // is never allowed to be LOWER than the speed already carried in
            // (Mathf.Max) - so the moment a fast downhill run levels out
            // onto flat ground or starts climbing, that earned speed is
            // preserved (and decays via flat/uphill drag until it drops
            // below SlideMinSpeed) instead of being instantly clamped down
            // to SlideMaxSpeed.
            float ceiling = isMovingDownhill ? ctx.Settings.MaxPreservedSpeed : ctx.Settings.SlideMaxSpeed;
            ceiling = Mathf.Max(speed, ceiling);
            float newSpeed = Mathf.Clamp(speed + slopeAccel * deltaTime, 0f, ceiling);

            horizontal = direction * newSpeed;
            ctx.Velocity.x = horizontal.x;
            ctx.Velocity.z = horizontal.z;
        }

        /// <summary>
        /// Sets the vertical component of ctx.Velocity used for this Tick's
        /// Move() call. On a slope, solves for the Y that keeps
        /// (Velocity.x, Y, Velocity.z) exactly tangent to the ground plane -
        /// a horizontal-only move vector combined with the flat -2
        /// ground-press constant can't keep pace with a steep/fast downhill
        /// slope, so the CharacterController repeatedly detaches from and
        /// re-snaps onto the surface each frame (the "bumpy" slide). Clamped
        /// to never be LESS steep than the flat -2 constant (Mathf.Min), so
        /// flat ground (tangent Y = 0) and uphill (tangent Y > 0) fall back
        /// to exactly the old flat behavior - CharacterController's own
        /// collision-and-slide already climbs walkable uphill slopes fine;
        /// only the downhill case needed the extra pull-down. Only ever
        /// touches Velocity.y, so this has zero effect on the horizontal
        /// steering/slope-speed/exit-condition math above.
        /// </summary>
        private void ApplyGroundVelocityY(MovementStateContext ctx, bool onSlope, Vector3 groundNormal)
        {
            if (onSlope && groundNormal.y > MinSlopeNormalYForTangentVelocity)
            {
                float tangentY = -(groundNormal.x * ctx.Velocity.x + groundNormal.z * ctx.Velocity.z) / groundNormal.y;
                ctx.Velocity.y = Mathf.Min(tangentY, -2f);
            }
            else
            {
                ctx.Velocity.y = -2f;
            }
        }
    }
}
