// =============================================================================
// ProjectileShotBehavior — spawns a networked Projectile instead of resolving
// damage immediately. Rockets/grenades/plasma bolts/arrows.
//
// MUZZLE VS AIM CORRECTION:
// The projectile visually spawns at the weapon muzzle, but its initial
// direction is corrected to point at whatever the player's crosshair is
// actually resting on - otherwise a muzzle offset from the camera (normal for
// any first/third-person gun) would make close-range shots visibly miss
// their target. This does a quick server-side raycast along the trusted
// camera aim ray to find that point, then aims the projectile from the
// muzzle toward it - the same technique Titanfall/Halo-style projectile
// weapons use.
//
// NETWORKING:
// Spawning happens exactly once, server-side, via IShotBehaviorHost.
// SpawnProjectile (which wraps ServerManager.Spawn). See Projectile.cs for
// why this makes duplicate damage impossible.
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Shot Behaviors > Projectile
// =============================================================================

using FishNet.Object;
using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Shot Behaviors/Projectile", fileName = "ShotBehavior_Projectile")]
    public class ProjectileShotBehavior : InstantShotBehavior
    {
        [Header("Prefab")]
        [Tooltip("Must have a NetworkObject, NetworkTransform, Rigidbody, and Collider in addition to the Projectile component.")]
        public Projectile ProjectilePrefab;

        [Header("Flight")]
        [Min(0f)] public float Speed = 40f;
        public bool UseGravity = false;
        [Tooltip("Seconds before the projectile despawns itself if it hasn't hit anything.")]
        [Min(0.01f)] public float Lifetime = 5f;

        [Header("Splash (optional)")]
        [Tooltip("0 = direct-impact damage only.")]
        [Min(0f)] public float SplashRadius = 0f;
        [Min(0f)] public float SplashDamage = 0f;

        public override void Fire(ShotContext ctx)
        {
            if (ProjectilePrefab == null) return;

            GunData data = ctx.Data;
            Vector3 muzzle = ctx.Host.MuzzlePosition;
            Vector3 direction = ComputeAimCorrectedDirection(ctx, muzzle);

            NetworkObject spawned = ctx.Host.SpawnProjectile(ProjectilePrefab.NetworkObject, muzzle, Quaternion.LookRotation(direction, Vector3.up));
            if (spawned == null) return;

            Projectile projectile = spawned.GetComponent<Projectile>();
            if (projectile != null)
                projectile.ServerInitialize(ctx.Attacker, ctx.AttackerRoot, data, this, direction * Speed);
        }

        private Vector3 ComputeAimCorrectedDirection(ShotContext ctx, Vector3 muzzle)
        {
            float referenceDistance = ctx.Data.Range > 0f ? ctx.Data.Range : 500f;

            Vector3 aimPoint = Physics.Raycast(ctx.Origin, ctx.Direction, out RaycastHit hit, referenceDistance, ctx.Data.HitMask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : ctx.Origin + ctx.Direction * referenceDistance;

            Vector3 toAimPoint = aimPoint - muzzle;
            return toAimPoint.sqrMagnitude > 0.0001f ? toAimPoint.normalized : ctx.Direction;
        }
    }
}
