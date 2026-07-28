// =============================================================================
// GroundMomentum — shared bunny-hop-friendly ground speed blending.
//
// Extracted so every state that computes an input-capped ground speed
// (GroundedState, CrouchingState) preserves momentum above that cap
// identically - mirroring why MovementCapsuleUtility exists for capsule math.
// Without this, landing at high speed (built up via a jump chain or a
// slide-jump) would instantly clamp back down to walk/sprint/crouch speed
// the moment either state's Tick() ran, killing the reward for chaining
// jumps.
//
// MODEL:
//   No held input at all: full stop, immediately, regardless of how much
//   bonus momentum (bunny hop chain, slide-jump, grapple) is currently
//   banked above the cap. Bonus momentum is only preserved while the player
//   keeps actively steering it (see below) - without this, releasing input
//   after sprinting (or after any above-cap carry) would coast/slide to a
//   stop instead of stopping the instant input is released, since the cap
//   itself can drop out from under the current speed (e.g. releasing Sprint
//   mid-run instantly lowers the cap from SprintSpeed to WalkSpeed, which
//   used to be misread as "bonus momentum" to preserve rather than "player
//   let go, stop now").
//   At or below the cap (and holding input): fully responsive, snap-to-input -
//   today's normal walk/sprint/crouch feel, completely unchanged.
//   Above the cap while holding input (momentum carried in from Airborne or
//   Sliding): steer it toward the wish direction using the same acceleration
//   model as AirborneState's air control, WITHOUT clamping it back down, then
//   let only the EXCESS above the cap decay via Settings.BunnyHopMomentumDecay -
//   never below the cap. AirborneState applies no such decay, so chaining
//   jumps (and air-strafing, which AirborneState already rewards - see its
//   ApplyAirControl) is always strictly better than staying grounded once
//   above normal speed. That asymmetry is what makes bunny hopping "ideal
//   after a lot of momentum" instead of just tolerated.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement
{
    public static class GroundMomentum
    {
        /// <summary>
        /// Computes the horizontal (XZ) velocity a grounded/crouched state
        /// should move with this Tick, preserving any speed above
        /// <paramref name="speedCap"/> instead of clamping it away.
        /// </summary>
        /// <param name="ctx">Movement context (read for current Velocity and BunnyHopMomentumDecay).</param>
        /// <param name="wishDir">Unnormalized wish direction from player input (right * x + forward * y).</param>
        /// <param name="inputMag">Clamped [0,1] input magnitude.</param>
        /// <param name="speedCap">The state's normal speed cap (WalkSpeed/SprintSpeed or CrouchSpeed, already including SpeedMultiplier).</param>
        public static Vector3 ComputeHorizontalVelocity(
            MovementStateContext ctx, Vector3 wishDir, float inputMag, float speedCap, float deltaTime)
        {
            // No held input: full stop. Bonus momentum above the cap is only
            // ever preserved while the player is actively steering it - see
            // the MODEL note above for why this must come before the cap
            // check (releasing Sprint drops the cap out from under the
            // current speed, which must NOT be misread as bonus momentum).
            if (inputMag <= 0.001f)
                return Vector3.zero;

            Vector3 current = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float currentSpeed = current.magnitude;

            // No bonus momentum to preserve - behave exactly like today's
            // direct-set ground movement.
            if (currentSpeed <= speedCap + 0.01f)
                return wishDir.normalized * (speedCap * inputMag);

            float accel = ctx.Settings.AirAcceleration * ctx.Settings.AirControlMultiplier;
            current.x += wishDir.normalized.x * accel * deltaTime;
            current.z += wishDir.normalized.z * accel * deltaTime;

            float steeredSpeed = current.magnitude;
            Vector3 direction = steeredSpeed > 0.0001f ? current / steeredSpeed : wishDir.normalized;
            float decayedSpeed = Mathf.Max(speedCap, steeredSpeed - ctx.Settings.BunnyHopMomentumDecay * deltaTime);

            return direction * decayedSpeed;
        }
    }
}
