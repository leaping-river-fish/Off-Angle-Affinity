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
//   horizontal speed < SlideMinSpeed  → Crouching (if key held) else Grounded
//                                        (flat ground never decelerates on its
//                                        own now, so this mainly fires when
//                                        sliding uphill - see SPEED MODEL)
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
//   its own while sliding (steering and the duration timer still apply).
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement.States
{
    public class SlidingState : IMovementState
    {
        public MovementStateId StateId => MovementStateId.Sliding;

        // Extra distance beyond the capsule's half-height to cast the
        // ground-normal ray - generous enough to still hit gentle downslopes
        // without the ray starting inside the capsule itself.
        private const float GroundRayExtraDistance = 0.5f;

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

            // ── 4. Slope-aware speed update ───────────────────────────────
            ApplySlopeSpeed(ctx, deltaTime);

            // ── 5. Timer + minimum-speed exit checks ──────────────────────
            ctx.SlideTimer -= deltaTime;
            float horizontalSpeed = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z).magnitude;

            bool timeExpired = ctx.SlideTimer <= 0f;
            bool tooSlow      = horizontalSpeed < ctx.Settings.SlideMinSpeed;

            // ── 6. Apply ground-press constant + move ─────────────────────
            // Same CharacterController.isGrounded stability trick used by
            // GroundedState/CrouchingState.
            ctx.Velocity.y = -2f;
            ctx.Controller.Move(ctx.Velocity * deltaTime);

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

        private void ApplySlopeSpeed(MovementStateContext ctx, float deltaTime)
        {
            Vector3 horizontal = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float speed = horizontal.magnitude;
            if (speed < 0.001f) return;

            Vector3 direction = horizontal / speed;
            float slopeAccel = 0f;

            if (ctx.Settings.SlideSlopeAcceleration > 0f && TryGetGroundNormal(ctx, out Vector3 groundNormal))
            {
                // Downhill direction of the slope the player is standing on.
                // Positive dot with the slide direction = sliding downhill
                // (assist); negative = sliding uphill (resistance).
                Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
                float downhillDot = Vector3.Dot(direction, downhill);
                slopeAccel = downhillDot * ctx.Settings.SlideSlopeAcceleration;
            }

            // No baseline ambient friction: flat ground (slopeAccel == 0)
            // holds speed exactly constant ("flat speed"). Only an actual
            // slope changes it, clamped to SlideMaxSpeed so a downhill run
            // can't runaway-accelerate forever.
            float newSpeed = Mathf.Clamp(speed + slopeAccel * deltaTime, 0f, ctx.Settings.SlideMaxSpeed);

            horizontal = direction * newSpeed;
            ctx.Velocity.x = horizontal.x;
            ctx.Velocity.z = horizontal.z;
        }

        private bool TryGetGroundNormal(MovementStateContext ctx, out Vector3 normal)
        {
            float halfHeight = ctx.Controller.height * 0.5f;
            float castDistance = halfHeight + GroundRayExtraDistance;
            Vector3 origin = ctx.PlayerTransform.position + Vector3.up * halfHeight;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, castDistance,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                normal = hit.normal;
                return true;
            }

            normal = Vector3.up;
            return false;
        }
    }
}
