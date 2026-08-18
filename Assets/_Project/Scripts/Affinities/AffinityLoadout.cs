// =============================================================================
// AffinityLoadout — one player's complete, permanent-for-the-match Affinity
// choice, as RESOLVED ASSET REFERENCES.
//
// ARCHITECTURE:
//   A plain [Serializable] class, not a ScriptableObject and not a networked
//   type. It is the shape everything agrees on: the selection UI builds one,
//   AffinityLoadoutCodec turns it into the string that crosses the wire,
//   AffinityLoadoutRules sanitizes it server-side, and PlayerAffinity reads it
//   to decide which effects to attach.
//
// PERKS ARE INDEXED BY GRID ROW, NOT BY PICK ORDER:
//   PrimaryPerks[r] and SecondaryPerks[r] both hold the perk taken from row r,
//   with null meaning "nothing taken from that row". Both arrays are
//   PerkRowCount long even though a secondary can only fill some of its rows -
//   the entry for a row it may not use simply stays null.
//
//   Row-indexing (rather than a packed list of picks) is what makes the
//   one-perk-per-row rule enforce itself: there is physically nowhere to put a
//   second perk from the same row. It also means validation, encoding and the
//   UI all loop rows identically, with no slot-to-row mapping to get wrong,
//   and it survives any future change to which rows a secondary may draw from.
// =============================================================================

using System;

namespace OffAngle.Affinities
{
    [Serializable]
    public class AffinityLoadout
    {
        /// <summary>Grants its passive, one of its ultimates, and one perk per row.</summary>
        public AffinityDefinition Primary;

        /// <summary>Must belong to Primary. Null means no ultimate selected.</summary>
        public UltimateDefinition Ultimate;

        /// <summary>Indexed by grid row. Null entry = nothing taken from that row.</summary>
        public PerkDefinition[] PrimaryPerks = new PerkDefinition[AffinityLoadoutRules.PerkRowCount];

        /// <summary>Optional. Contributes perks only - never its passive, never an ultimate.</summary>
        public AffinityDefinition Secondary;

        /// <summary>Indexed by grid row. The primary-only row's entry is always null.</summary>
        public PerkDefinition[] SecondaryPerks = new PerkDefinition[AffinityLoadoutRules.PerkRowCount];

        /// <summary>True if nothing at all has been chosen yet.</summary>
        public bool IsEmpty => Primary == null && Secondary == null && Ultimate == null;

        /// <summary>
        /// Deep copy. Callers hand these across ownership boundaries (UI -> codec,
        /// codec -> server store), so returning a shared instance would let one
        /// side mutate another's state.
        /// </summary>
        public AffinityLoadout Clone()
        {
            var copy = new AffinityLoadout
            {
                Primary = Primary,
                Ultimate = Ultimate,
                Secondary = Secondary,
            };

            Array.Copy(PrimaryPerks, copy.PrimaryPerks, AffinityLoadoutRules.PerkRowCount);
            Array.Copy(SecondaryPerks, copy.SecondaryPerks, AffinityLoadoutRules.PerkRowCount);

            return copy;
        }

        /// <summary>
        /// Guards against a loadout built by hand or deserialized with the wrong
        /// array sizes - every consumer indexes these arrays by row without
        /// bounds-checking, so they must always be exactly PerkRowCount long.
        /// </summary>
        public void EnsureArraySizes()
        {
            if (PrimaryPerks == null || PrimaryPerks.Length != AffinityLoadoutRules.PerkRowCount)
                Array.Resize(ref PrimaryPerks, AffinityLoadoutRules.PerkRowCount);

            if (SecondaryPerks == null || SecondaryPerks.Length != AffinityLoadoutRules.PerkRowCount)
                Array.Resize(ref SecondaryPerks, AffinityLoadoutRules.PerkRowCount);
        }
    }
}
