// =============================================================================
// GunData — ScriptableObject holding all serialized weapon values.
//
// This is the ONLY place gun tuning lives. Gun components reference a GunData
// instance; PlayerWeaponController reads the same reference on the server for
// authoritative validation. No values are duplicated on the Gun MonoBehaviour.
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Gun Data
// =============================================================================

using OffAngle.Combat;
using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Gun Data", fileName = "GunData_New")]
    public class GunData : ScriptableObject
    {
        [Header("Damage")]
        [Min(0f)] public float Damage = 25f;

        [Header("Fire")]
        [Tooltip("Shots per second.")]
        [Min(0.01f)] public float FireRate = 5f;

        [Tooltip("Hitscan is currently the only implemented shot type. Projectile is reserved for future expansion.")]
        public ShotType ShotType = ShotType.Hitscan;

        [Tooltip("Max hitscan distance in meters.")]
        [Min(0f)] public float Range = 100f;

        [Header("Targeting")]
        [Tooltip("Layers the hitscan raycast may hit.")]
        public LayerMask HitMask = ~0;

        [Header("Affinity (placeholder — no runtime effect yet)")]
        [Tooltip("Carried through DamageInfo. Every affinity currently deals identical damage; future systems will read this value to add slows/burns/etc.")]
        public AffinityType Affinity = AffinityType.None;
    }
}
