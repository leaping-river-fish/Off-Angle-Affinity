// =============================================================================
// AffinityLoadoutRules — the SINGLE source of truth for what a legal Affinity
// loadout looks like.
//
// WHY ONE PLACE:
//   Two very different callers need these rules and must never disagree:
//     - the selection UI, to grey out picks a player may not take
//     - the SERVER, to sanitize whatever a client actually submitted
//   If those drifted apart, the UI would offer builds the server silently
//   rewrites. Everything here is static and Unity-free so both can call it.
//
// THE ROW LOCK:
//   Rows are ordered and meaningful. A PRIMARY affinity may take one perk from
//   every row. A SECONDARY may take one perk from every row EXCEPT
//   PrimaryOnlyRow - that tier is the reward for committing to an affinity.
//   The locked row is the same index for every affinity, by design. Changing
//   which row is reserved is a one-line edit to the constant below.
//
// SANITIZE IS FORGIVING, NEVER REJECTING:
//   A malformed, stale, or hostile submission must still produce a PLAYABLE
//   player. Every rule violation drops just the offending piece rather than
//   failing the whole loadout, and empty required slots are filled with sane
//   defaults. There is no path here that returns "no affinity" for a player
//   who is about to spawn.
//
// WARNING DISCIPLINE:
//   Only genuine RULE VIOLATIONS are logged - a client naming a perk that
//   isn't where it claims. Filling an empty slot is silent, because the
//   commonest caller is a player who never picked at all (countdown expiry,
//   late join) and that is expected, not anomalous. A warning from here always
//   means "someone sent something wrong", never "someone sent nothing".
// =============================================================================

using System.Text;
using UnityEngine;

namespace OffAngle.Affinities
{
    public static class AffinityLoadoutRules
    {
        // ------------------------------------------------------------------
        // Shape
        // ------------------------------------------------------------------

        /// <summary>Rows in every affinity's perk grid.</summary>
        public const int PerkRowCount = 3;

        /// <summary>Perks offered per row. A player takes at most one of them.</summary>
        public const int PerkColumnCount = 3;

        /// <summary>
        /// The row index a SECONDARY affinity may not draw from - primary-only.
        /// Hardcoded and identical for every affinity: the player does not get to
        /// choose which rows their secondary uses. Change this one value to move
        /// the reserved tier.
        /// </summary>
        public const int PrimaryOnlyRow = 1;

        /// <summary>Perks a primary contributes when fully picked (one per row).</summary>
        public const int PrimaryPerkCount = PerkRowCount;

        /// <summary>Perks a secondary contributes when fully picked (every row but the locked one).</summary>
        public const int SecondaryPerkCount = PerkRowCount - 1;

        // ------------------------------------------------------------------
        // Queries — used by the UI to gate what is offered
        // ------------------------------------------------------------------

        /// <summary>True if a perk may be taken from this row in the given slot.</summary>
        public static bool IsRowAvailable(int row, bool asSecondary)
        {
            if (row < 0 || row >= PerkRowCount) return false;
            return !asSecondary || row != PrimaryOnlyRow;
        }

        /// <summary>
        /// True if <paramref name="perk"/> may be taken from <paramref name="affinity"/>
        /// in the given slot. Checks both that the perk actually belongs to that
        /// affinity's grid and that its row is available to that slot.
        /// </summary>
        public static bool CanSelectPerk(AffinityDefinition affinity, PerkDefinition perk, bool asSecondary)
        {
            if (affinity == null || perk == null) return false;
            if (!affinity.TryGetPerkRow(perk, out int row)) return false;
            return IsRowAvailable(row, asSecondary);
        }

        /// <summary>
        /// True if every required slot is filled. The selection UI uses this to
        /// enable its Submit button. A null Secondary counts as COMPLETE - taking
        /// a secondary affinity is optional; half-taking one is not.
        /// </summary>
        public static bool IsComplete(AffinityLoadout loadout)
        {
            if (loadout?.Primary == null) return false;
            loadout.EnsureArraySizes();

            if (loadout.Ultimate == null) return false;

            for (int row = 0; row < PerkRowCount; row++)
            {
                if (loadout.PrimaryPerks[row] == null) return false;
            }

            if (loadout.Secondary == null) return true;

            for (int row = 0; row < PerkRowCount; row++)
            {
                if (!IsRowAvailable(row, asSecondary: true)) continue;
                if (loadout.SecondaryPerks[row] == null) return false;
            }

            return true;
        }

