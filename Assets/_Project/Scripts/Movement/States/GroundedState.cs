// =============================================================================
// GroundedState — walk, sprint, jump, ledge-fall, and CrouchSlide detection.
// Phase 1: fully implemented.
//
// TRANSITIONS OUT:
//   Jump pressed              → AirborneState   (applies jump impulse first)
//   !Controller.isGrounded   → AirborneState   (walked off ledge; XZ preserved)
//   CrouchSlide + high speed → SlidingState    (Phase 2 — still stubbed)
//   CrouchSlide + low speed  → CrouchingState  (Phase 2: implemented; gated by
//                                                ctx.NextCrouchAllowedTime - see
//                                                HandleCrouchSlide())
//
// NOTE: landing while CrouchSlide is still held (e.g. a crouch-jump) is
// handled by AirborneState redirecting straight into CrouchingState -
// Grounded is never entered in that case, so this state can assume
// IsCrouchSlideHeld is false the moment Tick() starts.
//
// MOMENTUM CONTRACT:
//   - ctx.Velocity.y is set to -2 each Tick to keep isGrounded reliable.
//     This is a CharacterController quirk: without a downward component,
//     isGrounded can flicker on flat surfaces.
//   - ctx.Velocity.x/z reflect actual horizontal movement this frame.
//   - On jump: ctx.Velocity.y is set to the impulse BEFORE TransitionTo().
//     AirborneState.Enter() does NOT recompute it — it inherits exactly this.
//   - Ledge fall: XZ velocity is preserved as-is so the player launches off
//     edges at their current run speed (intentional; enables ledge combos).
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement.States
{
    public class GroundedState : IMovementState
    {
        public MovementStateId StateId => MovementStateId.Grounded;

        // ------------------------------------------------------------------
        // IMovementState implementation
        // ------------------------------------------------------------------

        public void Enter(MovementStateContext ctx)
        {
            // Reset jump budget on every landing
            ctx.RemainingJumps = ctx.Settings.MaxJumps;

            // Clear vertical velocity accumulated during the fall.
            // XZ is intentionally preserved: a player sprinting into a landing
            // should keep their horizontal momentum (foundation for slide-on-land,
            // Phase 2). Hard landing effects belong in an animation/VFX layer.
            ctx.Velocity.y = 0f;

            // INVARIANT: Grounded always means fully standing (CrouchAmount
            // already 0, capsule already at StandingHeight/StandingCenter).
            // CrouchingState only transitions here once CrouchAmount reaches
            // 0, and AirborneState redirects a crouched/held landing into
            // CrouchingState instead of here (see AirborneState.Tick()). Do
            // NOT force-reset the capsule here - an un-gated reset could grow
            // it into geometry without ever checking headroom.
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            // ── 1. CrouchSlide check ───────────────────────────────────────
            // Consume before jump so simultaneous press of both favors jump.
            if (ctx.CrouchSlidePending)
            {
                ctx.CrouchSlidePending = false;
                HandleCrouchSlide(ctx);

                // If HandleCrouchSlide triggered a transition, bail out so we
                // do not move the player on the same frame as the state change.
                if (ctx.StateMachine.CurrentStateId != MovementStateId.Grounded)
                    return;
            }

            // ── 2. Jump check ──────────────────────────────────────────────
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;
                PerformJump(ctx);
                return; // PerformJump always calls TransitionTo(Airborne)
            }

            // ── 3. Ledge-fall detection ────────────────────────────────────
            // isGrounded reflects the result of the previous frame's Move call.
            // If false here, the player walked off a surface since last frame.
            if (!ctx.Controller.isGrounded)
            {
                // Do not modify ctx.Velocity — preserve sprint/walk horizontal
                // speed so the player launches off the edge at full velocity.
                ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
                return;
            }

            // ── 4. Compute horizontal move vector ─────────────────────────
            float speed = ctx.Input.IsSprinting
                ? ctx.Settings.SprintSpeed
                : ctx.Settings.WalkSpeed;

            Vector2 rawInput = ctx.Input.MoveInput;
            Vector3 wishDir  = ctx.PlayerTransform.right   * rawInput.x
                             + ctx.PlayerTransform.forward * rawInput.y;

            // Clamp to [0,1] so diagonal keyboard input (which has magnitude ~1.41)
            // does not exceed the intended speed. Analog sticks already return
            // values <= 1, so this is future-proof for controller support.
            float inputMag = Mathf.Clamp01(rawInput.magnitude);
            Vector3 horizontalVelocity = wishDir.normalized * (speed * inputMag);

            // ── 5. Apply ground-press constant ────────────────────────────
            // A small downward component is required every Move call to keep
            // CharacterController.isGrounded stable on flat surfaces.
            // -2 m/s is imperceptible to the player but sufficient for the API.
            ctx.Velocity = new Vector3(horizontalVelocity.x, -2f, horizontalVelocity.z);

            // ── 6. Move ───────────────────────────────────────────────────
            ctx.Controller.Move(ctx.Velocity * deltaTime);
        }

        public void FixedTick(MovementStateContext ctx, float fixedDeltaTime)
        {
            // Ground detection uses Controller.isGrounded (polled in Tick).
            // Phase 2 slope-angle queries and slide surface normals go here.
        }

        public void Exit(MovementStateContext ctx) { }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private void PerformJump(MovementStateContext ctx)
        {
            // Derive required exit velocity from desired apex height.
            // Formula: v = sqrt(2 * h * g), from kinematic equations.
            float jumpVelocity = Mathf.Sqrt(2f * ctx.Settings.JumpHeight * ctx.Settings.Gravity);

            // Override only vertical — horizontal speed flows into the jump arc
            // unchanged. This means a sprinting player jumps further than a
            // walking one, which is both physically correct and good game feel.
            ctx.Velocity.y = jumpVelocity;
            ctx.RemainingJumps--;

            ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
        }

        private void HandleCrouchSlide(MovementStateContext ctx)
        {
            // Spam cooldown: refuse a fresh press until CrouchCooldown seconds
            // have passed since we last stood back up (stamped by
            // CrouchingState.Exit()). Only gates a NEW press through this
            // method - never releasing the key to stand.
            if (Time.time < ctx.NextCrouchAllowedTime)
                return;

            // Measure horizontal speed only. ctx.Velocity.y is the -2 ground
            // press constant, not actual downward movement, so exclude it.
            float horizontalSpeed = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z).magnitude;

            if (horizontalSpeed >= ctx.Settings.SlideEntrySpeedThreshold)
            {
                // PHASE 2: uncomment when SlidingState is created and registered.
                // ctx.StateMachine.TransitionTo(MovementStateId.Sliding);
            }
            else
            {
                ctx.StateMachine.TransitionTo(MovementStateId.Crouching);
            }
        }
    }
}
