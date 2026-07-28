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
//   any specific jump count. Double jump overrides ONLY ctx.Velocity.y —
//   horizontal is preserved so the player can redirect mid-air. This is a
//   deliberate design choice. The jump budget itself is reset on landing
//   below (Tick(), step 5) rather than in GroundedState/CrouchingState.Enter() -
//   this is the single point that covers landing into either state.
//
// WALL RUN (Phase 3 hook):
//   FixedTick() is the intended location for wall-detection raycasts.
//   When WallRunningState is implemented, add the raycast logic here and
//   call TransitionTo(WallRunning) when a qualifying surface is found.
//
// AIR CONTROL MODEL:
//   Acceleration-based: each frame, a wish-direction vector is computed and
//   added at AirAcceleration × AirControlMultiplier m/s². The result is then
//   clamped to the current move speed cap. This model:
//     - Preserves momentum injected by grapples or slide-jumps (speed > cap
//       is not clipped downward, only excess acceleration is blocked)
//     - Gives the player partial steering without full ground-level control
//     - Can be tuned independently of ground speed via AirAcceleration
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
            //
            // PHASE 3 NOTE: WallRunningState will Enter() from here after a
            // successful wall-contact detection. It reads ctx.Velocity to seed
            // the wall-parallel run speed, so do not touch XZ here.
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            // InputLocked mutes player-driven input without disabling the
            // whole component - discard the jump request so it can't queue
            // up and fire the instant input unlocks (see MovementStateContext).
            if (ctx.InputLocked)
                ctx.JumpPending = false;

            // ── 1. Double jump ─────────────────────────────────────────────
            // Checked first so the impulse is applied before gravity this frame,
            // which gives the jump a crisp, immediate feel.
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;

                if (ctx.RemainingJumps > 0)
                {
                    // Active whenever EffectiveMaxJumps > 1. Override vertical
                    // only — horizontal from grapple/slide swings is preserved
                    // for skill-expressive direction changes.
                    float jumpVelocity = Mathf.Sqrt(2f * ctx.Settings.JumpHeight * ctx.Settings.Gravity);
                    ctx.Velocity.y = jumpVelocity;
                    ctx.RemainingJumps--;
                }
                // If no jumps remain, the request is silently consumed so it
                // does not phantom-trigger on the first frame after landing.
            }

            // ── 2. Gravity ─────────────────────────────────────────────────
            ctx.Velocity.y -= ctx.Settings.Gravity * ctx.GravityMultiplier * deltaTime;

            // ── 3. Air control (horizontal) ────────────────────────────────
            ApplyAirControl(ctx, deltaTime);

            // ── 4. Move ────────────────────────────────────────────────────
            ctx.Controller.Move(ctx.Velocity * deltaTime);

            // ── 5. Landing detection ───────────────────────────────────────
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

        public void FixedTick(MovementStateContext ctx, float fixedDeltaTime)
        {
            // PHASE 3: wall-detection raycasts belong here.
            // Cast left and right from the player; if a qualifying surface is
            // found (normal roughly perpendicular to gravity, speed sufficient),
            // call ctx.StateMachine.TransitionTo(MovementStateId.WallRunning).
            //
            // Example structure (do not implement until Phase 3):
            // bool hitLeft  = Physics.Raycast(origin, -ctx.PlayerTransform.right, ...);
            // bool hitRight = Physics.Raycast(origin,  ctx.PlayerTransform.right, ...);
            // if ((hitLeft || hitRight) && horizontalSpeed >= ctx.Settings.WallRunMinSpeed)
            //     ctx.StateMachine.TransitionTo(MovementStateId.WallRunning);
        }

        public void Exit(MovementStateContext ctx) { }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private void ApplyAirControl(MovementStateContext ctx, float deltaTime)
        {
            Vector2 rawInput = ctx.InputLocked ? Vector2.zero : ctx.Input.MoveInput;
            if (rawInput.sqrMagnitude < 0.001f)
                return;

            float speed = (ctx.Input.IsSprinting
                ? ctx.Settings.SprintSpeed
                : ctx.Settings.WalkSpeed) * ctx.SpeedMultiplier;

            Vector3 wishDir = ctx.PlayerTransform.right   * rawInput.x
                            + ctx.PlayerTransform.forward * rawInput.y;

            // Accelerate toward wish direction. Using acceleration (not direct
            // set) preserves momentum from slides and grapple swings while still
            // giving the player meaningful steering influence.
            float accel = ctx.Settings.AirAcceleration * ctx.Settings.AirControlMultiplier;
            ctx.Velocity.x += wishDir.normalized.x * accel * deltaTime;
            ctx.Velocity.z += wishDir.normalized.z * accel * deltaTime;

            // Clamp horizontal speed to the move speed cap.
            // NOTE: This only prevents the player from accelerating beyond cap
            // via input. Momentum injected by grapples or slide-jumps that
            // exceeds this value is preserved — we do not clamp downward.
            Vector2 horizontal = new Vector2(ctx.Velocity.x, ctx.Velocity.z);
            float   targetCap  = speed * Mathf.Clamp01(rawInput.magnitude);

            if (horizontal.magnitude > targetCap)
            {
                // Only clamp if the player's input-driven component is
                // responsible for the excess, not existing momentum.
                // Simple approach: clamp if we're also accelerating in the
                // same direction as the current velocity.
                Vector2 wishDir2D = new Vector2(wishDir.x, wishDir.z).normalized;
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
