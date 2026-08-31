// =============================================================================
// HitResolution — shared hit -> damage pipeline used by every instant shot
// behavior (Hitscan, Shotgun pellets, and later Piercing/Ricochet).
//
// Extracted verbatim from PlayerWeaponController.CmdFire's old inline block so
// headshot detection and self-hit filtering are defined exactly once. Any
// future behavior that needs "turn a physics hit into damage" calls this
// instead of re-implementing Hitbox/HitZone lookup.
//
// Server-only by convention (callers only ever run this from server-gated
// code), but this class itself has no networking dependency beyond the
// NetworkObject/DamageInfo types damage already flows through.
// =============================================================================

using FishNet.Object;
using OffAngle.Combat;
using UnityEngine;

namespace OffAngle.Weapons
{
    public static class HitResolution
    {
        /// <summary>
        /// Resolves a physics hit into damage and applies it via IDamageable.
        /// Returns false (and applies nothing) for self-hits or colliders with
        /// no IDamageable in their parent chain.
        /// </summary>
        public static bool TryResolveAndApply(
            Collider hitCollider,
            Vector3 hitPoint,
            Vector3 hitNormal,
            Transform attackerRoot,
            NetworkObject attacker,
            GunData weapon,
            float damage,
            float headshotDamage,
            out DamageInfo appliedInfo)
        {
            appliedInfo = default;

            IDamageable damageable = hitCollider.GetComponentInParent<IDamageable>();
            if (damageable == null) return false;

            // Ignore self-hits (our own capsule/CharacterController and its children).
            if (damageable is Component damageableComponent && damageableComponent.transform.root == attackerRoot)
                return false;

            Hitbox hitbox = hitCollider.GetComponent<Hitbox>();
            HitZone zone = hitbox != null ? hitbox.Zone : HitZone.Body;

            float amount = zone == HitZone.Head ? headshotDamage : damage;
            DamageCategory category = zone == HitZone.Head ? DamageCategory.Critical : DamageCategory.Normal;

            appliedInfo = new DamageInfo(
                amount: amount,
                attacker: attacker,
                weapon: weapon,
                affinity: weapon != null ? weapon.Affinity : AffinityType.None,
                hitPoint: hitPoint,
                hitNormal: hitNormal,
                category: category);

            damageable.ApplyDamage(appliedInfo);
            return true;
        }

        /// <summary>Convenience overload for raycast-based behaviors (Hitscan, Shotgun, Piercing).</summary>
        public static bool TryResolveAndApply(
            RaycastHit hit,
            Transform attackerRoot,
            NetworkObject attacker,
            GunData weapon,
            float damage,
            float headshotDamage,
            out DamageInfo appliedInfo)
        {
            return TryResolveAndApply(hit.collider, hit.point, hit.normal, attackerRoot, attacker, weapon, damage, headshotDamage, out appliedInfo);
        }

        /// <summary>
        /// Raycasts along origin/direction, skipping the attacker's root (body + head).
        /// Use this instead of Physics.Raycast so a stale origin behind a fast-moving
        /// shooter does not eat the shot. Sorted nearest-first, same as projectile sweeps.
        /// </summary>
        public static bool TryRaycastIgnoreSelf(
            Vector3 origin, 
            Vector3 direction,
            float range,
            LayerMask mask,
            Transform attackerRoot,
            out RaycastHit hit)
        {
            hit = default;
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                direction,
                range,
                mask,
                QueryTriggerInteraction.Ignore);
            
            if (hits == null || hits.Length == 0) return false;

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit candidate = hits[i];
                if (attackerRoot != null && candidate.collider != null && candidate.collider.transform.root == attackerRoot) continue;

                hit = candidate;
                return true;
            }

            return false;
        }
    }
}
