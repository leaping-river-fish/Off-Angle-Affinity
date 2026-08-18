// =============================================================================
// AffinityEffect — the ONE polymorphic type in the Affinity system, and the
// only place where a passive/perk/ultimate actually does something.
//
// ARCHITECTURE:
//   Directly mirrors the ShotBehavior pattern (see ShotBehavior.cs): each
//   concrete effect is its OWN asset type declaring only the fields it needs.
//   There is no shared "one big config" asset and no switch statement anywhere
//   that branches on which perk this is. Adding an effect is: add a class, add
//   an asset, drop it on a PerkDefinition/AffinityPassive. Nothing else in the
//   codebase changes.
//
//   Because passives and perks hold a LIST of these, one perk can compose
//   several effects - which is how a single perk spans authority scopes (see
//   below) or reuses an effect another perk already needed.
//
// STATELESS ASSET, STATEFUL RUNTIME:
//   ScriptableObject instances are shared by every player using that perk, so
//   this asset must never hold per-player state - all serialized fields here
//   are read-only tuning data. CreateRuntime() produces the per-player object
//   that owns timers, handles and subscriptions. That split is the same rule
//   ShotBehavior documents, and violating it means one player's cooldown is
//   every player's cooldown.
//
// AUTHORITY (Scope):
//   This codebase splits authority hard, so an effect MUST declare where it
//   runs or it will silently do nothing (or run twice):
//     - Shield regen and all damage are SERVER-only.
//     - MovementStateMachine is DISABLED on non-owner players, so movement
//       effects are OWNER-only.
//     - VFX need every peer.
//   An effect needing more than one scope is authored as several effect assets
//   on the same perk, not as one effect with a broader scope.
// =============================================================================

using UnityEngine;

namespace OffAngle.Affinities
{
    /// <summary>Which peers a given effect's runtime is created and attached on.</summary>
    public enum AffinityEffectScope
    {
        /// <summary>Server only. Damage, shield, health, spawning networked objects.</summary>
        Server = 0,

        /// <summary>The owning client only. Movement, input, camera, first-person cosmetics.</summary>
        Owner,

        /// <summary>Every peer. Purely cosmetic effects that everyone must see.</summary>
        AllPeers,
    }

    public abstract class AffinityEffect : ScriptableObject
    {
        [Tooltip("Optional. Short designer-facing note about what this effect does. Player-facing text lives on the PerkDefinition/AffinityPassive that holds this effect, not here.")]
        [TextArea(1, 3)]
        public string EditorNote;

        /// <summary>
        /// Which peers this effect runs on. Deliberately code-declared rather than
        /// serialized: getting it wrong is a silent bug, not a tuning choice.
        /// </summary>
        public abstract AffinityEffectScope Scope { get; }

        /// <summary>
        /// Creates the per-player runtime that holds this effect's state. Called
        /// once per player per attach, only on peers matching Scope. Returning
        /// null is legal and means "nothing to run here".
        /// </summary>
        public abstract AffinityEffectRuntime CreateRuntime();
    }
}
