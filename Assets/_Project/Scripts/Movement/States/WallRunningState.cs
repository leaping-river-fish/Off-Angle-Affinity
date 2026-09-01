// =============================================================================
// WallRunningState — wall-parallel locomotion with curve-aware contact tracking,
// free wall-kick, and per-wall duration. Phase 3: implemented.
//
// ENTERING THIS STATE:
//   From AirborneState.Tick() (after Move) when a qualifying wall is found at
//   entry speed (see WallDetection.MeetsEntrySpeed) and neither the universal
//   WallJumpExitLock nor the same-wall reattach cooldown is blocking.
//   Vertical velocity is zeroed on enter so the player sticks at the height
//   they attached - default wall run is horizontal only.
//   Air-jump charges are refreshed to EffectiveMaxJumps - 1 (the same budget
//   left after a ground jump) so a free wall kick can be followed by a
//   redirect double jump into the next wall. MaxJumps == 1 grants nothing.
//
// TRANSITIONS OUT:
//   JumpPending                         → Airborne  (tutorial-style impulse:
//                                                     preserve XZ, zero Y, add
//                                                     up + look-scaled side kick)
//   Tangential speed < WallRunMinSpeed  → Airborne
//   Realized capsule speed blocked      → Airborne  (planned velocity still
//                                                     high but Move is eaten
//                                                     by a corner / facet)
//   WallRunElapsedTime >= WallRunDuration → Airborne
//   Lost wall contact                   → Airborne  (soft exit)
//   Controller.isGrounded               → Grounded
//
// EXIT LOCK:
//   Exit() stamps WallRunExitLockEndTime (blocks ALL wall entry briefly) and
//   WallRunExitTime (same-wall cooldown). Matches WallRunningAdvanced's
//   exitingWall timer without Rigidbody/DOTween.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement.States
{
    public class WallRunningState : IMovementState
    {
        public MovementStateId StateId => MovementStateId.WallRunning;

        private bool     _active;
        private WallSide _side;
        private Vector3  _smoothedNormal;
        private Vector3  _tangent;
        private float    _blockedTime;

        // Max meters of wall-gap closed per Tick when hugging curved geometry.
        private const float MaxWallHugCorrection = 0.2f;

        // Seconds of "planned speed is fine but the capsule is not moving"
        // before dropping. Absorbs a mesh-seam hitch; a true 0 m/s corner
        // still drops within a few frames.
        private const float BlockedDropTime = 0.08f;

        // Minimum side peel as a fraction of WallJumpOutwardForce when look
        // is along/into the wall - enough to clear the surface, not enough to
        // override forward momentum.
        private const float MinWallJumpSideFraction = 0.25f;

        // ------------------------------------------------------------------
        // IMovementState implementation
        // ------------------------------------------------------------------

        public void Enter(MovementStateContext ctx)
        {
            _active = false;

            if (!WallDetection.TryFindWall(ctx, WallSide.None, out WallHit wall))
                return;

            _active = true;
            _blockedTime = 0f;
            _side = wall.Side;
            _smoothedNormal = wall.Normal;
            ctx.WallRunSide = _side;

            Vector3 travelHint = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            if (travelHint.sqrMagnitude < 0.0001f)
                travelHint = ctx.PlayerTransform.forward;

            _tangent = WallDetection.GetWallTangent(wall.Normal, travelHint);

            if (!WallDetection.IsSameWall(ctx, wall.Collider, wall.Point))
                ctx.WallRunElapsedTime = 0f;

            ctx.WallRunLastCollider = wall.Collider;
            ctx.WallRunLastContactPoint = wall.Point;

            // Refresh the air-jump budget (not a full landing reset). Wall kick
            // is free and does not consume RemainingJumps, so restoring
            // EffectiveMaxJumps - 1 leaves exactly the post-ground-jump charge
            // count available for a redirect after the kick.
            int airJumpBudget = ctx.EffectiveMaxJumps - 1;
            if (airJumpBudget > 0)
                ctx.RemainingJumps = Mathf.Max(ctx.RemainingJumps, airJumpBudget);

            float tangentialSpeed = Vector3.Dot(ctx.Velocity, _tangent);
            if (tangentialSpeed < 0f)
            {
                _tangent = -_tangent;
                tangentialSpeed = -tangentialSpeed;
            }

            // Floor slow entries so attachment feels committed, but do NOT
            // clamp fast entries down to WallRunMaxSpeed here - Tick()'s own
            // Mathf.MoveTowards(tangentialSpeed, maxSpeed, accel * deltaTime)
            // already decays excess speed toward the cap at WallRunAcceleration,
            // the same rate it accelerates slow entries up. Clamping here
            // pre-empted that and turned a fast attach into an instant snap.
            // MaxPreservedSpeed is still a hard safety ceiling, same role it
            // plays in GroundMomentum.
            tangentialSpeed = Mathf.Max(tangentialSpeed, ctx.Settings.WallRunEntrySpeed);
            tangentialSpeed = Mathf.Min(tangentialSpeed, ctx.Settings.MaxPreservedSpeed);

            Vector3 runVelocity = _tangent * tangentialSpeed;
            ctx.Velocity = new Vector3(runVelocity.x, 0f, runVelocity.z);

            float desiredGap = ctx.Controller.radius + ctx.Controller.skinWidth;
            float gapError = wall.Distance - desiredGap;
            if (Mathf.Abs(gapError) > 0.001f)
                ctx.Controller.Move(-wall.Normal * gapError);
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            if (!_active)
            {
                ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
                return;
            }

            if (ctx.InputLocked)
                ctx.JumpPending = false;

            // ── 1. Free wall-kick ──────────────────────────────────────────
            if (ctx.JumpPending)
            {
                ctx.JumpPending = false;
                PerformWallJump(ctx);
                return;
            }

            // ── 2. Grounded fallthrough ───────────────────────────────────
            if (ctx.Controller.isGrounded)
            {
                ctx.Velocity.y = 0f;
                ctx.RemainingJumps = ctx.EffectiveMaxJumps;
                GroundMomentum.OnLanded(ctx);
                ctx.StateMachine.TransitionTo(MovementStateId.Grounded);
                return;
            }

            // ── 3. Re-detect wall ─────────────────────────────────────────
            Vector3 lookHint = ctx.PlayerTransform.forward;
            lookHint.y = 0f;
            if (!WallDetection.TryFindWall(ctx, _side, _smoothedNormal, lookHint, out WallHit wall))
            {
                SoftExitLostContact(ctx);
                return;
            }

            _side = wall.Side;
            ctx.WallRunSide = _side;

            // ── 4. Same-wall / duration bookkeeping ───────────────────────
            if (!WallDetection.IsSameWall(ctx, wall.Collider, wall.Point))
                ctx.WallRunElapsedTime = 0f;

            ctx.WallRunLastCollider = wall.Collider;
            ctx.WallRunLastContactPoint = wall.Point;
            ctx.WallRunElapsedTime += deltaTime;

            // ── 5. Smooth normal + continuous tangent ─────────────────────
            float maxRadians = ctx.Settings.WallNormalSmoothingSpeed * Mathf.Deg2Rad * deltaTime;
            _smoothedNormal = Vector3.RotateTowards(
                _smoothedNormal, wall.Normal, maxRadians, 0f).normalized;

            Vector3 newTangent = WallDetection.GetWallTangent(_smoothedNormal, _tangent);
            if (newTangent.sqrMagnitude > 0.0001f)
                _tangent = newTangent;

            // ── 6. Speed + exit checks ────────────────────────────────────
            float tangentialSpeed = Vector3.Dot(ctx.Velocity, _tangent);
            if (tangentialSpeed < 0f)
            {
                _tangent = -_tangent;
                tangentialSpeed = -tangentialSpeed;
            }

            float maxSpeed = ctx.Settings.WallRunMaxSpeed * ctx.SpeedMultiplier;

            // Below the cap: accelerate up at WallRunAcceleration, unchanged.
            // Above it (fast entry, or momentum carried in from elsewhere):
            // decay down at AirMomentumDecay instead - the same weak rate
            // AirborneState uses so a grapple/slide-jump doesn't feel
            // cancelled by normal input (see GroundMomentum.cs). Reusing
            // WallRunAcceleration for both directions made excess entry
            // speed bleed off just as fast as it climbs to the cap, which
            // read as a near-instant snap despite not being a literal clamp.
            // Matches ComputeAirborneMomentumVelocity: AirMomentumDecay is
            // NOT scaled by SpeedMultiplier there, so it isn't here either.
            float rate = tangentialSpeed > maxSpeed
                ? ctx.Settings.AirMomentumDecay
                : ctx.Settings.WallRunAcceleration * ctx.SpeedMultiplier;
            tangentialSpeed = Mathf.MoveTowards(tangentialSpeed, maxSpeed, rate * deltaTime);

            bool tooSlow = tangentialSpeed < ctx.Settings.WallRunMinSpeed;
            bool timedOut = ctx.WallRunElapsedTime >= ctx.Settings.WallRunDuration;
            if (tooSlow || timedOut)
            {
                Vector3 exitHorizontal = _tangent * tangentialSpeed;
                ctx.Velocity = new Vector3(exitHorizontal.x, 0f, exitHorizontal.z);
                ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
                return;
            }

            ctx.Velocity = _tangent * tangentialSpeed;
            ApplyWallMotion(ctx, deltaTime, wall);

            // HUD speed is CharacterController.velocity (actual displacement).
            // Planned ctx.Velocity stays high when Move is eaten by a corner
            // or facing facet, so min-speed above never fires. Drop once the
            // capsule has been blocked long enough that it is not a seam hitch.
            float realizedSpeed = new Vector3(
                ctx.Controller.velocity.x, 0f, ctx.Controller.velocity.z).magnitude;
            if (realizedSpeed < ctx.Settings.WallRunMinSpeed)
            {
                _blockedTime += deltaTime;
                if (_blockedTime >= BlockedDropTime)
                {
                    // Zero XZ so Airborne cannot re-stick on ghost planned speed.
                    ctx.Velocity = new Vector3(0f, ctx.Velocity.y, 0f);
                    ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
                }
            }
            else
            {
                _blockedTime = 0f;
            }
        }

        public void FixedTick(MovementStateContext ctx, float fixedDeltaTime) { }

        public void Exit(MovementStateContext ctx)
        {
            if (_active)
            {
                ctx.WallRunExitTime = Time.time;
                ctx.WallRunExitLockEndTime = Time.time + ctx.Settings.WallJumpExitLock;
            }

            ctx.WallRunSide = WallSide.None;
            _active = false;
            _blockedTime = 0f;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private void ApplyWallMotion(MovementStateContext ctx, float deltaTime, WallHit wall)
        {
            float tangentialSpeed = Mathf.Max(0f, Vector3.Dot(ctx.Velocity, _tangent));
            Vector3 horizontal = _tangent * tangentialSpeed;

            float vertical;
            if (Mathf.Abs(ctx.WallVerticalInput) > 0.001f)
            {
                vertical = ctx.WallVerticalInput * ctx.Settings.WallVerticalMoveSpeed * ctx.SpeedMultiplier;
            }
            else if (ctx.Settings.WallRunGravityScale > 0f)
            {
                vertical = ctx.Velocity.y
                    - ctx.Settings.Gravity * ctx.Settings.WallRunGravityScale * ctx.GravityMultiplier * deltaTime;
            }
            else
            {
                vertical = 0f;
            }

            ctx.Velocity = new Vector3(horizontal.x, vertical, horizontal.z);

            Vector3 motion = ctx.Velocity * deltaTime;

            float desiredGap = ctx.Controller.radius + ctx.Controller.skinWidth;
            float gapError = wall.Distance - desiredGap;
            float hug = Mathf.Clamp(gapError, -MaxWallHugCorrection, MaxWallHugCorrection);
            if (Mathf.Abs(hug) > 0.001f)
                motion += -_smoothedNormal * hug;

            ctx.Controller.Move(motion);
        }

        private void SoftExitLostContact(MovementStateContext ctx)
        {
            // Hand XZ speed off untouched - AirborneState.ApplyAirControl
            // already decays anything above NormalMaxSpeed via
            // GroundMomentum.ComputeAirborneMomentumVelocity (AirMomentumDecay)
            // instead of snapping it down; see AirborneState.cs's AIR CONTROL
            // MODEL note. Rescaling to NormalMaxSpeed here duplicated that
            // with an instant cut instead of a decay, which is what made
            // losing wall contact feel like a hard stop instead of a fall-off.
            ctx.Velocity = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
        }

        private void PerformWallJump(MovementStateContext ctx)
        {
            // Tutorial impulse model (WallRunningAdvanced): preserve horizontal
            // XZ, zero Y, then add up + outward side force. Side force scales
            // with how much the camera looks away from the wall so looking
            // along the wall continues forward instead of always kicking out.
            ctx.Velocity = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);

            Vector3 lookFlat = ctx.PlayerTransform.forward;
            lookFlat.y = 0f;
            if (lookFlat.sqrMagnitude > 0.0001f)
                lookFlat.Normalize();
            else
                lookFlat = _tangent;

            // 0 = looking along/into wall, 1 = looking straight away from it.
            float lookAway = Mathf.Clamp01(Vector3.Dot(lookFlat, _smoothedNormal));
            float minSide = ctx.Settings.WallJumpOutwardForce * MinWallJumpSideFraction;
            float sideForce = Mathf.Lerp(minSide, ctx.Settings.WallJumpOutwardForce, lookAway);

            ctx.Velocity += Vector3.up * ctx.Settings.WallJumpUpwardForce
                          + _smoothedNormal * sideForce;

            ctx.StateMachine.TransitionTo(MovementStateId.Airborne);
        }
    }
}
