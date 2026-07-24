// =============================================================================
// ShotDeliveryKind — how a shot behavior expects trigger input to be wired.
//
// This is deliberately NOT "what the weapon is" (that's the concrete
// ShotBehavior subclass) - it only tells Gun/PlayerWeaponController which of
// the two input models to use. Instant behaviors (Hitscan, Shotgun,
// Projectile, and later Piercing/Ricochet/Chain) fire discrete shots through
// the existing FireMode loop. Continuous (Beam) and Charged behaviors ignore
// FireMode entirely and drive their own hold-to-sustain / hold-to-charge
// semantics instead. Adding a new shot behavior never requires adding a case
// here - only these two input models exist, chosen once per behavior.
// =============================================================================

namespace OffAngle.Weapons
{
    public enum ShotDeliveryKind
    {
        Instant = 0,
        Continuous,
        Charged
    }
}
