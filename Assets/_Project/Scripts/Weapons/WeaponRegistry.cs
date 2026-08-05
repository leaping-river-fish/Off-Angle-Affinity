// =============================================================================
// WeaponRegistry — central registry of all WeaponDefinition assets in the
// project. Auto-populated by the custom inspector via an Editor script.
//
// WHY THIS EXISTS:
// Instead of manually placing every weapon choice in the prefab or using
// Resources.LoadAll (which has drawbacks), this registry provides a single
// authoritative list of all weapons that WeaponSelectionMenu can iterate at
// runtime to spawn choices dynamically.
//
// USAGE:
// 1. Create one instance via: Assets > Create > Off-Angle > Weapons > Weapon Registry
// 2. In the Inspector, click "Refresh Weapon List" to scan for all WeaponDefinition assets
// 3. Reference this asset in WeaponSelectionMenu's inspector
// 4. Adding new weapons = just create the WeaponDefinition asset and hit Refresh
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Weapon Registry
// =============================================================================

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Weapon Registry", fileName = "WeaponRegistry")]
    public class WeaponRegistry : ScriptableObject
    {
        [Tooltip("All weapons in the project. Use the 'Refresh Weapon List' button in the Inspector to auto-populate this from all WeaponDefinition assets.")]
        public List<WeaponDefinition> AllWeapons = new List<WeaponDefinition>();

        /// <summary>Returns all weapons belonging to the specified category.</summary>
        public IEnumerable<WeaponDefinition> GetByCategory(WeaponCategory category)
        {
            if (category == null) return Enumerable.Empty<WeaponDefinition>();
            return AllWeapons.Where(w => w != null && w.Category == category);
        }

        /// <summary>Returns all unique categories present in the registry.</summary>
        public IEnumerable<WeaponCategory> GetAllCategories()
        {
            return AllWeapons
                .Where(w => w != null && w.Category != null)
                .Select(w => w.Category)
                .Distinct();
        }
    }
}
