// =============================================================================
// DeathInfo — local, client-side payload describing a completed death.
//
// Not networked itself. PlayerLifecycleController builds this from the
// parameters of the RpcOnDied observer call (which IS networked) and hands it
// to local C# events (OnLocalDied, AnyPlayerDied) for UI/camera/ragdoll
// subscribers. Mirrors DamageInfo's role on the server side, but safe to
// construct on every peer.
// =============================================================================

using FishNet.Object;

namespace OffAngle.Combat
{
    public readonly struct DeathInfo
    {
        /// <summary>The NetworkObject that died. Convenience so subscribers do not need a separate reference.</summary>
        public readonly NetworkObject Victim;

        /// <summary>The NetworkObject responsible for the kill. May be null (e.g. future environmental deaths).</summary>
        public readonly NetworkObject Attacker;

        /// <summary>Display label for the weapon used. Falls back to a placeholder when unknown.</summary>
        public readonly string WeaponLabel;

        /// <summary>Seconds until respawn, as of the moment death was broadcast.</summary>
        public readonly float RespawnDuration;

        public DeathInfo(NetworkObject victim, NetworkObject attacker, string weaponLabel, float respawnDuration)
        {
            Victim = victim;
            Attacker = attacker;
            WeaponLabel = weaponLabel;
            RespawnDuration = respawnDuration;
        }
    }
}
