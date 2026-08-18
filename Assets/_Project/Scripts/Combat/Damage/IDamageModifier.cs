// =============================================================================
// IDamageModifier — one conditional adjustment to a single incoming hit.
//
// WHY THIS EXISTS SEPARATELY FROM PlayerStats:
//   PlayerStats scales numbers the player owns unconditionally - it has no idea
//   who you are shooting. Anything whose value depends on the TARGET ("more
//   damage below 30% HP"), on HISTORY ("more damage to someone who hasn't hit
//   you lately"), or on the HIT ITSELF ("headshots partially bypass shields")
//   needs the full picture of one exchange. That is what this is.
//
// AUTHORITY:
//   Server only. Modify() runs inside Health.ApplyDamage, which is already
//   gated on IsServerInitialized.
//
// ORDER BANDS:
//   Modifiers run in ascending Order. Two multipliers commute; an additive
//   followed by a multiplicative does not, so the band a modifier picks is
//   part of its meaning:
//
//     < 200    additive        flat +/- damage
//     200-299  multiplicative  percentage scaling
//     >= 300   final           anything that must see the SETTLED total,
//                              in particular splitting damage into
//                              ShieldBypassAmount - split too early and it
//                              splits a number later modifiers then change.
//
// IMPLEMENTATION RULES:
//   - Never mutate ctx.Info; it is the immutable record of the original hit.
//   - Read ctx.Amount as the running total, not as the original damage.
//   - Stay cheap. This runs for every modifier on both parties for every
//     single bullet that lands.
// =============================================================================

namespace OffAngle.Combat
{
    public interface IDamageModifier
    {
        /// <summary>Ascending execution order. See the band convention in this file's header.</summary>
        int Order { get; }

        /// <summary>Server-only. Adjust ctx.Amount and/or ctx.ShieldBypassAmount in place.</summary>
        void Modify(ref DamageModifierContext ctx);
    }
}
