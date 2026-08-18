// =============================================================================
// UltimateDefinition — one of the three ultimates offered by an affinity.
// A player picks exactly one, and only from their PRIMARY affinity.
//
// ARCHITECTURE:
//   Concrete data + a polymorphic Behavior asset, mirroring exactly how
//   GunData holds a ShotBehavior. Authoring a new ultimate is normally just a
//   new asset; only a genuinely new KIND of ultimate needs a new
//   UltimateBehavior subclass.
//
// CHARGE, NOT COOLDOWN:
//   Ultimates are gated purely by a charge meter that starts at 0 and fills
//   from damage dealt, damage taken, and a slow passive trickle. There is no
//   cooldown anywhere in the system.
//
//   RequiredCharge is THE per-ultimate power knob: a stronger ultimate simply
//   costs more. The GAIN RATES are deliberately NOT here - they live on
//   PlayerUltimate as global tuning, so retuning the economy is one Inspector
//   rather than every ultimate asset in the project. If per-ultimate rate
//   multipliers are ever wanted they can be added here without touching
//   anything else.
// =============================================================================

using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Ultimate", fileName = "Ultimate_New")]
    public class UltimateDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable network id, e.g. \"cinder_ult_firestorm\". Must be unique across EVERY affinity's ultimates and must never change once matches have been played.")]
        public string Id;

        [Tooltip("Player-facing name shown in the selection UI and on the HUD.")]
        public string DisplayName;

        [Tooltip("Optional. Icon shown in the selection UI and on the charge meter.")]
        public Sprite Icon;

        [Tooltip("Player-facing description. Authored text.")]
        [TextArea(2, 5)]
        public string Description;

        [Header("Charge")]
        [Tooltip("Charge POINTS required before this ultimate can be used - not a percentage. This is the power knob: a stronger ultimate costs more. Gain rates are global and live on PlayerUltimate.")]
        [Min(1f)]
        public float RequiredCharge = 100f;

        [Header("Behaviour")]
        [Tooltip("What actually happens on activation. Assign NoOpUltimateBehavior for a placeholder that charges and fires but does nothing.")]
        public UltimateBehavior Behavior;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(Id))
                Debug.LogWarning($"[{nameof(UltimateDefinition)}] '{name}' has no Id. It cannot be selected or synced until one is set.", this);
        }
#endif
    }
}
