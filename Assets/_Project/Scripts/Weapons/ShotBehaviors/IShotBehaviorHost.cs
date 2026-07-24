// =============================================================================
// IShotBehaviorHost — the seam ShotBehavior assets use to reach the handful of
// things only a NetworkBehaviour can do (spawn a networked prefab, broadcast a
// cosmetic RPC). Implemented by PlayerWeaponController.
//
// WHY THIS EXISTS:
// ShotBehavior assets are plain ScriptableObjects with no FishNet dependency
// (see ShotBehavior.cs) so the same gameplay math can run without caring who
// owns the network connection. This interface is the only way a behavior can
// reach into networking, and it only exposes cosmetic/spawn operations -
// never anything that mutates authoritative state (ammo/health stay owned by
// PlayerWeaponController and Health respectively).
// =============================================================================

using FishNet.Object;
using UnityEngine;

namespace OffAngle.Weapons
{
    public interface IShotBehaviorHost
    {
        /// <summary>World position of the weapon's muzzle (Gun.FirePoint), used as the tracer/projectile spawn point.</summary>
        Vector3 MuzzlePosition { get; }

        /// <summary>Broadcasts a single tracer streak to every peer (hit or miss). Purely cosmetic.</summary>
        void PlayTracer(Vector3 start, Vector3 end);

        /// <summary>Broadcasts one tracer streak per endpoint in a single batched RPC (e.g. shotgun pellets). Purely cosmetic.</summary>
        void PlayTracers(Vector3 start, Vector3[] ends);

        /// <summary>
        /// Server-only. Instantiates and networks-spawns a projectile prefab.
        /// Returns null off the server or if the prefab is unassigned.
        /// </summary>
        NetworkObject SpawnProjectile(NetworkObject prefab, Vector3 position, Quaternion rotation);
    }
}
