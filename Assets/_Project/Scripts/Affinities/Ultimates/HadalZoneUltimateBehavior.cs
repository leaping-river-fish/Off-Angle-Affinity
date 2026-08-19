// =============================================================================
// HadalZoneUltimateBehavior — Tide ultimate. Fires a projectile exactly like a
// weapon shot - same aim-corrected ProjectileShotBehavior.Fire path Solar
// Ascension's fireball and every equipped gun use - that leaves behind a
// Hadal Zone (GroundEffectZone + HadalZoneGroundEffectPayload) on impact:
// grounds, nearsights, and (once a sound system exists) muffles anyone
// standing in it.
//
// WHY THIS CAN BE AIM-CORRECTED (unlike a hand-rolled toss):
//   UltimateBehavior.ServerActivate now receives the owner's trusted aim ray
//   (aimOrigin/aimDirection), captured via PlayerWeaponController.
//   GetTrustedAimRay the instant PlayerUltimate.TryActivate() fires - the
//   same camera-based ray CmdFire sends for every normal shot. That is
//   enough to build a real ShotContext and hand it straight to
//   _projectileShotBehavior.Fire(), reusing its aim-correction/splash/
//   impact-zone logic unchanged rather than reimplementing any of it here.
//
// WHY THERE IS NO WEAPON-SWITCH/OVERRIDE STATE (unlike Solar Ascension):
//   Solar Ascension's fireball needs an override (ServerSetAscensionFireOverride)
//   because the player can fire many of them over an 8s window by pulling the
//   trigger repeatedly - the override is what redirects HandleFireStarted for
//   that whole window. Hadal Zone fires exactly once, immediately, using aim
//   data already in hand at activation time - there is no "wait for the next
//   trigger pull" to redirect, so there is nothing to switch to and back from.
//   No per-activation coordinator component is needed either (contrast
//   SolarAscensionEffect) - this is a single fire-and-forget spawn, not an
//   ongoing player state.
//
// Placed alongside NoOpUltimateBehavior.cs/SolarAscensionUltimateBehavior.cs,
// same flat-folder convention as every other UltimateBehavior/ShotBehavior.
//
// Create instances via: Assets > Create > Off-Angle > Affinities > Ultimates > Hadal Zone
// =============================================================================

using OffAngle.Weapons;
using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Ultimates/Hadal Zone", fileName = "Ultimate_HadalZone")]
    public class HadalZoneUltimateBehavior : UltimateBehavior
    {
        [Header("Projectile")]
        [Tooltip("Weapon tuning for the throw (Damage/HeadshotDamage should be 0 - this is a utility throw, not direct damage - HitMask/Range should still cover players and world geometry so aim-correction and collision work). Not equipped by any loadout - only ever passed directly into a ShotContext.")]
        [SerializeField] private GunData _projectileData;
        [Tooltip("The Projectile-spawning config fired on activation. Assign the Hadal Zone throw's ProjectileShotBehavior asset, with ImpactZonePrefab set to the Hadal Zone GroundEffectZone prefab.")]
        [SerializeField] private ProjectileShotBehavior _projectileShotBehavior;

        public override void ServerActivate(AffinityRuntimeContext ctx, Vector3 aimOrigin, Vector3 aimDirection)
        {
            if (ctx?.PlayerRoot == null || ctx.Weapons == null)
            {
                Debug.LogError($"[{nameof(HadalZoneUltimateBehavior)}] Context is missing PlayerRoot/Weapons - Hadal Zone cannot activate.");
                return;
            }

            if (_projectileData == null || _projectileShotBehavior == null || _projectileShotBehavior.ProjectilePrefab == null)
            {
                Debug.LogError($"[{nameof(HadalZoneUltimateBehavior)}] Missing GunData/ProjectileShotBehavior/ProjectilePrefab configuration.");
                return;
            }

            if (aimDirection.sqrMagnitude < 0.0001f) return;

            ShotContext shotCtx = new ShotContext(aimOrigin, aimDirection.normalized, _projectileData, ctx.NetworkObject, ctx.PlayerRoot.transform, ctx.Weapons);
            _projectileShotBehavior.Fire(shotCtx);
        }
    }
}
