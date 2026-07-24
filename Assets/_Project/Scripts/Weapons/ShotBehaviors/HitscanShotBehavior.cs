// =============================================================================
// HitscanShotBehavior — instant raycast shot. Rifle/pistol/SMG-style weapons.
//
// This is the migrated version of the raycast block that used to live inline
// in PlayerWeaponController.CmdFire - behavior is unchanged. GunData.Range,
// GunData.HitMask, GunData.Damage, and GunData.HeadshotDamage already cover
// everything this needs, so this asset has no fields of its own.
//
// A GunData with no ShotBehavior assigned falls back to a shared instance of
// this class (see PlayerWeaponController), so this is also what every
// existing weapon asset uses today without any changes required.
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Shot Behaviors > Hitscan
// =============================================================================

using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Shot Behaviors/Hitscan", fileName = "ShotBehavior_Hitscan")]
    public class HitscanShotBehavior : InstantShotBehavior
    {
        public override void Fire(ShotContext ctx)
        {
            GunData data = ctx.Data;

            bool didHit = Physics.Raycast(ctx.Origin, ctx.Direction, out RaycastHit hit, data.Range, data.HitMask, QueryTriggerInteraction.Ignore);

            // Tracer fires regardless of hit/miss - bullets are visible even when they go nowhere.
            Vector3 tracerEnd = didHit ? hit.point : ctx.Origin + ctx.Direction * data.Range;
            ctx.Host.PlayTracer(ctx.Host.MuzzlePosition, tracerEnd);

            if (!didHit) return;

            HitResolution.TryResolveAndApply(hit, ctx.AttackerRoot, ctx.Attacker, data, data.Damage, data.HeadshotDamage, out _);
        }
    }
}
