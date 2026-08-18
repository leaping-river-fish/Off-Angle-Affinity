// =============================================================================
// AffinityEvents — static broadcast hub for Affinity lifecycle events.
//
// Same convention as ShotEvents.cs and GrappleEvents.cs (see those headers):
// gameplay raises, VFX/audio/UI subscribe, gameplay never subscribes to its own
// broadcasts. Entirely cosmetic - nothing in the Affinity system reads these
// back. Raised from ObserversRpc handlers only, so every peer's local
// subscribers fire without any extra network messages.
// =============================================================================

using System;
using FishNet.Object;

namespace OffAngle.Affinities
{
    public static class AffinityEvents
    {
        /// <summary>
        /// Raised on every peer once a player's validated loadout has been applied.
        /// A nameplate, scoreboard or affinity-tinted UI subscribes here rather
        /// than polling PlayerAffinity.
        /// </summary>
        public static event Action<NetworkObject, AffinityLoadout> LoadoutApplied;

        /// <summary>Raised on every peer when a player activates their ultimate.</summary>
        public static event Action<NetworkObject, UltimateDefinition> UltimateActivated;

        public static void RaiseLoadoutApplied(NetworkObject owner, AffinityLoadout loadout) => LoadoutApplied?.Invoke(owner, loadout);
        public static void RaiseUltimateActivated(NetworkObject owner, UltimateDefinition ultimate) => UltimateActivated?.Invoke(owner, ultimate);
    }
}
