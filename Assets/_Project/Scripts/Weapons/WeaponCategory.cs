// =============================================================================
// WeaponCategory — tiny ScriptableObject identifying a weapon "slot" such as
// Primary or Sidearm.
//
// WHY AN ASSET INSTEAD OF AN ENUM:
// Categories need to be addable without touching code (e.g. a future "Melee"
// category). Every place that would otherwise switch on an enum value instead
// holds/compares a reference to one of these assets. Two categories are equal
// if they are the same asset - no Id string comparisons required at runtime,
// but Id is kept for readability in the Inspector and for any future
// network/serialization needs.
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Weapon Category
// =============================================================================

using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Weapon Category", fileName = "WeaponCategory_New")]
    public class WeaponCategory : ScriptableObject
    {
        [Tooltip("Stable identifier for this category. Not currently used for lookups (asset reference equality is), but useful for debugging/future networking.")]
        public string Id;

        [Tooltip("Label shown in UI, e.g. section headers.")]
        public string DisplayName;
    }
}
