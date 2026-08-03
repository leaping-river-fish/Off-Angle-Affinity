// =============================================================================
// SlidingState — high-speed ground slide with steering, slope-aware friction,
// and a configurable slide-jump launch. Phase 2: implemented.
//
// ENTERING THIS STATE:
//   Via SlideEntry.TryEnterSlideOrCrouch() (see SlideEntry.cs), called from
//   either GroundedState.HandleCrouchSlide() (a fresh press while grounded)
//   or AirborneState's landing block (CrouchSlide already held on landing -
//   e.g. jump → hold slide → land). Both require horizontal speed >=
//   SlideEntrySpeedThreshold. ctx.Velocity is inherited unmodified except for
//   a downward clamp to SlideMaxSpeed if entry speed exceeds it - this
//   preserves the Sprint → Slide momentum contract (see IMovementState.cs)
//   while still giving designers a hard ceiling.
//
// TRANSITIONS OUT:
//   !Controller.isGrounded            → Airborne  (velocity preserved -
//                                                   sliding off a ledge is
//                                                   intentional, not a bug)
//   JumpPending + AllowJumpDuringSlide → Airborne  (slide-jump: horizontal
//                                                   velocity preserved,
//                                                   vertical impulse added -
//                                                   the "launch pad" feel)
//   SlideTimer <= 0                   → Crouching (if key held) else Grounded
//                                        (NEVER fires while moving meaningfully
//                                        downhill - see DURATION below)
//   horizontal speed < SlideMinSpeed  → Crouching (if key held) else Grounded
//                                        (flat ground never decelerates on its
//                                        own now, so this mainly fires when
//                                        sliding uphill - see SPEED MODEL)
//
// DURATION:
//   SlideTimer only counts down while on flat ground or moving uphill.
//   While the slide direction is meaningfully aligned with the slope's
//   downhill direction (see ApplySlopeSpeed's isMovingDownhill /
//   DownhillDotThreshold), the timer is frozen - a long downhill run is
//   never cut short by SlideDuration (which is tuned for flat-ground
//   slides). The slide still ends the moment speed drops below
//   SlideMinSpeed or the player leaves the ground, downhill or not.
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
//   Flat ground holds speed CONSTANT - this state applies no ambient ground
//   friction of its own. A single downward raycast each Tick finds the
//   ground normal beneath the player; the slope's downhill direction then
//   either assists (accelerates) or resists (decelerates) the current slide
//   direction, scaled by SlideSlopeAcceleration. Set SlideSlopeAcceleration
//   to 0 to disable slope sensitivity entirely - speed then never changes on
//   its own while sliding (steering and the duration timer still apply, and
//   duration is then never treated as "downhill" either, since that also
//   depends on the same ground-normal/SlideSlopeAcceleration-gated query).
//
//   Sliding meaningfully downhill is NOT capped at SlideMaxSpeed - it
//   accelerates continuously up to MaxPreservedSpeed (the same global
//   momentum safety ceiling GroundMomentum uses for grapples/bunny-hops),
//   so a long slope keeps building speed instead of plateauing almost
//   immediately. SlideMaxSpeed still applies once the player is on flat
//   ground or moving uphill - but never by retroactively clamping speed
//   already earned downhill back down; that speed simply holds (flat) or
//   bleeds off via uphill resistance/SlideMinSpeed like normal.
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

        // Minimum downhill alignment (dot product with the slope's downhill
        // direction) required before a slide counts as "moving downhill" for
        // the infinite-duration rule below - filters out near-flat/contour
        // strafing so the timer doesn't flicker on/off near dot ≈ 0.
        private const float DownhillDotThreshold = 0.15f;

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

            // Clamp entry speed DOWN to the slide ceiling only; never clamp
            // up. A slide entered below the ceiling keeps its true entry
            // speed (Sprint → Slide momentum contract).
            Vector3 horizontal = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float speed = horizontal.magnitude;
            if (speed > ctx.Settings.SlideMaxSpeed)
            {
                horizontal = horizontal.normalized * ctx.Settings.SlideMaxSpeed;
                ctx.Velocity.x = horizontal.x;
                ctx.Velocity.z = horizontal.z;
            }
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
            bool isMovingDownhill = ApplySlopeSpeed(ctx, deltaTime, onSlope, groundNormal);

            // ── 5. Timer + minimum-speed exit checks ──────────────────────
            // A slide moving meaningfully downhill never expires by time -
            // only by slowing below SlideMinSpeed (e.g. the slope flattens
            // out) or by leaving the ground. Without this, a long downhill
            // slide would still get cut short mid-descent purely because
            // SlideDuration (tuned for flat-ground slides) ran out. Flat
            // ground and uphill slides use the normal timer unchanged.
            if (!isMovingDownhill)
                ctx.SlideTimer -= deltaTime;

            float horizontalSpeed = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z).magnitude;

            bool timeExpired = !isMovingDownhill && ctx.SlideTimer <= 0f;
            bool tooSlow      = horizontalSpeed < ctx.Settings.SlideMinSpeed;

            // ── 6. Slope-tangent vertical velocity + move ─────────────────
            ApplyGroundVelocityY(ctx, onSlope, groundNormal);

            // Folds a ground-snap correction into this SAME Move() call - see
            // SlopeUtility.MoveWithGroundSnap for why it must not be a
            // separate Move() call (it would corrupt Controller.velocity).
            SlopeUtility.MoveWithGroundSnap(ctx, ctx.Velocity * deltaTime, ctx.Settings.GroundSnapDistance);

            // ── 7. Exit ────────────────────────────────────────────────────
            if (timeExpired || tooSlow)
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
            // the player nudge direction. Normalized once and reused instead
            // of calling wishDir.normalized twice.
            Vector3 wishDirNormalized = wishDir.normalized;
            float accel = ctx.Settings.SlideSteerAcceleration * ctx.SpeedMultiplier;
            ctx.Velocity.x += wishDirNormalized.x * accel * deltaTime;
            ctx.Velocity.z += wishDirNormalized.z * accel * deltaTime;
        }

        /// <summary>Returns true if the player is currently sliding meaningfully downhill (see DownhillDotThreshold) - used to gate the infinite-duration rule in Tick().</summary>
        private bool ApplySlopeSpeed(MovementStateContext ctx, float deltaTime, bool onSlope, Vector3 groundNormal)
        {
            Vector3 horizontal = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float speed = horizontal.magnitude;
            if (speed < 0.001f) return false;

            Vector3 direction = horizontal / speed;
            float slopeAccel = 0f;
            bool isMovingDownhill = false;

            if (ctx.Settings.SlideSlopeAcceleration > 0f && onSlope)
            {
                // Downhill direction of the slope the player is standing on.
                // Positive dot with the slide direction = sliding downhill
                // (assist); negative = sliding uphill (resistance).
                Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
                float downhillDot = Vector3.Dot(direction, downhill);
                slopeAccel = downhillDot * ctx.Settings.SlideSlopeAcceleration;
                isMovingDownhill = downhillDot > DownhillDotThreshold;
            }

            // No baseline ambient friction: flat ground (slopeAccel == 0)
            // holds speed exactly constant ("flat speed"). Only an actual
            // slope changes it.
            //
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
            // preserved (and decays naturally via uphill resistance/duration/
            // SlideMinSpeed) instead of being instantly clamped down to
            // SlideMaxSpeed.
            float ceiling = isMovingDownhill ? ctx.Settings.MaxPreservedSpeed : ctx.Settings.SlideMaxSpeed;
            ceiling = Mathf.Max(speed, ceiling);
            float newSpeed = Mathf.Clamp(speed + slopeAccel * deltaTime, 0f, ceiling);

            horizontal = direction * newSpeed;
            ctx.Velocity.x = horizontal.x;
            ctx.Velocity.z = horizontal.z;

            return isMovingDownhill;
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
