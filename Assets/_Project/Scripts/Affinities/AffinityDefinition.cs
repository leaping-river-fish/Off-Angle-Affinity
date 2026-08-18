// =============================================================================
// AffinityDefinition — one selectable Affinity: its passive, its three
// ultimates, and its 3x3 perk grid.
//
// ARCHITECTURE:
//   An ASSET, not an enum value - same rationale WeaponCategory documents.
//   Affinities need to be addable/retunable without touching code, and nothing
//   in the codebase ever switches on "which affinity is this"; systems hold a
//   reference to one of these assets and read the data hanging off it.
//
//   This type is deliberately CONCRETE and DATA-ONLY. So are AffinityPassive,
//   PerkDefinition and UltimateDefinition. Only AffinityEffect (the thing that
//   actually does something) is polymorphic. That split is what makes authoring
//   the full placeholder set - 6 affinities x (1 passive + 3 ultimates +
//   9 perks) - require zero new classes.
//
// THE PERK GRID:
//   3 rows x 3 columns. A player takes exactly one perk from each row of their
//   PRIMARY affinity, and one from each of the secondary-eligible rows of their
//   SECONDARY affinity. Which rows those are lives in AffinityLoadoutRules, not
//   here - this type only describes the shape, never the selection rules.
//
//   PerkRow exists purely because Unity cannot serialize a nested array
//   (PerkDefinition[][]); a [Serializable] wrapper class is the standard idiom.
//
// ID STABILITY:
//   Id is what crosses the network (see AffinityLoadoutCodec) and what the
//   registry resolves against. It must be unique project-wide and must not
//   change once matches have been played with it - renaming the ASSET is safe,
//   renaming the Id is not.
// =============================================================================

using System;
using System.Collections.Generic;
using OffAngle.Combat;
using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Affinity Definition", fileName = "Affinity_New")]
    public class AffinityDefinition : ScriptableObject
    {
        /// <summary>One row of the perk grid. Wrapper class exists so Unity can serialize a 2D grid.</summary>
        [Serializable]
        public class PerkRow
        {
            [Tooltip("The perks offered on this row. A player picks at most one of them.")]
            public PerkDefinition[] Perks = new PerkDefinition[AffinityLoadoutRules.PerkColumnCount];
        }

        [Header("Identity")]
        [Tooltip("Stable network id, e.g. \"cinder\". Must be unique across every AffinityDefinition and must never change once matches have been played - this is what travels over the wire, not the asset name.")]
        public string Id;

        [Tooltip("Player-facing name shown in the selection UI.")]
        public string DisplayName;

        [Tooltip("Optional. Icon shown in the selection UI.")]
        public Sprite Icon;

        [Tooltip("Accent colour for this affinity's UI panels, highlights, and any themed VFX.")]
        public Color ThemeColor = Color.white;

        [Tooltip("Bridges to the pre-existing AffinityType enum so DamageInfo and the floating damage-number tint keep working. Purely cosmetic today - no gameplay reads it.")]
        public AffinityType DamageTag = AffinityType.None;

        [Header("Content")]
        [Tooltip("Always-on bundle of effects granted while this is the player's PRIMARY affinity. A secondary affinity's passive is deliberately never applied.")]
        public AffinityPassive Passive;

        [Tooltip("The ultimates offered by this affinity. A player picks exactly one, and only from their primary. Expected to hold 3.")]
        public List<UltimateDefinition> Ultimates = new List<UltimateDefinition>();

        [Tooltip("The 3x3 perk grid. One perk may be taken per row. Row order is meaningful - see AffinityLoadoutRules.PrimaryOnlyRow.")]
        public PerkRow[] PerkRows = new PerkRow[AffinityLoadoutRules.PerkRowCount];

        // ------------------------------------------------------------------
        // Queries — used by the selection UI and by server-side validation
        // ------------------------------------------------------------------

        /// <summary>Returns the perk at a grid position, or null if the position is out of range or empty.</summary>
        public PerkDefinition GetPerk(int row, int column)
        {
            if (PerkRows == null || row < 0 || row >= PerkRows.Length) return null;

            PerkRow perkRow = PerkRows[row];
            if (perkRow?.Perks == null || column < 0 || column >= perkRow.Perks.Length) return null;

            return perkRow.Perks[column];
        }

        /// <summary>
        /// Finds which row a perk sits on in THIS affinity's grid. Returns false if
        /// the perk belongs to a different affinity or isn't in the grid at all -
        /// which is exactly the check server-side validation needs.
        /// </summary>
        public bool TryGetPerkRow(PerkDefinition perk, out int row)
        {
            row = -1;
            if (perk == null || PerkRows == null) return false;

            for (int r = 0; r < PerkRows.Length; r++)
            {
                PerkRow perkRow = PerkRows[r];
                if (perkRow?.Perks == null) continue;

                for (int c = 0; c < perkRow.Perks.Length; c++)
                {
                    if (perkRow.Perks[c] != perk) continue;
                    row = r;
                    return true;
                }
            }

            return false;
        }

        /// <summary>True if this affinity offers the given ultimate. Used to reject an ultimate borrowed from another affinity.</summary>
        public bool HasUltimate(UltimateDefinition ultimate)
        {
            if (ultimate == null || Ultimates == null) return false;
            return Ultimates.Contains(ultimate);
        }

        // ------------------------------------------------------------------
        // Editor sanity checks — warn, never autofix (project convention)
        // ------------------------------------------------------------------

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Keep the grid the right shape so the Inspector always shows 3x3.
            // This is structural, not gameplay data, so resizing here is safe.
            if (PerkRows == null || PerkRows.Length != AffinityLoadoutRules.PerkRowCount)
                Array.Resize(ref PerkRows, AffinityLoadoutRules.PerkRowCount);

            for (int r = 0; r < PerkRows.Length; r++)
            {
                PerkRows[r] ??= new PerkRow();
                if (PerkRows[r].Perks == null || PerkRows[r].Perks.Length != AffinityLoadoutRules.PerkColumnCount)
                    Array.Resize(ref PerkRows[r].Perks, AffinityLoadoutRules.PerkColumnCount);
            }

            if (string.IsNullOrWhiteSpace(Id))
                Debug.LogWarning($"[{nameof(AffinityDefinition)}] '{name}' has no Id. It cannot be selected or synced until one is set.", this);
        }
#endif
    }
}
