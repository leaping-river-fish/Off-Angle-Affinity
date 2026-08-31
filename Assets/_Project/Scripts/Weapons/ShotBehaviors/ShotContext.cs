// =============================================================================
// ShotContext — everything a ShotBehavior needs to resolve one shot/tick,
// built server-side by PlayerWeaponController right before dispatching to a
// behavior. Origin/Direction are the same client-trusted aim ray CmdFire has
// always used (see PlayerWeaponController's header AUTHORITY NOTE) - this
// struct does not expand that trust, it just carries it to the behavior.
// =============================================================================

using FishNet.Object;
using UnityEngine;

namespace OffAngle.Weapons
{
    public readonly struct ShotContext
    {
        /// <summary>Trusted ray origin (owner's camera position at the moment of firing).</summary>
        public readonly Vector3 Origin;

        /// <summary>Trusted, normalized ray direction.</summary>
        public readonly Vector3 Direction;

        /// <summary>The firing weapon's tuning data.</summary>
        public readonly GunData Data;

        /// <summary>The shooter, for DamageInfo.Attacker.</summary>
        public readonly NetworkObject Attacker;

        /// <summary>The shooter's root transform, used to filter out self-hits.</summary>
        public readonly Transform AttackerRoot;

        /// <summary>Networking/cosmetic seam back into PlayerWeaponController.</summary>
        public readonly IShotBehaviorHost Host;

        /// <summary>
        /// Seconds this shot's trigger/hold has been continuously active.
        /// Always 0 for Instant shot behaviors (Hitscan/Shotgun/Projectile),
        /// which have no concept of a hold. Continuous behaviors (Beam) use
        /// this to ramp effects like damage-over-hold-time - see
        /// BeamShotBehavior.RampUpTime.
        /// </summary>
        public readonly float HeldDuration;

        /// <summary>
        /// Unique shot ID for prediction/reconciliation. Associates predicted
        /// client shots with authoritative server shots to avoid duplicate effects.
        /// </summary>
        public readonly ushort ShotId;

        /// <summary>
        /// Owner's FishNet tick at the moment of fire (TimeManager.Tick on the
        /// client that sent CmdFire). 0 when the shot was not initiated by that
        /// path (beam ticks, ascension override, ultimates).
        /// </summary>
        public readonly uint FireTick;

        public ShotContext(Vector3 origin, Vector3 direction, GunData data, NetworkObject attacker, Transform attackerRoot, IShotBehaviorHost host, float heldDuration = 0f, ushort shotId = 0, uint fireTick = 0)
        {
            Origin = origin;
            Direction = direction;
            Data = data;
            Attacker = attacker;
            AttackerRoot = attackerRoot;
            Host = host;
            HeldDuration = heldDuration;
            ShotId = shotId;
            FireTick = fireTick;
        }
    }
}
