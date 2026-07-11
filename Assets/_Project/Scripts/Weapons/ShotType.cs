// =============================================================================
// ShotType — how a gun delivers damage.
//
// Only Hitscan is currently implemented. Projectile is reserved so GunData
// assets can already declare intent; PlayerWeaponController rejects non-Hitscan
// shots today with an early-out.
// =============================================================================

namespace OffAngle.Weapons
{
    public enum ShotType
    {
        Hitscan = 0,
        Projectile
    }
}
