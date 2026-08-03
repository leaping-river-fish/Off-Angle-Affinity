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
//   applies in AirborneState always, and in GroundedState/CrouchingState only
//   while GroundMomentum.ShouldPreserveGroundMomentum(ctx) is true (see that
//   method - a short grace window right after landing, or an ice surface):
//     - Input never replaces velocity - it only STEERS the velocity's
//       DIRECTION toward the wish direction (never its magnitude - see
//       ApplyMomentumDecay's DIRECTION VS MAGNITUDE note below), so a
//       direction change has to fight the existing momentum instead of
//       cancelling it in one frame, and releasing input does not remove
//       momentum at all.
//     - Speed decays smoothly toward NormalMaxSpeed (never below it, never
//       snapped, and - critically - NEVER increased by steering) at a rate
//       the caller supplies - GroundedState/CrouchingState use
//       Settings.GroundMomentumDecay (strong, so a fast landing/slide
//       doesn't slide across the ground for too long); AirborneState uses
//       Settings.AirMomentumDecay (weak, so a grapple release or slide-jump
//       stays committed to its trajectory far longer than the same speed
//       would last on the ground).
//     - Settings.MaxPreservedSpeed is a hard safety ceiling applied after
//       decay, bounding worst-case stacked momentum regardless of source.
//   Once decay brings speed back down to NormalMaxSpeed, the very next Tick
//   falls through to the normal responsive branch above - control is handed
//   back cleanly, with no separate "recovering" state needed.
//
//   OUTSIDE that grace window, GroundedState/CrouchingState instead HARD-STOP
//   excess ground momentum (fall through to the same responsive branch used
//   at/below the threshold) rather than gradually decaying it - see
//   ShouldPreserveGroundMomentum/OnLanded below and ComputeHorizontalVelocity.
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
        /// True while GroundedState/CrouchingState should keep preserving
        /// (gradually decaying/steering) excess ground momentum instead of
        /// hard-stopping it - either because we're still within the
        /// bunny-hop grace window stamped by OnLanded() (see
        /// Settings.GroundMomentumGraceDuration), or because ctx.OnIcyGround
        /// is true.
        ///
        /// ctx.OnIcyGround is a RESERVED HOOK for a future ice terrain type:
        /// nothing sets it yet, but once a terrain-detection system does
        /// (e.g. a ground raycast against a PhysicMaterial/tag), standing on
        /// ice will automatically and permanently behave like being inside
        /// the grace window - momentum is never force-stopped while on ice,
        /// matching the "ice preserves momentum" design intent without this
        /// method or its caller needing to change at all.
        /// </summary>
        public static bool ShouldPreserveGroundMomentum(MovementStateContext ctx) =>
            ctx.OnIcyGround || Time.time < ctx.GroundMomentumGraceEndTime;

        /// <summary>
        /// Stamps a fresh bunny-hop grace window starting now. Call exactly
        /// once per actual landing (transitioning from a non-grounded state
        /// onto the ground) - see AirborneState.Tick()'s landing block and
        /// AbilityMovementState.ExitToLocomotion() for the two call sites.
        /// Deliberately NOT called from GroundedState/CrouchingState.Enter()
        /// (which also fire for ground-to-ground transitions like standing
        /// up from Crouch or a Slide ending) - re-stamping on those would let
        /// a player refresh the grace window indefinitely without ever
        /// leaving the ground, defeating the point of the window.
        /// </summary>
        public static void OnLanded(MovementStateContext ctx) =>
            ctx.GroundMomentumGraceEndTime = Time.time + ctx.Settings.GroundMomentumGraceDuration;

        /// <summary>
        /// Computes the horizontal (XZ) velocity a grounded/crouched state
        /// should move with this Tick. At or below the momentum threshold,
        /// behavior is the classic direct-set walk/sprint/crouch model
        /// (unchanged). Above it:
        ///   - While ShouldPreserveGroundMomentum(ctx) is true (bunny-hop
        ///     grace window, or ice), hands off to the shared momentum
        ///     decay/steer model using Settings.GroundMomentumDecay/
        ///     GroundMomentumSteerAcceleration - see this file's MODEL note.
        ///   - Otherwise, HARD-STOPS: falls through to the same classic
        ///     responsive branch used at/below the threshold, instantly
        ///     snapping excess ground momentum down to the normal cap (or to
        ///     zero with no input) rather than gradually decaying it.
        /// </summary>
        /// <param name="ctx">Movement context (read for current Velocity and momentum tunables).</param>
        /// <param name="wishDir">Unnormalized wish direction from player input (right * x + forward * y).</param>
        /// <param name="inputMag">Clamped [0,1] input magnitude.</param>
        /// <param name="speedCap">The state's normal speed cap (WalkSpeed/SprintSpeed or CrouchSpeed, already including SpeedMultiplier) - only used for the responsive branch (at/below threshold, or a hard stop above it).</param>
        public static Vector3 ComputeHorizontalVelocity(
            MovementStateContext ctx, Vector3 wishDir, float inputMag, float speedCap, float deltaTime)
        {
            Vector3 current = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z);
            float currentSpeed = current.magnitude;
            float momentumThreshold = GetMomentumThreshold(ctx);

            bool aboveThreshold = currentSpeed > momentumThreshold + ctx.Settings.MomentumTolerance;

            // AT OR BELOW the momentum threshold, OR above it but outside the
            // bunny-hop grace window (and not on ice): classic, fully
            // responsive movement - no input means an instant stop, held
            // input means an instant snap to the context's own cap. This is
            // both the unchanged pre-momentum-system behavior AND the "stop
            // fully" hard-stop for ground momentum that outlived its grace.
            if (!aboveThreshold || !ShouldPreserveGroundMomentum(ctx))
            {
                if (inputMag <= 0.001f)
                    return Vector3.zero;

                return wishDir.normalized * (speedCap * inputMag);
            }

            // ABOVE the momentum threshold AND within the grace window (or on
            // ice): preserve, steer, and decay toward the threshold instead
            // of snapping back to it.
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
        /// momentum paths above.
        ///
        /// DIRECTION VS MAGNITUDE (this is the fix for the "speed keeps
        /// increasing every hop" exploit): steering and decay are computed
        /// as two fully independent operations, deliberately in that order,
        /// so the final result can never end up FASTER than
        /// <paramref name="currentSpeed"/> going in:
        ///   1. DIRECTION ONLY - input accelerates a vector toward the wish
        ///      direction and the result is immediately re-normalized. This
        ///      only rotates the travel direction; whatever magnitude the
        ///      steering vector added is thrown away, it never lingers into
        ///      the speed calculation below.
        ///   2. MAGNITUDE ONLY - decay is subtracted from
        ///      <paramref name="currentSpeed"/> (the speed the caller
        ///      already had this Tick), never from a steered/inflated
        ///      magnitude.
        /// The previous implementation added the steer vector directly onto
        /// velocity and THEN decayed the resulting (larger) magnitude - when
        /// steerAccel exceeds decayRate (true for AirMomentumSteerAcceleration
        /// vs AirMomentumDecay), that let holding a direction that roughly
        /// matched current travel (the common case right after a grapple
        /// release, or every landing-to-jump hop) gain speed every single
        /// Tick, compounding without bound across a bunny-hop chain. Speed
        /// out of this function is now monotonically <= currentSpeed (floored
        /// at <paramref name="floorSpeed"/>, and safety-clamped to
        /// <paramref name="ceilingSpeed"/>) - steering can only ever
        /// redirect momentum, never manufacture more of it.
        /// </summary>
        private static Vector3 ApplyMomentumDecay(
            Vector3 current, float currentSpeed, Vector3 wishDir, float inputMag,
            float steerAccel, float decayRate, float floorSpeed, float ceilingSpeed, float deltaTime)
        {
            Vector3 direction = currentSpeed > 0.0001f ? current / currentSpeed : Vector3.zero;

            if (inputMag > 0.001f)
            {
                Vector3 steered = current + wishDir.normalized * (steerAccel * inputMag * deltaTime);
                if (steered.sqrMagnitude > 0.0001f)
                    direction = steered.normalized;
            }

            float decayedSpeed = Mathf.Max(floorSpeed, currentSpeed - decayRate * deltaTime);
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
