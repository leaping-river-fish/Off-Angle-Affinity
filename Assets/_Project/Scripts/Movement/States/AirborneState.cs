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
//   Controller.isGrounded → CrouchingState (capsule still shrunk and/or key
//                                            still held - e.g. a crouch-jump.
//                                            CrouchAmount/height are never
//                                            touched while airborne, so this
//                                            redirect is what lets the capsule
//                                            grow back smoothly and safely
//                                            instead of snapping - see Tick())
//
// DOUBLE JUMP (Phase 2 unlock):
//   The slot is wired now. Set MovementSettings.MaxJumps = 2 to enable.
//   Double jump overrides ONLY ctx.Velocity.y — horizontal is preserved so
//   the player can redirect mid-air. This is a deliberate design choice.
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
            // ── 1. Double jump ─────────────────────────────────────────────
            // Checked first so the impulse is applied before gravity this frame,
            // which gives the jump a crisp, immediate feel.
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;

                if (ctx.RemainingJumps > 0)
                {
                    // PHASE 2: this branch is active when MaxJumps = 2.
                    // Override vertical only — horizontal from grapple/slide
                    // swings is preserved for skill-expressive direction changes.
                    float jumpVelocity = Mathf.Sqrt(2f * ctx.Settings.JumpHeight * ctx.Settings.Gravity);
                    ctx.Velocity.y = jumpVelocity;
                    ctx.RemainingJumps--;
                }
                // If no jumps remain, the request is silently consumed so it
                // does not phantom-trigger on the first frame after landing.
            }

            // ── 2. Gravity ─────────────────────────────────────────────────
            ctx.Velocity.y -= ctx.Settings.Gravity * deltaTime;

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

                // Land into Crouching (not Grounded) if the capsule is still
                // shrunk or the key is still held. This state never touches
                // CrouchAmount, so a crouch-jump keeps the capsule crouched
                // for the whole arc - only CrouchingState knows how to grow
                // it back safely (headroom-gated, smoothly lerped).
                if (ctx.CrouchAmount > 0f || ctx.IsCrouchSlideHeld)
                    ctx.StateMachine.TransitionTo(MovementStateId.Crouching);
                else
                    ctx.StateMachine.TransitionTo(MovementStateId.Grounded);
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
            Vector2 rawInput = ctx.Input.MoveInput;
            if (rawInput.sqrMagnitude < 0.001f)
                return;

            float speed = ctx.Input.IsSprinting
                ? ctx.Settings.SprintSpeed
                : ctx.Settings.WalkSpeed;

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
