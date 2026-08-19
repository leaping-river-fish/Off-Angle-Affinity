// =============================================================================
// SlideEntry — shared "should CrouchSlide start a Slide or a Crouch?" decision.
//
// Used by both GroundedState (a fresh CrouchSlide press while already
// grounded) and AirborneState (landing while CrouchSlide is already held -
// e.g. jump then hold the key to chain straight into a slide). Extracted so
// a landing-into-slide resolves with the exact same speed/cooldown rules as
// a normal ground press, instead of always downgrading into a slow Crouch
// regardless of how much speed the player is carrying.
// =============================================================================

using UnityEngine;

namespace OffAngle.Movement
{
    public static class SlideEntry
    {
        /// <summary>
        /// Transitions into Sliding if horizontal speed clears
        /// SlideEntrySpeedThreshold and the slide's own cooldown has elapsed;
        /// otherwise transitions into Crouching. Returns false (with no
        /// transition at all) only in the "fast enough but slide is on
        /// cooldown" case - callers decide the fallback for that themselves,
        /// since a forced Crouch there would feel like a punishment for
        /// wanting to slide.
        /// </summary>
        public static bool TryEnterSlideOrCrouch(MovementStateContext ctx)
        {
            // Measure horizontal speed only. ctx.Velocity.y may just be the
            // -2 ground-press constant, not actual downward movement.
            float horizontalSpeed = new Vector3(ctx.Velocity.x, 0f, ctx.Velocity.z).magnitude;

            // "Grounded" debuff (e.g. Hadal Zone) always falls through to the
            // Crouching branch below - see MovementStateContext.GroundedLocked.
            if (horizontalSpeed >= ctx.Settings.SlideEntrySpeedThreshold && !ctx.GroundedLocked)
            {
                if (Time.time < ctx.NextSlideAllowedTime)
                    return false;

                ctx.StateMachine.TransitionTo(MovementStateId.Sliding);
                return true;
            }

            ctx.StateMachine.TransitionTo(MovementStateId.Crouching);
            return true;
        }
    }
}
