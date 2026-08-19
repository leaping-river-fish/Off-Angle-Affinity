// =============================================================================
// FireGroundEffectPayload — the first GroundEffectPayload: a burning patch
// that deals damage-per-second to anyone standing in it, excluding whoever
// created the zone (matches Projectile.ApplySplashDamage's existing
// self-exclusion convention for a caster's own splash). Pure damage-over-time,
// no persistent state to grant/revert - OnEnter/OnExit are no-ops.
//
// Create instances via: Assets > Create > Off-Angle > Combat > Ground Effects > Fire
// =============================================================================

using UnityEngine;
using OffAngle.Affinities;

namespace OffAngle.Combat
{
    [CreateAssetMenu(menuName = "Off-Angle/Combat/Ground Effects/Fire", fileName = "GroundEffect_Fire")]
    public class FireGroundEffectPayload : GroundEffectPayload
    {
        [Tooltip("Damage per second dealt to anyone standing in the zone, excluding the zone's owner.")]
        [SerializeField, Min(0f)] private float _damagePerSecond = 15f;

        public override void OnEnter(AffinityRuntimeContext occupant, GroundEffectZone zone) { }

        public override void OnTick(AffinityRuntimeContext occupant, GroundEffectZone zone, float deltaTime)
        {
            if (occupant?.Health == null) return;
            if (zone.OwnerNetworkObject != null && occupant.NetworkObject == zone.OwnerNetworkObject) return;

            float amount = _damagePerSecond * deltaTime;
            if (amount <= 0f) return;

            Vector3 point = occupant.PlayerRoot != null ? occupant.PlayerRoot.transform.position : zone.transform.position;
            DamageInfo info = new DamageInfo(amount, zone.OwnerNetworkObject, null, AffinityType.Cinder, point, Vector3.up, DamageCategory.Normal);
            occupant.Health.ApplyDamage(info);
        }

        public override void OnExit(AffinityRuntimeContext occupant, GroundEffectZone zone) { }
    }
}
