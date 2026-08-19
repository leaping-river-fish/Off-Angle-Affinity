// =============================================================================
// AffinityRuntimeContext — the reference bag an AffinityEffectRuntime reaches
// the rest of the player through.
//
// ARCHITECTURE:
//   Same idea as MovementStateContext: resolve every component ONCE (in
//   PlayerAffinity.Awake) and hand effects a plain data object, so no effect
//   ever calls GetComponent and no effect needs to know how the player prefab
//   is assembled. Adding a system effects should reach = add a field here.
//
//   Fields are public and mutable-by-assignment only because PlayerAffinity
//   populates them at construction; effects must treat every reference as
//   read-only. Any of them may be NULL - this same context shape is used for
//   entities that are not full players (see the Dummy, which has Health but no
//   movement, grapple or weapons), so effects null-check what they use.
//
// AUTHORITY FLAGS:
//   IsServer/IsOwner are captured at attach time and are what an effect checks
//   when it needs finer control than its declared Scope gives - e.g. an
//   AllPeers cosmetic effect that must additionally do something server-side.
//   On a listen host BOTH are true for the host's own player, which is exactly
//   the case that causes double-application bugs; PlayerAffinity guarantees a
//   runtime is attached only once regardless.
// =============================================================================

using FishNet.Object;
using OffAngle.Combat;
using OffAngle.Movement;
using OffAngle.Networking;
using OffAngle.Player;
using UnityEngine;

namespace OffAngle.Affinities
{
    public class AffinityRuntimeContext
    {
        // --- Identity ----------------------------------------------------

        /// <summary>Root GameObject of the player this effect is attached to.</summary>
        public GameObject PlayerRoot;

        /// <summary>This player's NetworkObject - the identity used to compare attacker/victim.</summary>
        public NetworkObject NetworkObject;

        /// <summary>The full validated loadout, so an effect can read sibling picks if it ever needs to.</summary>
        public AffinityLoadout Loadout;

        // --- Authority ---------------------------------------------------

        /// <summary>True on the server. Both this and IsOwner are true on a listen host's own player.</summary>
        public bool IsServer;

        /// <summary>True on the client that owns this player.</summary>
        public bool IsOwner;

        // --- Stat and damage seams (the preferred way to change numbers) --

        /// <summary>Handle-based multiplier/additive stack. Use this rather than writing baselines directly.</summary>
        public PlayerStats Stats;

        /// <summary>Conditional damage modification - outgoing and incoming. Server-side.</summary>
        public PlayerDamageModifiers DamageModifiers;

        /// <summary>Per-target history: who hit whom, when, how many times. Server-side.</summary>
        public CombatMemory CombatMemory;

        // --- Player systems ----------------------------------------------

        public Health Health;
        public Shield Shield;
        public PlayerLifecycleController Lifecycle;
        public MovementStateMachine Movement;
        public PlayerGrapple Grapple;
        public PlayerWeaponController Weapons;

        /// <summary>Seam for debuffs that must reach owner-only systems (movement, camera) on the occupant's own client - see PlayerStatusEffects. May be null (e.g. the Dummy).</summary>
        public PlayerStatusEffects StatusEffects;
    }
}
