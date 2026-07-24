// =============================================================================
// IContinuousShotBehavior — contract for shot behaviors that sustain fire
// while held (currently: BeamShotBehavior) instead of firing one discrete
// shot per RequestFire.
//
// DIVISION OF RESPONSIBILITY:
// PlayerWeaponController owns everything network-shaped for a continuous
// behavior - the start/stop RPCs, the per-tick timer, ammo SyncVar writes,
// and lifecycle/reload/death checks. This interface only describes the pure
// gameplay math for a single tick (raycast + damage), the same way
// InstantShotBehavior.Fire does for discrete shots. Keeping the tick math
// here (not on PlayerWeaponController) means a future second continuous
// behavior does not require touching PlayerWeaponController at all.
// =============================================================================

using UnityEngine;

namespace OffAngle.Weapons
{
    public interface IContinuousShotBehavior
    {
        /// <summary>Damage ticks per second. Also paces how often PlayerWeaponController sends CmdBeamTick - never every rendered frame.</summary>
        float TickRate { get; }

        /// <summary>Magazine ammo consumed per tick. Supports fractional values (e.g. 0.5 for "one round every two ticks") via the host's accumulator.</summary>
        float AmmoPerTick { get; }

        /// <summary>Server-only. Performs one damage tick along ctx.Origin/Direction and reports the visual result.</summary>
        BeamTickResult Tick(ShotContext ctx);
    }

    /// <summary>Result of a single continuous-behavior tick, for the host to relay to observers and to ShotEvents.</summary>
    public readonly struct BeamTickResult
    {
        public readonly bool DidHit;
        public readonly Vector3 EndPoint;

        public BeamTickResult(bool didHit, Vector3 endPoint)
        {
            DidHit = didHit;
            EndPoint = endPoint;
        }
    }
}
