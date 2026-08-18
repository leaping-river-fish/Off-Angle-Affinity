// =============================================================================
// AffinityRegistry — the single authoritative list of every AffinityDefinition
// in the project, and the id -> asset resolver the network layer depends on.
//
// WHY THIS EXISTS:
//   Only ids travel over the wire (see AffinityLoadoutCodec). Every peer must
//   resolve a given id to the SAME asset, so exactly one object needs to hold
//   the full catalogue.
//
//   PlayerAffinity references this asset directly rather than carrying a
//   per-prefab array of definitions the way PlayerWeaponEquipper does. That
//   array has to be hand-maintained on the Player prefab, and its own tooltip
//   warns that a missing entry silently strands a client on the wrong weapon.
//   A single asset reference cannot drift that way.
//
// USAGE:
//   1. Create one via: Assets > Create > Off-Angle > Affinities > Affinity Registry
//   2. Click "Refresh Affinity List" in the Inspector to scan the project
//   3. Assign DefaultAffinity - the fallback for a player who never picked
//   4. Reference this asset on PlayerAffinity and on the selection UI
//
// LOOKUP COST:
//   Linear scans. The full set is 6 affinities / 18 ultimates / 54 perks, and
//   lookups happen once per player spawn and once per UI rebuild - not per
//   frame. A dictionary cache would mean runtime state on a shared
//   ScriptableObject, which this project's SO convention forbids.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Affinity Registry", fileName = "AffinityRegistry")]
    public class AffinityRegistry : ScriptableObject
    {
        [Tooltip("Every affinity in the project. Use the 'Refresh Affinity List' button in the Inspector to auto-populate this.")]
        public List<AffinityDefinition> AllAffinities = new List<AffinityDefinition>();

        [Tooltip("Fallback applied when a player never submitted a selection, or submitted one that failed validation entirely. Must be set, or such players spawn with no affinity at all.")]
        public AffinityDefinition DefaultAffinity;

        /// <summary>Resolves an affinity by its stable network Id. Returns null for an unknown or empty id.</summary>
        public AffinityDefinition GetAffinityById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            for (int i = 0; i < AllAffinities.Count; i++)
            {
                AffinityDefinition affinity = AllAffinities[i];
                if (affinity != null && affinity.Id == id)
                    return affinity;
            }

            return null;
        }

        /// <summary>
        /// Resolves a perk by its stable network Id, searching every affinity's grid.
        /// Whether that perk is LEGAL for the loadout that named it is a separate
        /// question, answered by AffinityLoadoutRules.
        /// </summary>
        public PerkDefinition GetPerkById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            for (int i = 0; i < AllAffinities.Count; i++)
            {
                AffinityDefinition affinity = AllAffinities[i];
                if (affinity?.PerkRows == null) continue;

                for (int r = 0; r < affinity.PerkRows.Length; r++)
                {
                    AffinityDefinition.PerkRow row = affinity.PerkRows[r];
                    if (row?.Perks == null) continue;

                    for (int c = 0; c < row.Perks.Length; c++)
                    {
                        PerkDefinition perk = row.Perks[c];
                        if (perk != null && perk.Id == id)
                            return perk;
                    }
                }
            }

            return null;
        }

        /// <summary>Resolves an ultimate by its stable network Id, searching every affinity.</summary>
        public UltimateDefinition GetUltimateById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            for (int i = 0; i < AllAffinities.Count; i++)
            {
                AffinityDefinition affinity = AllAffinities[i];
                if (affinity?.Ultimates == null) continue;

                for (int u = 0; u < affinity.Ultimates.Count; u++)
                {
                    UltimateDefinition ultimate = affinity.Ultimates[u];
                    if (ultimate != null && ultimate.Id == id)
                        return ultimate;
                }
            }

            return null;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (DefaultAffinity == null)
                Debug.LogWarning($"[{nameof(AffinityRegistry)}] '{name}' has no DefaultAffinity. Players who never picked will spawn with no affinity.", this);
        }
#endif
    }
}
