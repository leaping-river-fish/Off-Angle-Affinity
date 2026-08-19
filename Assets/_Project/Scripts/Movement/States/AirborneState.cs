// =============================================================================
// AirborneState — gravity accumulation, air control, landing, double jump slot.
// Phase 1: fully implemented.
//
// ENTERING THIS STATE:
//   From GroundedState (jump)  : ctx.Velocity.y already set to jump impulse.
//   From GroundedState (ledge) : ctx.Velocity carries horizontal run speed.
//   Enter() does NOT touch Velocity — it inherits whatever the source set.
//   This is the momentum preservation contract (see IMovementState.cs).
//
// TRANSITIONS OUT:
//   Controller.isGrounded → GroundedState  (clears Y before exit; only when
//                                            the capsule is already fully
//                                            standing and the key isn't held)
//   Controller.isGrounded → CrouchingState (capsule STILL SHRUNK from before
//                                            this jump - i.e. a true
//                                            crouch-jump. CrouchAmount/height
//                                            are never touched while airborne,
//                                            so this redirect is what lets the
//                                            capsule grow back smoothly and
//                                            safely instead of snapping - see
//                                            Tick())
//   Controller.isGrounded → SlidingState   (capsule NOT shrunk, but CrouchSlide
//                                            is held - e.g. jump → hold slide →
//                                            land - AND horizontal speed clears
//                                            SlideEntrySpeedThreshold. Resolved
//                                            by the same SlideEntry helper
//                                            GroundedState uses, so a fast
//                                            jump-into-slide chain does not get
//                                            silently downgraded into a slow
//                                            Crouch - see Tick()/SlideEntry.cs)
//
// DOUBLE JUMP (Phase 2: implemented):
//   Set MovementSettings.MaxJumps >= 2 for a base double/triple/etc. jump, or
//   grant it at runtime via MovementStateMachine.AddMaxJumpsBonus() (see
//   MovementStateContext.EffectiveMaxJumps) - nothing here is hardcoded to
//   any specific jump count. Double jump always overrides ctx.Velocity.y.
//   Horizontal is preserved ONLY if the player holds roughly the same
//   direction they were already travelling in (see ApplyDoubleJumpRedirect
//   and MovementSettings.DoubleJumpRedirectDotThreshold) - redirecting into a
//   sufficiently different held direction snaps velocity toward that new
//   direction and cuts speed (scaled by how sharp the turn is, see
//   DoubleJumpReversalRetention), so a full reversal costs much more speed
//   than a slight sidestep. No held input at all leaves momentum untouched.
//   The jump budget itself is reset on landing below (Tick(), step 5) rather
//   than in GroundedState/CrouchingState.Enter() - this is the single point
//   that covers landing into either state.
//
// WALL RUN (Phase 3: implemented):
//   Tick() after Move() is the single gateway into WallRunningState (checked
//   against the post-move position so contact is reliable). Uses
//   WallDetection.TryFindWall + MeetsEntrySpeed. Refuses ALL walls while
//   Time.time < WallRunExitLockEndTime (tutorial exitingWall), then also
//   refuses same-wall reattachment within WallReattachCooldown of
//   WallRunExitTime.
//
// AIR CONTROL MODEL:
//   Two regimes, split at GroundMomentum.GetMomentumThreshold (see
//   GroundMomentum.cs and MovementSettings.NormalMaxSpeed):
//     - AT OR BELOW the threshold: classic acceleration-based air control,
//       unchanged from before the momentum system existed - each frame, a
//       wish-direction vector is added at AirAcceleration ×
//       AirControlMultiplier m/s², then clamped to the current move speed
//       cap × inputMag.
//     - ABOVE the threshold (momentum carried in from a grapple release,
//       slide-jump, or bunny-hop chain): hands off entirely to
//       GroundMomentum.ComputeAirborneMomentumVelocity, which steers via
//       AirMomentumSteerAcceleration and decays via AirMomentumDecay -
//       deliberately much weaker than the ground equivalents, so speed is
//       preserved far longer in the air than on the ground and normal
//       movement input can never instantly snap it back down to the cap.
//       This replaced an old clamp that measured the dot product between
//       wish direction and current velocity and snapped straight down to
//       the cap whenever they roughly matched - which fired on nearly every
//       "hold forward after a grapple release" case and was the primary
//       cause of momentum feeling like it got cancelled by normal input.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement.States
{
    public class AirborneState : IMovementState
    {
        public MovementStateId StateId => MovementStateId.Airborne;

        // ------------------------------------------------------------------
        // IMovementState implementation
        // ------------------------------------------------------------------

        public void Enter(MovementStateContext ctx)
        {
            // Intentionally empty — Velocity is owned by the previous state.
            // WallRunningState.Enter() reads ctx.Velocity to seed wall-parallel
            // run speed when transitioning from here, so do not touch XZ.
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            // InputLocked mutes player-driven input without disabling the
            // whole component - discard the jump request so it can't queue
            // up and fire the instant input unlocks (see MovementStateContext).
            if (ctx.InputLocked)
                ctx.JumpPending = false;

            // "Grounded" debuff (e.g. Hadal Zone) - blocks the double-jump
            // charge below; the upward-velocity clamp after gravity (step 2)
            // is what actually prevents an already-airborne player (e.g. a
            // jump started the instant before the debuff applied) from
            // carrying that momentum upward. See MovementStateContext.GroundedLocked.
            if (ctx.GroundedLocked)
                ctx.JumpPending = false;

            // ── 1. Double jump ─────────────────────────────────────────────
            // Checked first so the impulse is applied before gravity this frame,
            // which gives the jump a crisp, immediate feel.
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;

                if (ctx.RemainingJumps > 0)
                {
                    // Active whenever EffectiveMaxJumps > 1. Always overrides
                    // vertical; horizontal is only preserved as-is when the
                    // held direction roughly matches current travel - see
                    // ApplyDoubleJumpRedirect for the "different direction"
                    // case.
                    ctx.Velocity.y = ctx.Settings.JumpVelocity;
                    ctx.RemainingJumps--;

                    ApplyDoubleJumpRedirect(ctx);
                }
                // If no jumps remain, the request is silently consumed so it
                // does not phantom-trigger on the first frame after landing.
            }

            // ── 2. Gravity ─────────────────────────────────────────────────
            ctx.Velocity.y -= ctx.Settings.Gravity * ctx.GravityMultiplier * deltaTime;

            // "Grounded" debuff: strip any remaining upward velocity every
            // Tick so gravity always wins - covers a jump/knockback that was
            // already in progress when the debuff applied, not just new
            // jump attempts (handled above).
            if (ctx.GroundedLocked && ctx.Velocity.y > 0f)
                ctx.Velocity.y = 0f;

            // ── 3. Air control (horizontal) ────────────────────────────────
            ApplyAirControl(ctx, deltaTime);

            // ── 4. Move ────────────────────────────────────────────────────
            ctx.Controller.Move(ctx.Velocity * deltaTime);

            // ── 5. Wall-run entry (post-Move so contact is current) ────────
            // Must run before landing so a frame that both brushes a wall and
            // kisses the ground still prefers ground. Only attempts entry
            // while still airborne after this frame's Move.
            if (!ctx.Controller.isGrounded && TryEnterWallRun(ctx))
                return;

            // ── 6. Landing detection ───────────────────────────────────────
            // isGrounded is updated by CharacterController.Move() above, so
            // this check reflects the result of this frame's movement.
            if (ctx.Controller.isGrounded)
            {
                ctx.Velocity.y = 0f;

                // Centralized jump-budget reset: this is the single point
                // where the player transitions from Airborne into ANY
                // grounded-family state (Grounded, Crouching, or Sliding
                // below), so resetting here - rather than only in
                // GroundedState.Enter() - guarantees every landing refills
                // the jump budget, including a landing that redirects
                // straight into Crouching (a crouch-jump), which
                // GroundedState.Enter() never sees.
                ctx.RemainingJumps = ctx.EffectiveMaxJumps;

                // Centralized bunny-hop grace stamp: same "single point for
                // every landing" reasoning as RemainingJumps above - starts
                // the window during which GroundedState/CrouchingState still
                // gradually decay/steer excess momentum instead of
                // hard-stopping it (see GroundMomentum.OnLanded).
                GroundMomentum.OnLanded(ctx);

                // A press queued the instant before landing (or stale from a
                // tap-then-release while still airborne) must not leak into
                // GroundedState's Tick() and fire a second, unwanted
                // crouch/slide next frame - same hygiene as
                // CrouchingState/SlidingState.Enter().
                ctx.CrouchSlidePending = false;

                if (ctx.CrouchAmount > 0f)
                {
                    // Capsule is still shrunk from BEFORE this jump (a true
                    // crouch-jump) - always return to Crouching regardless of
                    // speed. Sliding would need the capsule at full height,
                    // and this state never touches CrouchAmount mid-air.
                    ctx.StateMachine.TransitionTo(MovementStateId.Crouching);
                }
                else if (ctx.IsCrouchSlideHeld)
                {
                    // Held CrouchSlide while airborne (e.g. jump → hold slide
                    // → land) must resolve exactly like a fresh ground press:
                    // fast enough → Sliding, otherwise → Crouching. Without
                    // routing this through SlideEntry, landing while holding
                    // the key ALWAYS forced Crouching regardless of speed -
                    // a jump-into-slide chain would silently downgrade into a
                    // slow crouch instead of the fast slide the player expects.
                    // Same NextCrouchAllowedTime spam gate as GroundedState's
                    // fresh-press path - a refusal here just lands Grounded.
                    bool entered = Time.time >= ctx.NextCrouchAllowedTime
                        && SlideEntry.TryEnterSlideOrCrouch(ctx);

                    if (!entered)
                        ctx.StateMachine.TransitionTo(MovementStateId.Grounded);
                }
                else
                {
                    ctx.StateMachine.TransitionTo(MovementStateId.Grounded);
                }
            }
        }

        public void FixedTick(MovementStateContext ctx, float fixedDeltaTime) { }

        public void Exit(MovementStateContext ctx) { }

        /// <summary>
        /// Attempts TransitionTo(WallRunning) when a qualifying wall is beside
        /// the player. Returns true if the transition happened (caller should
        /// stop processing this Tick). See WALL RUN header note.
        /// </summary>
        private bool TryEnterWallRun(MovementStateContext ctx)
        {
            if (ctx.InputLocked)
                return false;

            // "Grounded" debuff (e.g. Hadal Zone) - see MovementStateContext.GroundedLocked.
            if (ctx.GroundedLocked)
                return false;

            // Universal post-exit lock (tutorial exitingWall) - blocks ALL
            // walls briefly so a wall jump cannot instantly re-stick.
            if (Time.time < ctx.WallRunExitLockEndTime)
                return false;

            if (!WallDetection.TryFindWall(ctx, WallSide.None, out WallHit wall))
                return false;

            // Same-wall reattachment cooldown: jumping away must not
            // immediately re-stick after the universal lock expires.
            bool sameWall = WallDetection.IsSameWall(ctx, wall.Collider, wall.Point);
            if (sameWall && Time.time < ctx.WallRunExitTime + ctx.Settings.WallReattachCooldown)
                return false;

            Vector3 horizontal = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            if (!WallDetection.MeetsEntrySpeed(ctx, wall.Normal, horizontal))
                return false;

            ctx.StateMachine.TransitionTo(MovementStateId.WallRunning);
            return true;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// A double jump held toward a direction different from the player's
        /// current horizontal travel redirects momentum into that new
        /// direction instead of fully preserving the old one - the sharper
        /// the turn, the more speed is cut. Skips entirely (leaves ctx.Velocity
        /// untouched) when there is no held direction to redirect toward, or
        /// when the held direction is already close enough to current travel
        /// (dot >= DoubleJumpRedirectDotThreshold) - straight double jumps and
        /// slight steering keep their full reach exactly like before.
        /// </summary>
        private void ApplyDoubleJumpRedirect(MovementStateContext ctx)
        {
            Vector3 rawWishDir = GroundMomentum.GetWishDirection(ctx, out float inputMag);
            if (inputMag * inputMag < 0.001f)
                return; // No held direction to redirect toward.

            Vector3 horizontal = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float speed = horizontal.magnitude;
            if (speed < 0.01f)
                return; // Not moving horizontally - nothing to redirect.

            Vector3 wishDir = rawWishDir.normalized;
            float dot = Vector3.Dot(horizontal / speed, wishDir);

            if (dot >= ctx.Settings.DoubleJumpRedirectDotThreshold)
                return; // Close enough to the current heading - full momentum preserved.

            // Map how sharp the redirect is (dot from the threshold down to -1,
            // a full reversal) onto a speed-retention fraction. Direction snaps
            // to the new wish direction - the momentum CHANGES, it does not
            // just decelerate along the old path.
            float t = Mathf.InverseLerp(ctx.Settings.DoubleJumpRedirectDotThreshold, -1f, dot);
            float retention = Mathf.Lerp(1f, ctx.Settings.DoubleJumpReversalRetention, t);

            Vector3 redirected = wishDir * (speed * retention);
            ctx.Velocity.x = redirected.x;
            ctx.Velocity.z = redirected.z;
        }

        private void ApplyAirControl(MovementStateContext ctx, float deltaTime)
        {
            Vector3 wishDir = GroundMomentum.GetWishDirection(ctx, out float inputMag);
            Vector3 horizontal3 = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float currentSpeed = horizontal3.magnitude;
            float momentumThreshold = GroundMomentum.GetMomentumThreshold(ctx);

            // ABOVE the momentum threshold: never clamp back down to the
            // normal cap. Preserve the trajectory, let input steer it
            // gradually, and bleed off only the excess via AirMomentumDecay -
            // see GroundMomentum.cs and this file's AIR CONTROL MODEL note.
            if (currentSpeed > momentumThreshold + ctx.Settings.MomentumTolerance)
            {
                Vector3 result = GroundMomentum.ComputeAirborneMomentumVelocity(ctx, horizontal3, wishDir, inputMag, deltaTime);
                ctx.Velocity.x = result.x;
                ctx.Velocity.z = result.z;
                return;
            }

            // AT OR BELOW the threshold: classic acceleration-based air
            // control, unchanged from before the momentum system existed.
            if (inputMag * inputMag < 0.001f)
                return;

            // "Grounded" debuff caps this at walk speed - see MovementStateContext.GroundedLocked.
            bool sprinting = ctx.Input.IsSprinting && !ctx.GroundedLocked;
            float speed = (sprinting
                ? ctx.Settings.SprintSpeed
                : ctx.Settings.WalkSpeed) * ctx.SpeedMultiplier;

            // Normalized once and reused below instead of calling
            // wishDir.normalized twice (x and z components separately).
            Vector3 wishDirNormalized = wishDir.normalized;

            // Accelerate toward wish direction. Using acceleration (not direct
            // set) preserves momentum from slides and grapple swings while still
            // giving the player meaningful steering influence.
            float accel = ctx.Settings.AirAcceleration * ctx.Settings.AirControlMultiplier;
            ctx.Velocity.x += wishDirNormalized.x * accel * deltaTime;
            ctx.Velocity.z += wishDirNormalized.z * accel * deltaTime;

            // Clamp horizontal speed to the move speed cap.
            // NOTE: This only prevents the player from accelerating beyond cap
            // via input. Momentum injected by grapples or slide-jumps that
            // exceeds this value is handled by the branch above and never
            // reaches this clamp.
            Vector2 horizontal = new Vector2(ctx.Velocity.x, ctx.Velocity.z);
            float   targetCap  = speed * inputMag;

            if (horizontal.magnitude > targetCap)
            {
                // Only clamp if the player's input-driven component is
                // responsible for the excess, not existing momentum.
                // Simple approach: clamp if we're also accelerating in the
                // same direction as the current velocity. This also
                // preserves Quake-style air-strafing below the cap: strafing
                // at an angle to current velocity (dot <= 0) is deliberately
                // left unclamped, exactly as before the momentum system.
                Vector2 wishDir2D = new Vector2(wishDirNormalized.x, wishDirNormalized.z);
                float   dot       = Vector2.Dot(horizontal.normalized, wishDir2D);

                if (dot > 0f)
                {
                    horizontal  = horizontal.normalized * targetCap;
                    ctx.Velocity.x = horizontal.x;
                    ctx.Velocity.z = horizontal.y;
                }
            }
        }
    }
}
