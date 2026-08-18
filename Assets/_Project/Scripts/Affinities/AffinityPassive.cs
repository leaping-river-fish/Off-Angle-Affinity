// =============================================================================
// AffinityPassive — the always-on bundle of effects granted by a player's
// PRIMARY affinity.
//
// ARCHITECTURE:
//   Concrete and data-only. It carries display text and a LIST of
//   AffinityEffect assets; it has no logic of its own. Everything that
//   actually happens lives in the effects, which are the polymorphic half of
//   this system (see AffinityEffect).
//
//   Holding a LIST rather than a single effect is what lets one passive span
//   several authority scopes - e.g. a server-side stat grant plus an
//   all-peers cosmetic - since each AffinityEffect declares its own scope.
//
// SECONDARY AFFINITIES DO NOT GET THIS:
//   PlayerAffinity deliberately skips the secondary affinity's passive. A
//   secondary contributes perks only. That rule lives in PlayerAffinity, not
//   here - this asset does not know or care which slot it was selected into.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace OffAngle.Affinities
{
    [CreateAssetMenu(menuName = "Off-Angle/Affinities/Passive", fileName = "Passive_New")]
    public class AffinityPassive : ScriptableObject
    {
        [Header("Presentation")]
        [Tooltip("Player-facing name shown on the affinity's panel in the selection UI.")]
        public string DisplayName;

        [Tooltip("Optional. Icon shown in the selection UI. Falls back to the parent AffinityDefinition's icon if left unset.")]
        public Sprite Icon;

        [Tooltip("Player-facing description of what this passive does. Authored text - nothing generates it from the effect list.")]
        [TextArea(2, 5)]
        public string Description;

        [Header("Behaviour")]
        [Tooltip("Effects applied for as long as this is the player's PRIMARY affinity. Leave empty for a placeholder passive that does nothing yet.")]
        public List<AffinityEffect> Effects = new List<AffinityEffect>();
    }
}
