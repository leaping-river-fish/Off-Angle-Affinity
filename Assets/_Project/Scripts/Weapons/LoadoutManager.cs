// =============================================================================
// LoadoutManager — central, local storage for "which WeaponDefinition is
// currently selected per WeaponCategory".
//
// ARCHITECTURE:
//   Plain MonoBehaviour, zero FishNet - same scene-singleton idiom as
//   PlayerSpawner.Instance. This is deliberately LOCAL UI STATE ONLY for now:
//   the weapon selection menu writes here, PlayerWeaponEquipper reads here.
//   Nothing about this class is synchronized over the network yet.
//
//   Keyed by WeaponCategory (an asset reference), not an enum, so any number
//   of categories can be supported without touching this file - just add a
//   Category/Default entry in the Inspector list.
//
// MULTIPLAYER NOTE:
//   If/when weapon choice needs to be authoritative (dedicated server,
//   remote clients seeing the correct gun on you), the selection made here
//   will need to be sent to the server (e.g. a ServerRpc carrying the chosen
//   WeaponDefinition's Id) so it can be validated and replicated. That is
//   intentionally not built yet - this class only reflects the local
//   player's own choice.
// =============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace OffAngle.Weapons
{
    public class LoadoutManager : MonoBehaviour
    {
        [Serializable]
        private class CategoryDefault
        {
            public WeaponCategory Category;
            public WeaponDefinition Default;
        }

        [Tooltip("One entry per category that should exist in the loadout, with the weapon selected at startup before the player opens the menu.")]
        [SerializeField] private List<CategoryDefault> _defaults = new();

        /// <summary>
        /// Scene singleton reference, same convention as PlayerSpawner.Instance.
        /// Consumers should null-check because this only exists once the
        /// gameplay scene is loaded.
        /// </summary>
        public static LoadoutManager Instance { get; private set; }

        private readonly Dictionary<WeaponCategory, WeaponDefinition> _selected = new();

        /// <summary>Raised whenever a category's selection changes, including nulling it out. UI and the equip pipeline subscribe here.</summary>
        public event Action<WeaponCategory, WeaponDefinition> SelectionChanged;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(LoadoutManager)}] Multiple instances found. Keeping the first on '{Instance.name}'.", this);
                return;
            }
            Instance = this;

            foreach (CategoryDefault entry in _defaults)
            {
                if (entry.Category == null) continue;
                _selected[entry.Category] = entry.Default;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>Returns the currently selected weapon for a category, or null if none is selected.</summary>
        public WeaponDefinition GetSelected(WeaponCategory category)
        {
            if (category == null) return null;
            return _selected.TryGetValue(category, out WeaponDefinition definition) ? definition : null;
        }

        /// <summary>
        /// Sets the selected weapon for a category and notifies listeners.
        /// Passing null clears the selection for that category.
        /// </summary>
        public void SetSelected(WeaponCategory category, WeaponDefinition definition)
        {
            if (category == null) return;

            if (definition != null && definition.Category != category)
            {
                Debug.LogWarning($"[{nameof(LoadoutManager)}] '{definition.name}' belongs to category '{definition.Category?.DisplayName}', not '{category.DisplayName}'. Ignoring selection.", this);
                return;
            }

            _selected[category] = definition;
            SelectionChanged?.Invoke(category, definition);
        }
    }
}
