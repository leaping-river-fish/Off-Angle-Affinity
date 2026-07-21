// =============================================================================
// WeaponDefinition — the "selectable weapon" layer sitting on top of GunData.
//
// GunData stays pure tuning (damage/fire-rate/ammo/etc.) and is unaware this
// selection system exists. WeaponDefinition is what the UI and the equip
// pipeline actually reference: which category it belongs to, what prefab to
// spawn, and (redundantly, for convenience) which GunData it uses.
//
// Adding a new weapon to the game later = one new WeaponDefinition asset
// pointing at an existing (or new) GunData asset and Gun prefab. No code or
// switch statements involved.
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Weapon Definition
// =============================================================================

using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Weapon Definition", fileName = "WeaponDefinition_New")]
    public class WeaponDefinition : ScriptableObject
    {
        [Tooltip("Stable identifier for this weapon. Not used for lookups today (asset reference equality is), but useful for debugging/future networking/persistence.")]
        public string Id;

        [Tooltip("Label shown in the weapon choice UI.")]
        public string DisplayName;

        [Tooltip("Which category (Primary, Sidearm, ...) this weapon can be equipped into.")]
        public WeaponCategory Category;

        [Tooltip("Tuning data for this weapon. Same GunData asset already used by the Gun prefab below - kept here too so UI/inspection code doesn't need to dig into the prefab.")]
        public GunData Data;

        [Tooltip("Root prefab containing the Gun component (e.g. 'Hand Cannon.prefab'). Instantiated under the player's weapon holder when this weapon is equipped.")]
        public Gun WeaponPrefab;
    }
}
