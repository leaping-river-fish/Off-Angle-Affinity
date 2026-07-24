// =============================================================================
// InstantShotBehavior — base for shot behaviors that resolve completely on a
// single trigger pull (Hitscan, Shotgun, Projectile, and later
// Piercing/Ricochet/Chain). Driven by Gun's existing FireMode loop exactly the
// way hitscan already was - one RequestFire in, one Fire() call out.
// =============================================================================

namespace OffAngle.Weapons
{
    public abstract class InstantShotBehavior : ShotBehavior
    {
        public override ShotDeliveryKind Kind => ShotDeliveryKind.Instant;

        /// <summary>Server-only. Resolves this shot immediately (raycast, spawn projectile, etc.).</summary>
        public abstract void Fire(ShotContext ctx);
    }
}
