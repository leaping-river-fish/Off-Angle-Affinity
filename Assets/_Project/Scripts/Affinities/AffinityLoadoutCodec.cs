// =============================================================================
// AffinityLoadoutCodec — turns an AffinityLoadout into the single delimited
// string that crosses the network, and back again.
//
// WIRE FORMAT — 9 '|'-separated fields, fixed positions, empty segment = none:
//
//   primaryId | ultimateId | p0 | p1 | p2 | secondaryId | s0 | s1 | s2
//
//   pN / sN are the perk taken from ROW N (see AffinityLoadout). The secondary's
//   entry for the primary-only row is always empty. That deliberately-wasted
//   segment is what keeps encode and decode a symmetric row loop with no
//   slot-to-row mapping, and means changing which rows a secondary may use
//   needs no change to this format at all.
//
// WHY A DELIMITED STRING:
//   Exactly the idiom PlayerWeaponEquipper already uses for its equipped
//   weapon ids - a plain SyncVar<string> with the OnChange pattern already
//   established in this project, rather than a SyncList or a custom FishNet
//   serializer for what is a handful of short ids written once per match.
//
// DECODE IS DELIBERATELY LENIENT:
//   Unknown ids resolve to null and missing trailing fields are treated as
//   empty rather than failing the whole payload. Legality is NOT this type's
//   job - AffinityLoadoutRules.Sanitize is the gate, and it runs on the server
//   over whatever this produces. Keeping the two separate means a malformed
//   payload degrades to "picked nothing" instead of "player fails to spawn".
// =============================================================================

using UnityEngine;

namespace OffAngle.Affinities
{
    public static class AffinityLoadoutCodec
    {
        private const char Separator = '|';

        /// <summary>Primary, ultimate, one perk per row, secondary, one perk per row.</summary>
        private const int FieldCount = 2 + AffinityLoadoutRules.PerkRowCount + 1 + AffinityLoadoutRules.PerkRowCount;

        private const int PrimaryIndex = 0;
        private const int UltimateIndex = 1;
        private const int PrimaryPerksIndex = 2;
        private const int SecondaryIndex = PrimaryPerksIndex + AffinityLoadoutRules.PerkRowCount;
        private const int SecondaryPerksIndex = SecondaryIndex + 1;

        /// <summary>
        /// Serializes a loadout to the wire string. Null input encodes as an
        /// all-empty payload, which decodes back to "picked nothing".
        /// </summary>
        public static string Encode(AffinityLoadout loadout)
        {
            string[] fields = new string[FieldCount];
            for (int i = 0; i < fields.Length; i++)
                fields[i] = string.Empty;

            if (loadout == null)
                return string.Join(Separator.ToString(), fields);

            loadout.EnsureArraySizes();

            fields[PrimaryIndex] = SafeId(loadout.Primary != null ? loadout.Primary.Id : null, loadout.Primary);
            fields[UltimateIndex] = SafeId(loadout.Ultimate != null ? loadout.Ultimate.Id : null, loadout.Ultimate);
            fields[SecondaryIndex] = SafeId(loadout.Secondary != null ? loadout.Secondary.Id : null, loadout.Secondary);

            for (int row = 0; row < AffinityLoadoutRules.PerkRowCount; row++)
            {
                PerkDefinition primaryPerk = loadout.PrimaryPerks[row];
                PerkDefinition secondaryPerk = loadout.SecondaryPerks[row];

                fields[PrimaryPerksIndex + row] = SafeId(primaryPerk != null ? primaryPerk.Id : null, primaryPerk);
                fields[SecondaryPerksIndex + row] = SafeId(secondaryPerk != null ? secondaryPerk.Id : null, secondaryPerk);
            }

            return string.Join(Separator.ToString(), fields);
        }

        /// <summary>
        /// Parses a wire string into resolved asset references. Returns false only
        /// when there is no registry to resolve against; every other malformation
        /// degrades to null entries in an otherwise valid loadout, which
        /// AffinityLoadoutRules.Sanitize then fills in.
        /// </summary>
        public static bool TryDecode(string encoded, AffinityRegistry registry, out AffinityLoadout loadout)
        {
            loadout = new AffinityLoadout();

            if (registry == null)
            {
                Debug.LogError($"[{nameof(AffinityLoadoutCodec)}] TryDecode called with no AffinityRegistry.");
                return false;
            }

            if (string.IsNullOrEmpty(encoded))
                return true;

            string[] fields = encoded.Split(Separator);

            loadout.Primary = registry.GetAffinityById(Field(fields, PrimaryIndex));
            loadout.Ultimate = registry.GetUltimateById(Field(fields, UltimateIndex));
            loadout.Secondary = registry.GetAffinityById(Field(fields, SecondaryIndex));

            for (int row = 0; row < AffinityLoadoutRules.PerkRowCount; row++)
            {
                loadout.PrimaryPerks[row] = registry.GetPerkById(Field(fields, PrimaryPerksIndex + row));
                loadout.SecondaryPerks[row] = registry.GetPerkById(Field(fields, SecondaryPerksIndex + row));
            }

            return true;
        }

        /// <summary>Out-of-range reads return empty rather than throwing, so a truncated payload still decodes.</summary>
        private static string Field(string[] fields, int index)
            => index >= 0 && index < fields.Length ? fields[index] : string.Empty;

        /// <summary>
        /// An Id containing the separator would silently shift every later field,
        /// producing a loadout that decodes as a DIFFERENT valid build rather than
        /// failing. Refuse to encode it and make the authoring mistake loud.
        /// </summary>
        private static string SafeId(string id, UnityEngine.Object source)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;

            if (id.IndexOf(Separator) >= 0)
            {
                Debug.LogError($"[{nameof(AffinityLoadoutCodec)}] Id '{id}' on '{(source != null ? source.name : "<unknown>")}' contains the reserved '{Separator}' character and cannot be sent. Rename the Id.", source);
                return string.Empty;
            }

            return id;
        }
    }
}
