// =============================================================================
// NoOpUltimateBehavior — placeholder so every ultimate asset can be authored
// and wired today, before any of them do anything.
//
// An ultimate with this behavior still charges, still gates on RequiredCharge,
// still activates, still resets charge, and still raises
// AffinityEvents.UltimateActivated on every peer. Only the payload is missing -
// which makes it the right asset for exercising the whole charge/activation
// path and for keeping UltimateDefinition.Behavior non-null (a null Behavior is
// treated as "not usable" by PlayerUltimate).
//
// One shared asset can be reused by every placeholder ultimate; it holds no
// state and no tuning.
// =============================================================================

using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Ultimates/No-Op (placeholder)", fileName = "Ultimate_NoOp")]
    public class NoOpUltimateBehavior : UltimateBehavior
    {
        [Tooltip("Log to the Console on activation. Useful while the real behaviour does not exist yet.")]
        [SerializeField] private bool _logOnActivate = true;

        public override void ServerActivate(AffinityRuntimeContext ctx)
        {
            if (!_logOnActivate) return;

            string owner = ctx?.PlayerRoot != null ? ctx.PlayerRoot.name : "<unknown>";
            Debug.Log($"[{nameof(NoOpUltimateBehavior)}] Ultimate activated by '{owner}' (placeholder - no effect).");
        }
    }
}
