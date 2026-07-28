// =============================================================================
// GroundedState — walk, sprint, jump, ledge-fall, and CrouchSlide detection.
// Phase 1: fully implemented.
//
// TRANSITIONS OUT:
//   Jump pressed              → AirborneState   (applies jump impulse first)
//   !Controller.isGrounded   → AirborneState   (walked off ledge; XZ preserved)
//   CrouchSlide + high speed → SlidingState    (gated by ctx.NextCrouchAllowedTime
//                                                AND ctx.NextSlideAllowedTime -
//                                                see HandleCrouchSlide()/SlideEntry.cs)
//   CrouchSlide + low speed  → CrouchingState  (gated by ctx.NextCrouchAllowedTime -
//                                                see HandleCrouchSlide())
//
// NOTE: landing while CrouchSlide is still held (e.g. jump → hold slide →
// land) resolves through the SAME SlideEntry.TryEnterSlideOrCrouch() speed
// check used here, via AirborneState's landing block - so a fast
// jump-into-slide chain reaches SlidingState, not just a fresh ground press.
// The one exception is a true crouch-jump (already crouched before jumping),
// which AirborneState always redirects into CrouchingState regardless of
// speed, since the capsule is still shrunk. Grounded can assume
// IsCrouchSlideHeld is false the moment Tick() starts either way.
//
// MOMENTUM CONTRACT:
//   - ctx.Velocity.y is set to -2 each Tick to keep isGrounded reliable.
//     This is a CharacterController quirk: without a downward component,
//     isGrounded can flicker on flat surfaces.
//   - ctx.Velocity.x/z reflect actual horizontal movement this frame -
//     computed via GroundMomentum.ComputeHorizontalVelocity(), which only
//     clamps to the walk/sprint cap when AT or BELOW it; speed carried in
//     from a jump chain or slide-jump above that cap is preserved and only
//     slowly decays (BunnyHopMomentumDecay), which is what makes bunny
//     hopping actually reward chained jumps instead of losing all momentum
//     the instant the player touches ground.
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
            // Reset jump budget. Defensive backstop for grounded-entries that
            // don't come from AirborneState's landing detection (e.g.
            // standing back up from Crouching, or ending a Slide) - those are
            // already grounded so this is a harmless no-op in practice. The
            // primary reset point is centralized in AirborneState.Tick().
            ctx.RemainingJumps = ctx.EffectiveMaxJumps;

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
            // InputLocked mutes player-driven input (see MovementStateContext)
            // without disabling the whole component - discard pending flags
            // so they don't queue up and fire the instant input unlocks.
            if (ctx.InputLocked)
            {
                ctx.JumpPending = false;
                ctx.CrouchSlidePending = false;
            }

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
            float speed = (ctx.Input.IsSprinting
                ? ctx.Settings.SprintSpeed
                : ctx.Settings.WalkSpeed) * ctx.SpeedMultiplier;

            Vector2 rawInput = ctx.InputLocked ? Vector2.zero : ctx.Input.MoveInput;
            Vector3 wishDir  = ctx.PlayerTransform.right   * rawInput.x
                             + ctx.PlayerTransform.forward * rawInput.y;

            // Clamp to [0,1] so diagonal keyboard input (which has magnitude ~1.41)
            // does not exceed the intended speed. Analog sticks already return
            // values <= 1, so this is future-proof for controller support.
            float inputMag = Mathf.Clamp01(rawInput.magnitude);

            // Bunny hop: if the player landed carrying more speed than the
            // walk/sprint cap (from a jump chain or slide-jump), preserve it
            // instead of snapping back down to cap - see GroundMomentum.cs.
            Vector3 horizontalVelocity = GroundMomentum.ComputeHorizontalVelocity(ctx, wishDir, inputMag, speed, deltaTime);

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

            // Speed-gated Slide-vs-Crouch decision, and slide's own
            // independent cooldown, live in SlideEntry so AirborneState's
            // landing-while-held case resolves identically - see SlideEntry.cs.
            // If fast enough for a slide but slide's cooldown isn't up yet,
            // this does nothing (no fallback to Crouching) - the player keeps
            // walking/sprinting until the cooldown clears.
            SlideEntry.TryEnterSlideOrCrouch(ctx);
        }
    }
}
