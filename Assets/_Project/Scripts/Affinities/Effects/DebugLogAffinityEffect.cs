// =============================================================================
// DebugLogAffinityEffect — a VERIFICATION HARNESS, not a gameplay effect.
//
// WHY IT EXISTS:
//   Until real effects are written there is no way to prove the pipeline works
//   end to end: select -> encode -> sync -> server-validate -> attach on the
//   right peers -> survive respawn -> detach on despawn. Dropping one of these
//   on a placeholder perk makes every one of those steps visible in the
//   Console.
//
//   It is also the only place in the system where Scope is a SERIALIZED field
//   rather than code-declared. That is deliberate and specific to testing:
//   verifying scope filtering means putting the SAME effect on Server, Owner
//   and AllPeers and comparing what each peer logs. Real effects must keep
//   declaring their scope in code.
//
// Delete or stop referencing this once real effects exist.
// =============================================================================

using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Effects/Debug Log (test harness)", fileName = "Effect_DebugLog")]
    public class DebugLogAffinityEffect : AffinityEffect
    {
        [Header("Test harness")]
        [Tooltip("Which peers this effect attaches on. Serialized ONLY because this is the scope-filtering test harness - real effects declare Scope in code.")]
        [SerializeField] private AffinityEffectScope _scope = AffinityEffectScope.Server;

        [Tooltip("Prefix so several of these on the same player are distinguishable in the Console.")]
        [SerializeField] private string _label = "DebugEffect";

        [Tooltip("Also log every OnTick. Extremely noisy - leave off unless specifically testing the tick path.")]
        [SerializeField] private bool _logTicks;

        public override AffinityEffectScope Scope => _scope;

        public override AffinityEffectRuntime CreateRuntime() => new Runtime(this);

        private class Runtime : AffinityEffectRuntime
        {
            private readonly DebugLogAffinityEffect _effect;

            public Runtime(DebugLogAffinityEffect effect) => _effect = effect;

            public override void OnAttach(AffinityRuntimeContext ctx) => Log(ctx, "ATTACH");

            public override void OnDetach(AffinityRuntimeContext ctx) => Log(ctx, "DETACH");

            public override void OnRespawn(AffinityRuntimeContext ctx) => Log(ctx, "RESPAWN");

            public override void OnTick(AffinityRuntimeContext ctx, float deltaTime)
            {
                if (_effect._logTicks) Log(ctx, "TICK");
            }

            private void Log(AffinityRuntimeContext ctx, string phase)
            {
                string owner = ctx.PlayerRoot != null ? ctx.PlayerRoot.name : "<unknown>";
                Debug.Log($"[{_effect._label}] {phase} on '{owner}' (scope={_effect._scope}, isServer={ctx.IsServer}, isOwner={ctx.IsOwner})");
            }
        }
    }
}
