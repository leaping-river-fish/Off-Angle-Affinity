// =============================================================================
// UltimateBehavior — what actually happens when an ultimate is activated.
//
// ARCHITECTURE:
//   The polymorphic half of UltimateDefinition, exactly as ShotBehavior is the
//   polymorphic half of GunData. UltimateDefinition carries the data (name,
//   icon, RequiredCharge); this carries the behaviour. Adding a new KIND of
//   ultimate is a new subclass + asset and changes nothing else.
//
// AUTHORITY:
//   ServerActivate runs ONLY on the server, after PlayerUltimate has already
//   re-validated charge, alive state and movement availability. Implementations
//   must not assume they are on the owning client and must not touch
//   owner-only systems (camera, input, MovementStateMachine) directly - reach
//   the owner with a TargetRpc from a component instead.
//
// STATELESS:
//   Same rule as AffinityEffect and ShotBehavior: this asset is shared by every
//   player who picked that ultimate, so it must hold only read-only tuning
//   data. Anything per-player belongs on a component or an
//   AffinityEffectRuntime.
//
// COSMETICS:
//   Do not broadcast VFX from here. PlayerUltimate already raises
//   AffinityEvents.UltimateActivated on every peer after this returns.
// =============================================================================

using UnityEngine;

namespace OffAngle.Affinities
{
    public abstract class UltimateBehavior : ScriptableObject
    {
        /// <summary>
        /// Server-only. Called once per activation, after all gating has passed and
        /// immediately before the player's charge is reset to zero.
        /// </summary>
        /// <param name="aimOrigin">
        /// The owner's trusted camera-based aim ray at the moment of
        /// activation - same PlayerWeaponController.GetTrustedAimRay every
        /// shot uses (muzzle-clearance already applied). Only meaningful for
        /// an ultimate that throws/fires something (e.g. Hadal Zone); a
        /// self-targeted ultimate (e.g. Solar Ascension) ignores both
        /// parameters, same as ShotContext.HeldDuration is ignored by
        /// behaviors with no concept of a hold.
        /// </param>
        /// <param name="aimDirection">Trusted, normalized aim direction paired with aimOrigin.</param>
        public abstract void ServerActivate(AffinityRuntimeContext ctx, Vector3 aimOrigin, Vector3 aimDirection);
    }
}
