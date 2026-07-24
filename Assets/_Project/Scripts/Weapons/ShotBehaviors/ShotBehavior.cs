// =============================================================================
// ShotBehavior — abstract base for "what happens when this weapon fires."
//
// ARCHITECTURE:
// This is the polymorphic ScriptableObject strategy GunData.ShotBehavior
// references. Each concrete shot type (HitscanShotBehavior,
// ShotgunShotBehavior, ProjectileShotBehavior, BeamShotBehavior, ...) is its
// OWN asset type declaring only the fields it needs - there is no shared
// "one big config" asset and no switch statement anywhere that branches on
// weapon identity. Adding a new shot type is: add a new class, add a new
// asset, assign it on a GunData. Nothing else in the codebase changes.
//
// ScriptableObject instances are shared across every weapon/player using that
// asset, so instances must stay stateless - all serialized fields here are
// read-only tuning data, never runtime state. Continuous behaviors (Beam)
// keep their per-player runtime bookkeeping on PlayerWeaponController instead
// of on the asset for exactly this reason.
//
// Deliberately has no FishNet dependency - see IShotBehaviorHost.cs for why.
// =============================================================================

using UnityEngine;

namespace OffAngle.Weapons
{
    public abstract class ShotBehavior : ScriptableObject
    {
        /// <summary>How Gun/PlayerWeaponController should wire trigger input for this behavior. See ShotDeliveryKind.</summary>
        public abstract ShotDeliveryKind Kind { get; }
    }
}
