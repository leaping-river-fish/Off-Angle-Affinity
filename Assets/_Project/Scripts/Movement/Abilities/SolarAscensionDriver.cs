// =============================================================================
// SolarAscensionDriver — IAbilityMovementDriver that lifts the player straight
// up to a fixed hover height, then holds position (no translation) until its
// full duration elapses. Turning/aiming keep working unmodified since
// PlayerCameraController is fully decoupled from MovementStateMachine - this
// driver never touches the camera.
//
// No FishNet dependency, like every other file under Scripts/Movement/ - see
// GrapplePullDriver.cs for the sibling pattern this mirrors.
//
// SELF-TIMED:
//   Movement is client(owner)-authoritative end to end in this project (see
//   MovementStateMachine.cs's header), so there is no server-ticked movement
//   timer to hook into - this driver self-times its own total duration
//   exactly like GrapplePullDriver self-times GrappleMaxDuration.
//   SolarAscensionEffect independently times the same duration server-side
//   for the non-movement parts of the ultimate (shield/fire-override/scale);
//   its TargetRpc-driven InterruptCurrentAction() on expiry is a safety net
//   if the two timers ever drift by more than a frame.
//
// TRANSITION:
//   Exit() leaves ctx.Velocity at zero, so the player free-falls naturally
//   once control returns to Airborne - this is the "instant/simple" descent
//   explicitly accepted for this pass; a real animated transition is future
//   polish (see the Solar Ascension implementation plan).
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement.Abilities
{
    public class SolarAscensionDriver : IAbilityMovementDriver
    {
        private readonly float _riseHeight;
        private readonly float _riseDuration;
        private readonly float _totalDuration;

        private float _startY;
        private float _targetY;
        private float _elapsed;
        private bool _complete;

        public SolarAscensionDriver(float riseHeight, float riseDuration, float totalDuration)
        {
            _riseHeight = Mathf.Max(0f, riseHeight);
            _riseDuration = Mathf.Max(0.05f, riseDuration);
            _totalDuration = Mathf.Max(_riseDuration, totalDuration);
        }

        public void Enter(MovementStateContext ctx)
        {
            _elapsed = 0f;
            _complete = false;
            _startY = ctx.PlayerTransform.position.y;
            _targetY = _startY + _riseHeight;
            ctx.Velocity = Vector3.zero;
        }

        public void Tick(MovementStateContext ctx, float deltaTime)
        {
            _elapsed += deltaTime;

            if (_elapsed <= _riseDuration)
            {
                // Ease-out rise so the ascent doesn't read as a jarring
                // instant teleport, and so it doesn't clip through ceiling
                // geometry the way a true teleport could.
                float t = Mathf.Clamp01(_elapsed / _riseDuration);
                float eased = 1f - (1f - t) * (1f - t);
                float desiredY = Mathf.Lerp(_startY, _targetY, eased);
                float deltaY = desiredY - ctx.PlayerTransform.position.y;
                ctx.Controller.Move(new Vector3(0f, deltaY, 0f));
            }
            // Past the rise window: hold position exactly - no further Move()
            // calls, no input read, so the player is fully locked in place
            // until the ability ends.

            ctx.Velocity = Vector3.zero;

            if (_elapsed >= _totalDuration)
                _complete = true;
        }

        public bool IsComplete(MovementStateContext ctx) => _complete;

        public void Exit(MovementStateContext ctx, bool wasInterrupted)
        {
            ctx.Velocity = Vector3.zero;
        }
    }
}
