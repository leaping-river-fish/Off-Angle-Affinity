// =============================================================================
// IDamageable — contract for anything that can receive damage.
//
// Implementations must ensure ApplyDamage only mutates authoritative state on
// the server. Clients calling ApplyDamage directly would be a cheat vector.
// =============================================================================

namespace OffAngle.Combat
{
    public interface IDamageable
    {
        void ApplyDamage(DamageInfo info);
    }
}
