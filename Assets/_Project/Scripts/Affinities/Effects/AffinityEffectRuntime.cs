// =============================================================================
// AffinityEffectRuntime — the per-player, stateful half of an AffinityEffect.
//
// LIFECYCLE (driven entirely by PlayerAffinity):
//   OnAttach   once, when the loadout is applied on this peer
//   OnTick     every frame while attached
//   OnRespawn  after this player respawns, AFTER MovementStateMachine's
//              ResetTransientInput() has already run
//   OnDetach   once, on despawn or if the loadout is ever re-applied
//
// SYMMETRY IS THE CONTRACT:
//   Whatever OnAttach acquires - PlayerStats handles, PlayerDamageModifiers
//   registrations, event subscriptions, spawned objects - OnDetach must
//   release. Nothing else cleans up after an effect. A runtime that leaks a
//   PlayerStats handle leaves a permanent buff on a player whose perk is gone.
//
// WHY OnRespawn EXISTS:
//   MovementStateMachine.ResetTransientInput() wipes SpeedMultiplier and
//   GravityMultiplier back to 1. It runs on death, respawn, and menu — not
//   on pause. Anything an effect pushed directly into the movement context
//   has to be re-pushed here. Effects that route through PlayerStats instead
//   are unaffected and can ignore this hook entirely.
//
// AUTHORING SHAPE:
//   Keep the runtime as a private nested class inside its AffinityEffect so a
//   new effect stays one file:
//
//     public override AffinityEffectRuntime CreateRuntime() => new Runtime(this);
//     private class Runtime : AffinityEffectRuntime { ... }
// =============================================================================

namespace OffAngle.Affinities
{
    public abstract class AffinityEffectRuntime
    {
        /// <summary>Acquire resources, register modifiers, subscribe to events.</summary>
        public virtual void OnAttach(AffinityRuntimeContext ctx) { }

        /// <summary>Release everything OnAttach acquired. Must be safe to call even if OnAttach bailed early.</summary>
        public virtual void OnDetach(AffinityRuntimeContext ctx) { }

        /// <summary>
        /// Re-apply anything a respawn cleared. Runs after ResetTransientInput(),
        /// so it is safe to write straight into the movement context here.
        /// </summary>
        public virtual void OnRespawn(AffinityRuntimeContext ctx) { }

        /// <summary>Per-frame work: buff timers, stack decay, periodic procs.</summary>
        public virtual void OnTick(AffinityRuntimeContext ctx, float deltaTime) { }
    }
}
