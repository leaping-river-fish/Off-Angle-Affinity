// =============================================================================
// CrouchingState — hold-to-crouch, smooth capsule/height transition, gated stand-up.
// Phase 2: implemented.
//
// ENTERING THIS STATE:
//   From GroundedState (CrouchSlide + low speed) — see HandleCrouchSlide().
//   From AirborneState (landed while still crouched/held — e.g. a crouch-jump).
//   ctx.Velocity is inherited unmodified (momentum preservation contract).
//
// TRANSITIONS OUT:
//   !Controller.isGrounded  → AirborneState  (capsule stays crouched mid-air;
//                                             XZ velocity preserved, same
//                                             ledge-fall contract as Grounded)
//   CrouchAmount reaches 0  → GroundedState  (only once fully stood AND clear)
//
// STAND-UP GATE:
//   Releasing the key immediately starts the stand-up lerp (no extra delay -
//   see the pending-flag note below for why this used to stutter). Every Tick
//   while the key is released, MovementCapsuleUtility.HasHeadroom() is
//   re-checked. If blocked, the target crouch amount stays at 1 and the check
//   simply retries next Tick - the player stands up automatically the instant
//   they clear the obstruction, with no separate "queued stand" flag required.
//
// PENDING-FLAG HYGIENE (fixes a toggle-like re-crouch bug):
//   ctx.CrouchSlidePending is cleared on Enter() regardless of entry path.
//   Without this, a fresh press's pending flag could survive the entire
//   crouch and fire AGAIN in GroundedState right after standing back up -
//   crouching a second time for no reason and making a single tap look like
//   a toggle.
//
// SPAM COOLDOWN:
//   Exit() stamps ctx.NextCrouchAllowedTime = Time.time + CrouchCooldown.
//   GroundedState.HandleCrouchSlide() refuses a fresh crouch press until that
//   time passes, so rapid re-tapping can't re-trigger the transition mid-cycle
//   (Minecraft-style "you can spam it, but it's rate-limited"). Releasing the
//   key to stand is never cooldown-gated - only re-entering Crouching is.
//
// COLLISION STABILITY:
//   CrouchAmount is smoothed via Mathf.MoveTowards, and the resulting height/
//   center are applied every Tick BEFORE Controller.Move() runs. Shrinking is
//   always safe; growing is gated by HasHeadroom() above, so the controller
//   never resizes into solid geometry mid-transition.
//
// JUMP HANDLING:
//   ctx.JumpPending is explicitly consumed here (crouch-jump). This also
//   prevents a stale-flag bug: if this state didn't consume it, a jump press
//   while crouched would sit pending and fire as a surprise jump the instant
//   the player stands up.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement.States
{
    public class CrouchingState : IMovementState
    {
        public MovementStateId StateId => MovementStateId.Crouching;

        // ------------------------------------------------------------------
        // IMovementState implementation
        // ------------------------------------------------------------------

        public void Enter(MovementStateContext ctx)
        {
            // ctx.Velocity and ctx.CrouchAmount both carry over unmodified from
            // whatever state we entered from.
            //
            // CrouchSlidePending is explicitly cleared here regardless of entry
            // path, so a fresh press's pending flag never survives to fire a
            // second, unwanted crouch after this one ends - see the header
            // note above for the full symptom.
            ctx.CrouchSlidePending = false;
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            // ── 1. Ledge-fall detection (same contract as GroundedState) ────
            if (!ctx.Controller.isGrounded)
            {
                // Capsule stays at its current crouched height through the
                // fall; AirborneState does not touch it. Horizontal velocity
                // is preserved so a crouch-walk off an edge still carries speed.
                ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
                return;
            }

            // ── 2. Jump check (crouch-jump) ──────────────────────────────────
            // Consumed here so the flag never sits stale into a later state.
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;
                PerformJump(ctx);
                return;
            }

            // ── 3. Advance crouch amount toward its target ──────────────────
            // Held key always keeps us fully crouched. Released key only
            // succeeds in standing if there is headroom - re-checked every
            // Tick so standing resolves automatically once clear.
            bool wantsToStand = !ctx.IsCrouchSlideHeld;
            bool canStand     = wantsToStand && MovementCapsuleUtility.HasHeadroom(ctx);
            float target      = canStand ? 0f : 1f;

            float rate = 1f / Mathf.Max(0.01f, ctx.Settings.CrouchTransitionDuration);
            ctx.CrouchAmount = Mathf.MoveTowards(ctx.CrouchAmount, target, rate * deltaTime);

            MovementCapsuleUtility.ApplyCrouchHeight(ctx);

            // ── 4. Compute horizontal move vector at crouch speed ───────────
            // Sprint input is intentionally ignored while crouched - a single
            // enforcement point instead of a scattered "if crouching" check
            // elsewhere in the sprint-speed logic.
            Vector2 rawInput = ctx.Input.MoveInput;
            Vector3 wishDir  = ctx.PlayerTransform.right   * rawInput.x
                             + ctx.PlayerTransform.forward * rawInput.y;

            float inputMag = Mathf.Clamp01(rawInput.magnitude);
            Vector3 horizontalVelocity = wishDir.normalized * (ctx.Settings.CrouchSpeed * inputMag);

            // Same ground-press constant as GroundedState - required to keep
            // CharacterController.isGrounded stable on flat surfaces.
            ctx.Velocity = new Vector3(horizontalVelocity.x, -2f, horizontalVelocity.z);

            // ── 5. Move ───────────────────────────────────────────────────
            ctx.Controller.Move(ctx.Velocity * deltaTime);

            // ── 6. Exit once fully stood ─────────────────────────────────────
            if (ctx.CrouchAmount <= 0f)
            {
                ctx.StateMachine.TransitionTo(MovementStateId.Grounded);
            }
        }

        public void FixedTick(MovementStateContext ctx, float fixedDeltaTime)
        {
            // Reserved for future slope/surface queries, mirroring GroundedState.
        }

        public void Exit(MovementStateContext ctx)
        {
            // Start the spam cooldown the moment we're back to standing (not
            // on key release - releasing should never be delayed). A fresh
            // crouch press is refused by GroundedState.HandleCrouchSlide()
            // until this time passes.
            ctx.NextCrouchAllowedTime = Time.time + ctx.Settings.CrouchCooldown;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private void PerformJump(MovementStateContext ctx)
        {
            // Identical formula to GroundedState.PerformJump - horizontal
            // crouch-walk speed carries into the jump arc unchanged.
            float jumpVelocity = Mathf.Sqrt(2f * ctx.Settings.JumpHeight * ctx.Settings.Gravity);

            ctx.Velocity.y = jumpVelocity;
            ctx.RemainingJumps--;

            ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
        }
    }
}
