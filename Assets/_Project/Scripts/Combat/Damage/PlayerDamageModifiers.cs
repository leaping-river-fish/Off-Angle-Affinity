// =============================================================================
// PlayerDamageModifiers — the conditional damage modifiers currently attached
// to one entity, split into what it DEALS and what it TAKES.
//
// WHY TWO LISTS:
//   The two directions are not symmetric and cannot share one list.
//     OUTGOING - "damage I deal": bonus versus low-health targets, headshot
//                shield penetration, and so on. Belongs to the attacker.
//     INCOMING - "damage I take": personal damage reduction, but crucially
//                also DEBUFFS SOMEONE ELSE APPLIED TO ME. A perk that marks a
//                target so they take extra damage from EVERYONE has to affect
//                attackers who do not have that perk, so the modifier must
//                live on the victim, not on the player who applied it.
//
// SORTED ON INSERT:
//   Registration happens a handful of times per player per match; hits happen
//   constantly. Keeping both lists ordered means DamagePipeline can merge-walk
//   them with two indices and zero allocation, instead of sorting or
//   concatenating on every bullet.
//
// HANDLES:
//   Same discipline as PlayerStats - Add returns a handle, Remove takes it, and
//   an effect's OnDetach releases exactly what it registered. Removing an
//   already-removed or zero handle is a safe no-op.
//
// AUTHORITY:
//   Server-side in practice: only DamagePipeline (inside Health.ApplyDamage)
//   reads these, and that is server-only. Registering on a client is harmless
//   but has no effect, so effects that use this should declare
//   AffinityEffectScope.Server.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace OffAngle.Combat
{
    public class PlayerDamageModifiers : MonoBehaviour
    {
        private readonly struct Entry
        {
            public readonly int Handle;
            public readonly IDamageModifier Modifier;

            public Entry(int handle, IDamageModifier modifier)
            {
                Handle = handle;
                Modifier = modifier;
            }
        }

        private readonly List<Entry> _outgoing = new List<Entry>();
        private readonly List<Entry> _incoming = new List<Entry>();
        private int _nextHandle = 1;

        // ------------------------------------------------------------------
        // Registration
        // ------------------------------------------------------------------

        /// <summary>Registers a modifier on damage this entity DEALS. Returns the handle needed to remove it.</summary>
        public int AddOutgoing(IDamageModifier modifier) => Add(_outgoing, modifier);

        /// <summary>
        /// Registers a modifier on damage this entity TAKES. This is also where a
        /// debuff applied by another player is registered, so that it affects every
        /// attacker rather than only the one who applied it.
        /// </summary>
        public int AddIncoming(IDamageModifier modifier) => Add(_incoming, modifier);

        public void RemoveOutgoing(int handle) => Remove(_outgoing, handle);

        public void RemoveIncoming(int handle) => Remove(_incoming, handle);

        // ------------------------------------------------------------------
        // Read access for DamagePipeline — indexed rather than enumerated so
        // the hit path allocates nothing (no iterator, no defensive copy).
        // ------------------------------------------------------------------

        public int OutgoingCount => _outgoing.Count;
        public int IncomingCount => _incoming.Count;

        public IDamageModifier GetOutgoing(int index) => _outgoing[index].Modifier;
        public IDamageModifier GetIncoming(int index) => _incoming[index].Modifier;

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private int Add(List<Entry> list, IDamageModifier modifier)
        {
            if (modifier == null) return 0;

            int handle = _nextHandle++;
            var entry = new Entry(handle, modifier);

            // Insert-sorted by Order. Scanning for the first STRICTLY greater
            // entry keeps equal-Order modifiers in registration order, so two
            // perks in the same band behave predictably rather than depending on
            // which happened to attach first.
            int index = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Modifier.Order <= modifier.Order) continue;
                index = i;
                break;
            }

            list.Insert(index, entry);
            return handle;
        }

        private static void Remove(List<Entry> list, int handle)
        {
            if (handle == 0) return;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Handle != handle) continue;
                list.RemoveAt(i);
                return;
            }
        }
    }
}
