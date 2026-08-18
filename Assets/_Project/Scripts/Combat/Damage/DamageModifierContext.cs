// =============================================================================
// DamageModifierContext — one hit, in flight, while modifiers adjust it.
//
// ARCHITECTURE:
//   Built once per landed hit by DamagePipeline and passed by ref through every
//   registered IDamageModifier. A struct, so no allocation happens on the
//   server's hottest path.
//
//   The readonly fields are the QUESTIONS a conditional perk can ask; the two
//   mutable fields are the ANSWER it writes. Everything a perk might need to
//   read is precomputed here rather than left to each modifier to go looking
//   for, so modifiers stay cheap and cannot accidentally mutate the player.
//
// THE TWO DAMAGE CHANNELS:
//   Amount             absorbed by the shield pool first, remainder hits health
//   ShieldBypassAmount skips Shield.AbsorbDamage entirely and hits health
//
//   ShieldBypassAmount is a generic second channel, NOT a damage type - this
//   game has no "true damage". It exists so a perk can express partial shield
//   penetration, e.g. "25% of headshot damage bypasses shielding":
//
//     float split = ctx.Amount * 0.25f;
//     ctx.Amount -= split;
//     ctx.ShieldBypassAmount += split;
//
//   A modifier doing that must sit in the >= 300 "final" order band, or it
//   splits a total that later modifiers still intend to change.
//
// NULLS ARE EXPECTED:
//   Health also lives on Dummy targets, which have no shield, no memory and no
//   modifiers; and Attacker may be null for a future hazard or self-damage
//   source. Every reference here can be null and every fraction defaults to a
//   harmless value.
// =============================================================================

using FishNet.Object;
using UnityEngine;

namespace OffAngle.Combat
{
    public struct DamageModifierContext
    {
        // --- The original hit (never mutate) ------------------------------

        /// <summary>The unmodified hit as constructed by the weapon. Read-only record.</summary>
        public readonly DamageInfo Info;

        // --- Who is involved ----------------------------------------------

        public readonly Health Victim;
        public readonly NetworkObject VictimObject;

        /// <summary>May be null (hazards, future self-damage).</summary>
        public readonly NetworkObject AttackerObject;

        /// <summary>The victim's own history. Null on entities without one.</summary>
        public readonly CombatMemory VictimMemory;

        /// <summary>The attacker's history - "have I hit them before", "how many in a row". Null on entities without one.</summary>
        public readonly CombatMemory AttackerMemory;

        // --- Precomputed conditions ---------------------------------------

        /// <summary>Victim health as 0..1. Below-X-health perks read this.</summary>
        public readonly float VictimHealthFraction;

        /// <summary>Victim shield as 0..1. 0 when the victim has no shield at all.</summary>
        public readonly float VictimShieldFraction;

        /// <summary>Attacker health as 0..1. 1 when there is no attacker to read.</summary>
        public readonly float AttackerHealthFraction;

        /// <summary>True for a Head hitbox. Set by HitResolution as DamageCategory.Critical.</summary>
        public readonly bool IsHeadshot;

        // --- The running result (this is what modifiers write) ------------

        /// <summary>Damage the shield absorbs first. Starts at Info.Amount.</summary>
        public float Amount;

        /// <summary>Damage routed straight past the shield to health. Starts at 0.</summary>
        public float ShieldBypassAmount;

        public DamageModifierContext(
            DamageInfo info,
            Health victim,
            NetworkObject victimObject,
            Shield victimShield,
            Health attackerHealth,
            CombatMemory victimMemory,
            CombatMemory attackerMemory)
        {
            Info = info;
            Victim = victim;
            VictimObject = victimObject;
            AttackerObject = info.Attacker;
            VictimMemory = victimMemory;
            AttackerMemory = attackerMemory;

            VictimHealthFraction = victim != null ? victim.Normalized : 1f;
            VictimShieldFraction = victimShield != null ? victimShield.Normalized : 0f;
            AttackerHealthFraction = attackerHealth != null ? attackerHealth.Normalized : 1f;
            IsHeadshot = info.Category == DamageCategory.Critical;

            Amount = Mathf.Max(0f, info.Amount);
            ShieldBypassAmount = 0f;
        }
    }
}
