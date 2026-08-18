// =============================================================================
// PerkDefinition — one selectable cell of an affinity's 3x3 perk grid.
//
// ARCHITECTURE:
//   Concrete and data-only, exactly like AffinityPassive: display text plus a
//   list of AffinityEffect assets. Creating the full placeholder set (9 perks
//   per affinity) is therefore pure asset authoring with no new code, and a
//   perk that reuses an already-written effect costs nothing to add.
//
// GRID MEMBERSHIP IS NOT STORED HERE:
//   A perk does not know which affinity or row it belongs to - the grid on
//   AffinityDefinition is the single source of truth, and
//   AffinityDefinition.TryGetPerkRow is how anything asks. Storing the row on
//   both sides would let them disagree, and server-side validation exists
//   precisely to catch a client claiming a perk sits somewhere it doesn't.
//
// ID STABILITY:
//   Id crosses the network and must be unique project-wide, across every
//   affinity's grid - not just within one. See AffinityDefinition's header.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Perk", fileName = "Perk_New")]
    public class PerkDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable network id, e.g. \"cinder_r1_c2\". Must be unique across EVERY affinity's perk grid, not just this one, and must never change once matches have been played.")]
        public string Id;

        [Tooltip("Player-facing name shown in the perk grid.")]
        public string DisplayName;

        [Tooltip("Optional. Icon shown in the perk grid.")]
        public Sprite Icon;

        [Tooltip("Player-facing description. Authored text - nothing generates it from the effect list.")]
        [TextArea(2, 5)]
        public string Description;

        [Header("Behaviour")]
        [Tooltip("Effects applied while this perk is selected. Leave empty for a placeholder perk that does nothing yet.")]
        public List<AffinityEffect> Effects = new List<AffinityEffect>();

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(Id))
                Debug.LogWarning($"[{nameof(PerkDefinition)}] '{name}' has no Id. It cannot be selected or synced until one is set.", this);
        }
#endif
    }
}
