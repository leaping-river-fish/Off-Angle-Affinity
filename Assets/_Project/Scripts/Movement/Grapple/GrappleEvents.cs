// =============================================================================
// GrappleEvents — static broadcast hub for grapple-hook lifecycle events.
//
// Same convention as ShotEvents.cs (see that file's header): gameplay raises,
// future VFX/audio/rope-renderer systems subscribe, gameplay never subscribes
// to its own broadcasts. Entirely cosmetic - nothing in the grapple ability
// itself (GrapplePullDriver, PlayerGrapple, GrappleHook) reads these events
// back. Raised from ObserversRpc handlers only, so every peer's local
// subscribers fire correctly without any extra network messages - these
// piggyback on RPCs that already exist for gameplay reasons (hook spawn,
// attach/miss resolution, release).
// =============================================================================

using FishNet.Object;
using UnityEngine;

namespace OffAngle.Movement.Grapple
{
    public static class GrappleEvents
    {
        /// <summary>Raised on every peer the instant a hook is spawned (piggybacks on the hook's own NetworkObject spawn message).</summary>
        public static event System.Action<NetworkObject, Vector3, Vector3> HookFired;

        /// <summary>Raised on every peer when a hook attaches to a valid surface.</summary>
        public static event System.Action<NetworkObject, Vector3, Vector3> HookAttached;

        /// <summary>Raised on every peer when a hook fails to attach (entity/excluded surface/lifetime timeout).</summary>
        public static event System.Action<NetworkObject> HookMissed;

        /// <summary>Raised on every peer when an attached hook is released (pull completed, cancelled, or the owner died).</summary>
        public static event System.Action<NetworkObject> HookReleased;

        public static void RaiseHookFired(NetworkObject owner, Vector3 origin, Vector3 direction) => HookFired?.Invoke(owner, origin, direction);
        public static void RaiseHookAttached(NetworkObject owner, Vector3 point, Vector3 normal) => HookAttached?.Invoke(owner, point, normal);
        public static void RaiseHookMissed(NetworkObject owner) => HookMissed?.Invoke(owner);
        public static void RaiseHookReleased(NetworkObject owner) => HookReleased?.Invoke(owner);
    }
}
