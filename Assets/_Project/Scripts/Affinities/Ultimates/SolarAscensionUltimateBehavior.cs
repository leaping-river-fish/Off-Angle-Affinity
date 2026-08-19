// =============================================================================
// SolarAscensionUltimateBehavior — Cinder ultimate. Lifts the player into a
// hovering "ascended" form: locked in place but free to turn/aim, gun hidden,
// firing spawns fireballs instead, +bonus shield for the duration (the player
// can still die - no invulnerability), and a much larger presentation scale.
//
// Stateless, shared tuning record - same rule as every other UltimateBehavior/
// ShotBehavior asset. All the actual per-activation coordination lives on
// SolarAscensionEffect, which this just delegates to.
//
// Placed alongside NoOpUltimateBehavior.cs (not in a per-affinity folder),
// matching how every ShotBehavior subclass shares one flat folder regardless
// of which weapon uses it.
//
// Create instances via: Assets > Create > Off-Angle > Affinities > Ultimates > Solar Ascension
// =============================================================================

using OffAngle.Combat;
using OffAngle.Weapons;
using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Ultimates/Solar Ascension", fileName = "Ultimate_SolarAscension")]
    public class SolarAscensionUltimateBehavior : UltimateBehavior
    {
        [Header("Duration")]
        [Tooltip("Total seconds the ascended form lasts.")]
        [SerializeField, Min(0.1f)] private float _duration = 8f;

        [Header("Flight")]
        [Tooltip("Meters the player rises above their starting position.")]
        [SerializeField, Min(0f)] private float _riseHeight = 50f;
        [Tooltip("Seconds the rise takes. Kept short and eased rather than instant, so the ascent doesn't clip through ceiling geometry.")]
        [SerializeField, Min(0.05f)] private float _riseTime = 0.35f;

        [Header("Presentation")]
        [Tooltip("Uniform scale multiplier applied to the player's third-person model while ascended - also scales the hit colliders parented under it (see SolarAscensionEffect).")]
        [SerializeField, Min(1f)] private float _scaleMultiplier = 3f;

        [Header("Shield")]
        [Tooltip("Bonus shield granted for the duration, removed the moment the ultimate ends.")]
        [SerializeField, Min(0f)] private float _bonusShield = 500f;

        [Header("Fireball")]
        [Tooltip("Weapon tuning (damage/range/mask) used while firing fireballs. Not equipped by any loadout - only ever passed directly into a ShotContext.")]
        [SerializeField] private GunData _fireballData;
        [Tooltip("The Projectile-spawning behavior fired while ascended. Assign the fireball's ProjectileShotBehavior asset (with SplashRadius/SplashDamage and an impact ground-effect zone configured).")]
        [SerializeField] private ProjectileShotBehavior _fireballShotBehavior;

        public override void ServerActivate(AffinityRuntimeContext ctx)
        {
            SolarAscensionEffect effect = ctx?.PlayerRoot != null ? ctx.PlayerRoot.GetComponent<SolarAscensionEffect>() : null;
            if (effect == null)
            {
                Debug.LogError($"[{nameof(SolarAscensionUltimateBehavior)}] Player prefab is missing SolarAscensionEffect - Solar Ascension cannot activate.");
                return;
            }

            effect.ServerBeginAscension(_duration, _riseHeight, _riseTime, _scaleMultiplier, _bonusShield, _fireballData, _fireballShotBehavior);
        }
    }
}
