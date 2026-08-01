// =============================================================================
// GroundMomentum — shared momentum preservation math for every state that
// computes an input-driven horizontal speed (GroundedState, CrouchingState,
// AirborneState). Extracted so all three preserve, steer, and decay speed
// above normal movement speed identically - mirroring why
// MovementCapsuleUtility exists for capsule math.
//
// MODEL:
//   There is a single global gate - MovementSettings.NormalMaxSpeed (see
//   MovementStateContext.cs), scaled by ctx.SpeedMultiplier - not a
//   per-context cap. Below or at that gate, movement is fully responsive and
//   completely unchanged from "classic" behavior: releasing input stops
//   instantly, holding input directly sets speed to the context's own cap
//   (walk/sprint/crouch × inputMag). This is what keeps normal walking and
//   sprinting feeling exactly as snappy as before.
//
//   Above the gate, the player is carrying MOMENTUM (grapple release,
//   slide-jump, bunny-hop chain, external impulse, ...) and a different rule
//   applies everywhere that calls into this class:
//     - Input never replaces velocity - it only ACCELERATES the velocity
//       vector toward the wish direction (steering), so a direction change
//       has to fight the existing momentum instead of cancelling it in one
//       frame, and releasing input does not remove momentum at all.
//     - Speed decays smoothly toward NormalMaxSpeed (never below it, never
//       snapped) at a rate the caller supplies - GroundedState/
//       CrouchingState use Settings.GroundMomentumDecay (strong, so a fast
//       landing/slide doesn't slide across the ground for too long);
//       AirborneState uses Settings.AirMomentumDecay (weak, so a grapple
//       release or slide-jump stays committed to its trajectory far longer
//       than the same speed would last on the ground).
//     - Settings.MaxPreservedSpeed is a hard safety ceiling applied after
//       decay, bounding worst-case stacked momentum regardless of source.
//   Once decay brings speed back down to NormalMaxSpeed, the very next Tick
//   falls through to the normal responsive branch above - control is handed
//   back cleanly, with no separate "recovering" state needed.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement
{
    public static class GroundMomentum
    {
        /// <summary>
        /// The single global speed (m/s) above which ANY state in this file
        /// treats the player as carrying preserved momentum rather than just
        /// walking/sprinting - see MovementSettings.NormalMaxSpeed. Scaled by
        /// ctx.SpeedMultiplier so temporary speed buffs/debuffs shift the
        /// momentum gate exactly like they shift normal movement speed.
        /// </summary>
        public static float GetMomentumThreshold(MovementStateContext ctx) =>
            ctx.Settings.NormalMaxSpeed * ctx.SpeedMultiplier;

        /// <summary>
        /// Computes the horizontal (XZ) velocity a grounded/crouched state
        /// should move with this Tick. At or below the momentum threshold,
        /// behavior is the classic direct-set walk/sprint/crouch model
        /// (unchanged). Above it, hands off to the shared momentum decay/
        /// steer model using Settings.GroundMomentumDecay/
        /// GroundMomentumSteerAcceleration - see this file's MODEL note.
        /// </summary>
        /// <param name="ctx">Movement context (read for current Velocity and momentum tunables).</param>
        /// <param name="wishDir">Unnormalized wish direction from player input (right * x + forward * y).</param>
        /// <param name="inputMag">Clamped [0,1] input magnitude.</param>
        /// <param name="speedCap">The state's normal speed cap (WalkSpeed/SprintSpeed or CrouchSpeed, already including SpeedMultiplier) - only used for the at-or-below-threshold responsive branch.</param>
        public static Vector3 ComputeHorizontalVelocity(
            MovementStateContext ctx, Vector3 wishDir, float inputMag, float speedCap, float deltaTime)
        {
            Vector3 current = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float currentSpeed = current.magnitude;
            float momentumThreshold = GetMomentumThreshold(ctx);

            // AT OR BELOW the momentum threshold: classic, fully responsive
            // movement - no input means an instant stop, held input means an
            // instant snap to the context's own cap. Completely unchanged
            // from pre-momentum-system behavior.
            if (currentSpeed <= momentumThreshold + ctx.Settings.MomentumTolerance)
            {
                if (inputMag <= 0.001f)
                    return Vector3.zero;

                return wishDir.normalized * (speedCap * inputMag);
            }

            // ABOVE the momentum threshold: preserve, steer, and decay
            // toward the threshold instead of snapping back to it.
            return ApplyMomentumDecay(
                current, currentSpeed, wishDir, inputMag,
                steerAccel:   ctx.Settings.GroundMomentumSteerAcceleration,
                decayRate:    ctx.Settings.GroundMomentumDecay,
                floorSpeed:   momentumThreshold,
                ceilingSpeed: ctx.Settings.MaxPreservedSpeed,
                deltaTime:    deltaTime);
        }

        /// <summary>
        /// Computes the momentum-preserving horizontal (XZ) velocity for
        /// AirborneState while speed is above the momentum threshold. Uses
        /// Settings.AirMomentumDecay/AirMomentumSteerAcceleration, which are
        /// deliberately much weaker than their ground counterparts - see
        /// this file's MODEL note. AirborneState is responsible for checking
        /// the threshold itself and only calling this above it; at or below
        /// the threshold it keeps its own classic acceleration+clamp air
        /// control model.
        /// </summary>
        /// <param name="currentHorizontal">Current horizontal (XZ) velocity - ctx.Velocity with y zeroed.</param>
        public static Vector3 ComputeAirborneMomentumVelocity(
            MovementStateContext ctx, Vector3 currentHorizontal, Vector3 wishDir, float inputMag, float deltaTime)
        {
            float currentSpeed = currentHorizontal.magnitude;
            float momentumThreshold = GetMomentumThreshold(ctx);

            return ApplyMomentumDecay(
                currentHorizontal, currentSpeed, wishDir, inputMag,
                steerAccel:   ctx.Settings.AirMomentumSteerAcceleration,
                decayRate:    ctx.Settings.AirMomentumDecay,
                floorSpeed:   momentumThreshold,
                ceilingSpeed: ctx.Settings.MaxPreservedSpeed,
                deltaTime:    deltaTime);
        }

        /// <summary>
        /// Shared decay/steer core for both the grounded and airborne
        /// momentum paths above. Input accelerates the velocity vector
        /// toward the wish direction (steering, never a direct replacement),
        /// then the resulting speed decays toward - but never below -
        /// <paramref name="floorSpeed"/>, and is finally clamped to
        /// <paramref name="ceilingSpeed"/> as a safety cap.
        /// </summary>
        private static Vector3 ApplyMomentumDecay(
            Vector3 current, float currentSpeed, Vector3 wishDir, float inputMag,
            float steerAccel, float decayRate, float floorSpeed, float ceilingSpeed, float deltaTime)
        {
            if (inputMag > 0.001f)
                current += wishDir.normalized * (steerAccel * inputMag * deltaTime);

            float steeredSpeed = current.magnitude;
            Vector3 direction = steeredSpeed > 0.0001f
                ? current / steeredSpeed
                : (currentSpeed > 0.0001f ? current / currentSpeed : Vector3.zero);

            float decayedSpeed = Mathf.Max(floorSpeed, steeredSpeed - decayRate * deltaTime);
            decayedSpeed = Mathf.Min(decayedSpeed, ceilingSpeed);

            return direction * decayedSpeed;
        }

        /// <summary>
        /// Reads MoveInput (respecting InputLocked) and converts it into a
        /// world-space, UNNORMALIZED wish direction plus the clamped [0,1]
        /// input magnitude. Extracted so every state that reads player
        /// movement input builds the wish direction identically - see the
        /// file header for why this matters (GroundedState, CrouchingState,
        /// AirborneState, and SlidingState all previously duplicated this
        /// exact two-line block; future states like WallRunning/Zipline
        /// inherit it for free instead of re-deriving it).
        /// </summary>
        /// <param name="inputMag">Clamped [0,1] input magnitude - equivalent to rawInput.magnitude for any non-diagonal-keyboard input.</param>
        public static Vector3 GetWishDirection(MovementStateContext ctx, out float inputMag)
        {
            Vector2 rawInput = ctx.InputLocked ? Vector2.zero : ctx.Input.MoveInput;
            inputMag = Mathf.Clamp01(rawInput.magnitude);

            return ctx.PlayerTransform.right   * rawInput.x
                 + ctx.PlayerTransform.forward * rawInput.y;
        }
    }
}
