// =============================================================================
// ShotgunShotBehavior — fires multiple pellets from a single trigger pull.
//
// NETWORKING:
// Pellet directions are computed exclusively here, on the server (this class
// only ever runs from PlayerWeaponController.CmdFire, a ServerRpc). Every
// pellet's raycast is resolved server-side and the resulting endpoints are
// sent to every peer in a single batched RPC (IShotBehaviorHost.PlayTracers)
// so all clients render the exact pattern the server used - clients never
// compute spread themselves, so there is nothing to desync.
//
// Each pellet is a fully independent hit test: multiple pellets can hit the
// same target (including the same headshot hitbox), and total damage is
// naturally bounded by PelletCount * (PelletDamage or PelletHeadshotDamage).
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Shot Behaviors > Shotgun Pellet Burst
// =============================================================================

using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Shot Behaviors/Shotgun Pellet Burst", fileName = "ShotBehavior_Shotgun")]
    public class ShotgunShotBehavior : InstantShotBehavior
    {
        [Header("Pellets")]
        [Tooltip("How many independent raycasts fire per trigger pull.")]
        [Min(1)] public int PelletCount = 8;

        [Tooltip("Damage dealt by a single pellet on a body hit.")]
        [Min(0f)] public float PelletDamage = 8f;

        [Tooltip("Damage dealt by a single pellet on a headshot.")]
        [Min(0f)] public float PelletHeadshotDamage = 16f;

        [Header("Spread")]
        [Tooltip("RandomCone: a new random offset per pellet, every shot. FixedPattern: always uses FixedPattern below, identical every shot.")]
        public SpreadPatternType SpreadPattern = SpreadPatternType.RandomCone;

        [Tooltip("Half-angle in degrees, used when SpreadPattern is RandomCone.")]
        [Min(0f)] public float HorizontalSpread = 4f;
        [Min(0f)] public float VerticalSpread = 4f;

        [Tooltip("Horizontal/vertical degree offsets (X = horizontal, Y = vertical) per pellet, used when SpreadPattern is FixedPattern. Wraps around if shorter than PelletCount, so a symmetric 4- or 8-point pattern can cover any PelletCount.")]
        public Vector2[] FixedPattern;

        public override void Fire(ShotContext ctx)
        {
            GunData data = ctx.Data;
            Vector3 muzzle = ctx.Host.MuzzlePosition;
            Vector3[] tracerEnds = new Vector3[PelletCount];

            for (int i = 0; i < PelletCount; i++)
            {
                Vector3 pelletDirection = ComputePelletDirection(ctx.Direction, i);

                bool didHit = Physics.Raycast(ctx.Origin, pelletDirection, out RaycastHit hit, data.Range, data.HitMask, QueryTriggerInteraction.Ignore);
                tracerEnds[i] = didHit ? hit.point : ctx.Origin + pelletDirection * data.Range;

                if (didHit)
                    HitResolution.TryResolveAndApply(hit, ctx.AttackerRoot, ctx.Attacker, data, PelletDamage, PelletHeadshotDamage, out _);
            }

            ctx.Host.PlayTracers(muzzle, tracerEnds);
        }

        private Vector3 ComputePelletDirection(Vector3 centerDirection, int pelletIndex)
        {
            Vector2 offsetDegrees = SpreadPattern == SpreadPatternType.FixedPattern
                ? SampleFixedPattern(pelletIndex)
                : SampleRandomCone();

            // Build the offset in the identity frame (yaw around up, pitch around
            // right), then rotate the whole thing onto the trusted aim direction -
            // this keeps spread centered on where the player is aiming regardless
            // of that direction's own pitch/yaw.
            Quaternion spread = Quaternion.AngleAxis(offsetDegrees.x, Vector3.up) * Quaternion.AngleAxis(offsetDegrees.y, Vector3.right);
            Quaternion aim = Quaternion.LookRotation(centerDirection, Vector3.up);
            return aim * spread * Vector3.forward;
        }

        private Vector2 SampleRandomCone()
        {
            return new Vector2(
                Random.Range(-HorizontalSpread, HorizontalSpread),
                Random.Range(-VerticalSpread, VerticalSpread));
        }

        private Vector2 SampleFixedPattern(int pelletIndex)
        {
            if (FixedPattern == null || FixedPattern.Length == 0) return Vector2.zero;
            return FixedPattern[pelletIndex % FixedPattern.Length];
        }
    }
}
