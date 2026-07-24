// =============================================================================
// ShotEvents — static broadcast hub for shot-behavior lifecycle events.
//
// Same convention as Health.DamageFeedback: gameplay raises, future VFX/audio
// systems subscribe, gameplay never subscribes to its own broadcasts. This is
// the ONLY place visual/audio hooks are exposed - none of the ShotBehavior
// classes contain tracer/particle/sound code themselves.
//
// Raised from the cosmetic ObserversRpc handlers on PlayerWeaponController and
// Projectile (never from server-only code), so every peer's local
// subscribers fire correctly and no extra network messages are introduced -
// these events piggyback on RPCs/spawn messages that already exist for
// tracers, beam visuals, and projectile replication.
//
// The Charge/Piercing/Ricochet/Chain events are reserved for the follow-up
// pass (see the plan) so those behaviors won't need a second event hub.
// =============================================================================

using System;
using FishNet.Object;
using UnityEngine;

namespace OffAngle.Weapons
{
    public static class ShotEvents
    {
        public static event Action<NetworkObject, GunData, Vector3, Vector3> ShotFired;
        public static event Action<NetworkObject, GunData, Vector3, Vector3> PelletFired;
        public static event Action<NetworkObject, GunData, NetworkObject>    ProjectileSpawned;
        public static event Action<NetworkObject, GunData, Vector3, Vector3> ProjectileImpacted;

        public static event Action<NetworkObject, GunData>                          BeamStarted;
        public static event Action<NetworkObject, GunData, Vector3, Vector3, bool>  BeamUpdated;
        public static event Action<NetworkObject, GunData, Vector3>                 BeamHit;
        public static event Action<NetworkObject, GunData>                          BeamStopped;

        // Reserved for the Charged/Piercing/Ricochet/Chain follow-up pass.
        public static event Action<NetworkObject, GunData>              ChargeStarted;
        public static event Action<NetworkObject, GunData, float>       ChargeUpdated;
        public static event Action<NetworkObject, GunData, float>       ChargeReleased;
        public static event Action<NetworkObject, GunData, Vector3, int> PiercingHit;
        public static event Action<NetworkObject, GunData, Vector3, int> RicochetHit;
        public static event Action<NetworkObject, GunData, NetworkObject, int> ChainTargetAcquired;

        public static void RaiseShotFired(NetworkObject attacker, GunData weapon, Vector3 origin, Vector3 end) => ShotFired?.Invoke(attacker, weapon, origin, end);
        public static void RaisePelletFired(NetworkObject attacker, GunData weapon, Vector3 origin, Vector3 end) => PelletFired?.Invoke(attacker, weapon, origin, end);
        public static void RaiseProjectileSpawned(NetworkObject attacker, GunData weapon, NetworkObject projectile) => ProjectileSpawned?.Invoke(attacker, weapon, projectile);
        public static void RaiseProjectileImpacted(NetworkObject attacker, GunData weapon, Vector3 point, Vector3 normal) => ProjectileImpacted?.Invoke(attacker, weapon, point, normal);

        public static void RaiseBeamStarted(NetworkObject attacker, GunData weapon) => BeamStarted?.Invoke(attacker, weapon);
        public static void RaiseBeamUpdated(NetworkObject attacker, GunData weapon, Vector3 origin, Vector3 endPoint, bool didHit) => BeamUpdated?.Invoke(attacker, weapon, origin, endPoint, didHit);
        public static void RaiseBeamHit(NetworkObject attacker, GunData weapon, Vector3 hitPoint) => BeamHit?.Invoke(attacker, weapon, hitPoint);
        public static void RaiseBeamStopped(NetworkObject attacker, GunData weapon) => BeamStopped?.Invoke(attacker, weapon);
    }
}