        // ------------------------------------------------------------------
        // Server-side sanitize
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns a legal, playable loadout derived from <paramref name="requested"/>.
        /// Never returns null and never throws. Pass null for a player who never
        /// submitted anything and this produces the full default build.
        /// </summary>
        public static AffinityLoadout Sanitize(AffinityRegistry registry, AffinityLoadout requested)
        {
            var result = new AffinityLoadout();

            if (registry == null)
            {
                Debug.LogError($"[{nameof(AffinityLoadoutRules)}] Sanitize called with no AffinityRegistry. Returning an empty loadout - the player will have no affinity.");
                return result;
            }

            requested?.EnsureArraySizes();
            StringBuilder violations = null;

            // --- Primary -------------------------------------------------
            AffinityDefinition primary = requested?.Primary;
            if (primary != null && !IsRegistered(registry, primary))
            {
                Note(ref violations, $"primary '{primary.name}' is not in the registry");
                primary = null;
            }

            primary ??= registry.DefaultAffinity;

            if (primary == null)
            {
                Debug.LogError($"[{nameof(AffinityLoadoutRules)}] No valid primary and no DefaultAffinity set on '{registry.name}'. Returning an empty loadout.");
                return result;
            }

            result.Primary = primary;

            // --- Ultimate ------------------------------------------------
            UltimateDefinition ultimate = requested?.Ultimate;
            if (ultimate != null && !primary.HasUltimate(ultimate))
            {
                Note(ref violations, $"ultimate '{ultimate.name}' does not belong to '{primary.name}'");
                ultimate = null;
            }

            // An ultimate is required to play - filling the first one is better
            // than leaving the player accumulating charge they can never spend.
            if (ultimate == null && primary.Ultimates != null && primary.Ultimates.Count > 0)
                ultimate = primary.Ultimates[0];

            result.Ultimate = ultimate;

            // --- Primary perks -------------------------------------------
            SanitizePerkRows(primary, requested?.PrimaryPerks, result.PrimaryPerks, asSecondary: false, ref violations);

            // --- Secondary ------------------------------------------------
            AffinityDefinition secondary = requested?.Secondary;

            if (secondary != null && !IsRegistered(registry, secondary))
            {
                Note(ref violations, $"secondary '{secondary.name}' is not in the registry");
                secondary = null;
            }

            if (secondary != null && secondary == primary)
            {
                Note(ref violations, "secondary matched primary");
                secondary = null;
            }

            result.Secondary = secondary;

            // A secondary is optional, so an absent one stays absent rather than
            // being defaulted. Once chosen though, its available rows are filled -
            // a half-taken secondary is not a build anyone would have wanted.
            if (secondary != null)
                SanitizePerkRows(secondary, requested?.SecondaryPerks, result.SecondaryPerks, asSecondary: true, ref violations);

            if (violations != null)
                Debug.LogWarning($"[{nameof(AffinityLoadoutRules)}] Loadout sanitized: {violations}");

            return result;
        }

        private static bool IsRegistered(AffinityRegistry registry, AffinityDefinition affinity)
            => registry.AllAffinities != null && registry.AllAffinities.Contains(affinity);

        /// <summary>
        /// Copies row-indexed perks across, dropping any that don't belong to
        /// <paramref name="affinity"/> or don't sit on the row they claim.
        /// Available-but-empty rows are filled with column 0.
        /// </summary>
        private static void SanitizePerkRows(
            AffinityDefinition affinity,
            PerkDefinition[] source,
            PerkDefinition[] destination,
            bool asSecondary,
            ref StringBuilder violations)
        {
            for (int row = 0; row < PerkRowCount; row++)
            {
                if (!IsRowAvailable(row, asSecondary))
                {
                    // Not a violation worth logging: a UI that sends a full
                    // row-indexed array is doing nothing wrong, and the locked row
                    // simply has no effect in this slot.
                    destination[row] = null;
                    continue;
                }

                PerkDefinition perk = source != null && row < source.Length ? source[row] : null;

                if (perk != null)
                {
                    if (!affinity.TryGetPerkRow(perk, out int actualRow))
                    {
                        Note(ref violations, $"perk '{perk.name}' is not in '{affinity.name}' grid");
                        perk = null;
                    }
                    else if (actualRow != row)
                    {
                        Note(ref violations, $"perk '{perk.name}' claimed row {row} but sits on row {actualRow}");
                        perk = null;
                    }
                }

                // Silent fill so an auto-readied or timed-out player still spawns
                // with a functional build.
                destination[row] = perk ?? affinity.GetPerk(row, 0);
            }
        }

        private static void Note(ref StringBuilder violations, string message)
        {
            violations ??= new StringBuilder();
            if (violations.Length > 0) violations.Append("; ");
            violations.Append(message);
        }
    }
}
