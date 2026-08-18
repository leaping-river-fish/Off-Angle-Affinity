// =============================================================================
// DamagePipeline — runs every conditional modifier over one hit and returns the
// two damage channels Health should apply.
//
// WHERE IT SITS:
//   Health.ApplyDamage is the single choke point for damage in this project -
//   Health is the only IDamageable implementer, so every source (hitscan,
//   shotgun pellets, projectiles, beams, future hazards) funnels through it.
//   Inserting here once therefore covers everything, with no per-weapon
//   plumbing and nothing to remember when a new damage source is added.
//
// ORDERING CONTRACT (matters for relational perks):
//   Modifiers run BEFORE CombatMemory records this hit. That is deliberate: a
//   perk asking "is this my first hit on them?" or "how long since they hit
//   me?" must see the state as it was BEFORE the current hit, otherwise every
//   opener perk would answer its own question. Health raises
//   ServerDamageApplied - which is what feeds CombatMemory - only after damage
//   has landed.
//
// ALLOCATION:
//   None. The context is a struct and both modifier lists are pre-sorted, so
//   this merge-walks them with two indices rather than concatenating or
//   sorting per hit.
//
// DEGRADATION:
//   Every participant is optional. A Dummy has no PlayerDamageModifiers and no
//   CombatMemory; a hazard may have no attacker at all. With nothing
//   registered this returns the original amount unchanged, so the pipeline is
//   invisible until a perk actually uses it.
// =============================================================================

using FishNet.Object;

namespace OffAngle.Combat
{
    public static class DamagePipeline
    {
        /// <summary>
        /// Applies every outgoing modifier on the attacker and every incoming
        /// modifier on the victim, in ascending Order across both.
        /// </summary>
        /// <param name="amount">Damage the shield should absorb first.</param>
        /// <param name="shieldBypassAmount">Damage that skips the shield and hits health directly.</param>
        public static void Resolve(
            DamageInfo info,
            Health victim,
            Shield victimShield,
            out float amount,
            out float shieldBypassAmount)
        {
            NetworkObject victimObject = victim != null ? victim.NetworkObject : null;
            NetworkObject attackerObject = info.Attacker;

            PlayerDamageModifiers victimModifiers = victim != null ? victim.GetComponent<PlayerDamageModifiers>() : null;
            CombatMemory victimMemory = victim != null ? victim.GetComponent<CombatMemory>() : null;

            // GetComponent on the attacker's root rather than a cached map: this
            // is a same-GameObject lookup at realistic fire rates, and a static
            // cache would need invalidating on every despawn.
            PlayerDamageModifiers attackerModifiers = null;
            CombatMemory attackerMemory = null;
            Health attackerHealth = null;

            if (attackerObject != null)
            {
                attackerModifiers = attackerObject.GetComponent<PlayerDamageModifiers>();
                attackerMemory = attackerObject.GetComponent<CombatMemory>();
                attackerHealth = attackerObject.GetComponent<Health>();
            }

            var ctx = new DamageModifierContext(
                info,
                victim,
                victimObject,
                victimShield,
                attackerHealth,
                victimMemory,
                attackerMemory);

            int outgoingCount = attackerModifiers != null ? attackerModifiers.OutgoingCount : 0;
            int incomingCount = victimModifiers != null ? victimModifiers.IncomingCount : 0;

            // Merge-walk two already-sorted lists.
            int o = 0;
            int i = 0;
            while (o < outgoingCount || i < incomingCount)
            {
                IDamageModifier next;

                if (o >= outgoingCount)
                {
                    next = victimModifiers.GetIncoming(i++);
                }
                else if (i >= incomingCount)
                {
                    next = attackerModifiers.GetOutgoing(o++);
                }
                else
                {
                    IDamageModifier outgoing = attackerModifiers.GetOutgoing(o);
                    IDamageModifier incoming = victimModifiers.GetIncoming(i);

                    // Ties go to the attacker's modifier purely so the order is
                    // deterministic rather than dependent on list lengths.
                    if (outgoing.Order <= incoming.Order)
                    {
                        next = outgoing;
                        o++;
                    }
                    else
                    {
                        next = incoming;
                        i++;
                    }
                }

                next.Modify(ref ctx);
            }

            amount = ctx.Amount > 0f ? ctx.Amount : 0f;
            shieldBypassAmount = ctx.ShieldBypassAmount > 0f ? ctx.ShieldBypassAmount : 0f;
        }
    }
}
