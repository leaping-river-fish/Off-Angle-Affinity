// =============================================================================
// BeamShotBehavior — sustained beam weapon (laser-style), rather than a
// sequence of discrete bullets.
//
// This class only contains the pure per-tick gameplay math (raycast + damage).
// All networking - starting/stopping the beam, pacing ticks, consuming ammo,
// and stopping on reload/death/weapon-switch - lives on PlayerWeaponController,
// which drives this through IContinuousShotBehavior. See that interface's
// header for why the split is there.
//
// Create instances via: Assets > Create > Off-Angle > Weapons > Shot Behaviors > Continuous Beam
// =============================================================================

using UnityEngine;

namespace OffAngle.Weapons
{
    [CreateAssetMenu(menuName = "Off-Angle/Weapons/Shot Behaviors/Continuous Beam", fileName = "ShotBehavior_Beam")]
    public class BeamShotBehavior : ShotBehavior, IContinuousShotBehavior
    {
        [Header("Damage")]
        [Tooltip("Damage applied per tick on a body hit. DamagePerTick * TickRate = damage per second.")]
        [Min(0f)] public float DamagePerTick = 4f;

        [Tooltip("Damage applied per tick on a headshot.")]
        [Min(0f)] public float HeadshotDamagePerTick = 8f;

        [Header("Timing")]
        [Tooltip("Damage ticks per second. Also paces how often the owner sends a beam update to the server - never every rendered frame.")]
        [Min(1f)] public float TickRate = 10f;

        [Header("Damage Ramp")]
        [Tooltip("Seconds of continuous fire needed to reach max damage. 0 disables ramping (always deals base DamagePerTick/HeadshotDamagePerTick).")]
        [Min(0f)] public float RampUpTime = 2f;

        [Tooltip("DamagePerTick is multiplied by up to this much as the beam ramps from 0 to RampUpTime seconds of continuous hold.")]
        [Min(1f)] public float MaxDamageMultiplier = 2f;

        [Tooltip("HeadshotDamagePerTick is multiplied by up to this much as the beam ramps from 0 to RampUpTime seconds of continuous hold.")]
        [Min(1f)] public float MaxHeadshotDamageMultiplier = 2f;

        [Header("Range")]
        [Min(0f)] public float Range = 60f;

        [Header("Ammo")]
        [Tooltip("Magazine rounds consumed per tick. Supports fractional values (e.g. 0.5 = one round every two ticks); combine with TickRate for an effective ammo-per-second rate.")]
        [Min(0f)] public float AmmoPerTick = 1f;

        public override ShotDeliveryKind Kind => ShotDeliveryKind.Continuous;

        float IContinuousShotBehavior.TickRate => TickRate;
        float IContinuousShotBehavior.AmmoPerTick => AmmoPerTick;

        public BeamTickResult Tick(ShotContext ctx)
        {
            bool didHit = Physics.Raycast(ctx.Origin, ctx.Direction, out RaycastHit hit, Range, ctx.Data.HitMask, QueryTriggerInteraction.Ignore);
            Vector3 endPoint = didHit ? hit.point : ctx.Origin + ctx.Direction * Range;

            if (didHit)
            {
                float t = RampUpTime > 0f ? Mathf.Clamp01(ctx.HeldDuration / RampUpTime) : 1f;
                float damage = DamagePerTick * Mathf.Lerp(1f, MaxDamageMultiplier, t);
                float headshotDamage = HeadshotDamagePerTick * Mathf.Lerp(1f, MaxHeadshotDamageMultiplier, t);
                HitResolution.TryResolveAndApply(hit, ctx.AttackerRoot, ctx.Attacker, ctx.Data, damage, headshotDamage, out _);
            }

            return new BeamTickResult(didHit, endPoint);
        }
    }
}
