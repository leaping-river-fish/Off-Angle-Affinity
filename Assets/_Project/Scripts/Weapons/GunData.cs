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

        [Tooltip("Damage dealt instead of Damage when the hit registers against a Head hitbox.")]
        [Min(0f)] public float HeadshotDamage = 50f;

        [Header("Fire")]
        [Tooltip("Shots per second.")]
        [Min(0.01f)] public float FireRate = 5f;

        [Tooltip("How the trigger behaves: one shot per press, continuous while held, or a fixed multi-shot burst per press.")]
        public FireMode FireMode = FireMode.SemiAuto;

        [Tooltip("Number of shots fired per trigger press. Only used when FireMode is Burst.")]
        [Min(1)] public int BurstCount = 3;

        [Header("Ammo")]
        [Tooltip("Rounds held in the magazine before a reload is required.")]
        [Min(1)] public int MagazineSize = 30;

        [Tooltip("Rounds available in reserve when the player spawns with this weapon.")]
        [Min(0)] public int StartingReserveAmmo = 90;

        [Tooltip("Seconds a reload takes to complete.")]
        [Min(0.01f)] public float ReloadTime = 2f;
        
        [Tooltip("If true, a reload starts automatically when the magazine hits zero and reserve ammo remains.")]
        public bool AutoReloadOnEmpty = true;

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
