// =============================================================================
// GroundEffectPayload — the pluggable "what happens while you stand in this
// zone" seam, mirroring UltimateBehavior/AffinityEffect/ShotBehavior exactly:
// each concrete payload is its OWN asset type declaring only the fields it
// needs, and GroundEffectZone (the generic spatial/networking half) never
// branches on which payload it's hosting.
//
// Built fire-first (see FireGroundEffectPayload), but shaped so a future
// water zone (speed boost) or ice zone (momentum preservation) is just a new
// payload asset - OnEnter/OnExit are the hook a stateful effect (a buff that
// must be reverted on exit) uses; OnTick is the hook a continuous effect
// (damage-over-time) uses. A payload may use either, both, or neither.
//
// STATELESS ASSET:
// Same rule as every other ScriptableObject strategy in this codebase - this
// asset is shared by every zone instance that uses it, so it must hold only
// read-only tuning data. Per-zone/per-occupant state belongs on
// GroundEffectZone or on values threaded through AffinityRuntimeContext.
// =============================================================================

using UnityEngine;
using OffAngle.Affinities;

namespace OffAngle.Combat
{
    public abstract class GroundEffectPayload : ScriptableObject
    {
        /// <summary>Server-only. Called once when a player enters the zone.</summary>
        public abstract void OnEnter(AffinityRuntimeContext occupant, GroundEffectZone zone);

        /// <summary>Server-only. Called on every occupant at the zone's tick interval while they remain inside.</summary>
        public abstract void OnTick(AffinityRuntimeContext occupant, GroundEffectZone zone, float deltaTime);

        /// <summary>Server-only. Called once when a player leaves the zone, including a forced exit from the zone expiring while they're still inside.</summary>
        public abstract void OnExit(AffinityRuntimeContext occupant, GroundEffectZone zone);
    }
}
