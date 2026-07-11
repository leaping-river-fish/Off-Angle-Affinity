// =============================================================================
// AffinityType — placeholder enum for future elemental effects.
//
// Combat currently ignores the value entirely; every affinity deals the same
// damage. The enum exists so DamageInfo can carry the tag today without
// needing signature changes later when Frost slows, Cinder burns, etc. are
// implemented.
// =============================================================================

namespace OffAngle.Combat
{
    public enum AffinityType
    {
        None = 0,
        Cinder,
        Tide,
        Thorn,
        Tempest,
        Frost,
        Void
    }
}
