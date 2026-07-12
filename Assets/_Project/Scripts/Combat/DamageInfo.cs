// =============================================================================
// DamageInfo — server-side payload describing a single damage event.
//
// Constructed on the server after a successful hit and passed to
// IDamageable.ApplyDamage. Not sent across the network as-is — this is a plain
// struct that keeps the damage pipeline extensible without touching call sites.
//
// Future systems (status effects, kill feed, damage log, etc.) will read
// additional fields from here; add fields, don't add parameters to interfaces.
// =============================================================================

using FishNet.Object;
using OffAngle.Weapons;
using UnityEngine;

namespace OffAngle.Combat
{
    public readonly struct DamageInfo
    {
        public readonly float Amount;
        public readonly NetworkObject Attacker;
        public readonly GunData Weapon;
        public readonly AffinityType Affinity;
        public readonly Vector3 HitPoint;
        public readonly Vector3 HitNormal;
        public readonly DamageCategory Category;

        public DamageInfo(
            float amount,
            NetworkObject attacker,
            GunData weapon,
            AffinityType affinity,
            Vector3 hitPoint,
            Vector3 hitNormal,
            DamageCategory category = DamageCategory.Normal)
        {
            Amount = amount;
            Attacker = attacker;
            Weapon = weapon;
            Affinity = affinity;
            HitPoint = hitPoint;
            HitNormal = hitNormal;
            Category = category;
        }
    }
}
