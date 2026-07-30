// =============================================================================
// PlayerGrapple — bridges owner input to the server-authoritative grapple hook
// and the owner-local pull ability. Sibling to PlayerWeaponController/
// NetworkPlayerCrouch: the FishNet-aware bridge for a gameplay feature whose
// actual logic (GrapplePullDriver) lives in Scripts/Movement with zero
// networking dependency.
//
// FLOW:
//   1. Owner client sees PlayerInputReader.GrappleStarted (right mouse button).
//      Local gates: not already grappling, off cooldown,
//      MovementStateMachine.CanStartMovementAction() (same seam Sliding's
//      restrictions already read - see MovementStateMachine.cs).
//   2. CmdFireGrapple(origin, direction) is sent to the server, which
//      re-validates cooldown/death, spawns a GrappleHook, and hands it a
//      ServerOnHookResolved callback.
//   3. GrappleHook flies and resolves itself (see GrappleHook.cs for the
//      "any surface, not entities" collision rule) and reports back through
//      that callback exactly once.
//   4. On a hit, the server sends a TargetRpc (owner only - other peers don't
//      run this player's MovementStateMachine, see NetworkPlayerController)
//      with the anchor point. The owner begins the pull locally via
//      MovementStateMachine.BeginAbilityMovement(new GrapplePullDriver(...)).
//   5. Releasing the Grapple button mid-flight cancels the in-flight hook
//      (no pull ever starts); releasing it mid-pull calls
//      MovementStateMachine.InterruptCurrentAction() - the "slingshot" cancel,
//      which preserves ctx.Velocity exactly as GrapplePullDriver left it
//      (see GrapplePullDriver.cs's MOMENTUM CONTRACT).
//   6. GrapplePullDriver.Exit() fires HandlePullExited below with a
//      GrapplePullExitReason. A real arrival (Arrived) begins
//      GrappleWallHoldDriver instead of releasing - the player freezes at
//      the wall for GrappleWallHoldDuration seconds (Jump ends it early).
//      Every other reason (Interrupted/TimedOut/Obstructed) sends
//      CmdReleaseGrapple() immediately, exactly like before this phase existed.
//   7. GrappleWallHoldDriver.Exit() fires HandleHoldExited, which sends
//      CmdReleaseGrapple() to despawn the now-obsolete hook - whether the
//      hold ran its full course, was cut short by Jump, or was interrupted
//      (death/respawn).
//
// AUTHORITY NOTE:
//   Movement itself stays fully client-authoritative, same model as every
//   other ability in this project (see MovementStateMachine.cs's header) -
//   the server only arbitrates the hook's flight/collision and the
//   cooldown/death gate on firing, exactly like PlayerWeaponController only
//   arbitrates ammo/rate/death and never re-validates movement state.
// =============================================================================

using FishNet;
using FishNet.Connection;
using FishNet.Object;
using OffAngle.Combat;
using OffAngle.Core;
using OffAngle.Movement;
using OffAngle.Movement.Grapple;
using UnityEngine;

namespace OffAngle.Networking
{
    public class PlayerGrapple : NetworkBehaviour
    {
        private enum GrappleState { Idle, HookInFlight, Pulling, Holding }

        [Header("References")]
        [SerializeField] private PlayerInputReader _inputReader;
        [SerializeField] private MovementStateMachine _stateMachine;
        [Tooltip("Transform whose position/forward defines the aim ray on the owner client (usually the player camera). Same transform PlayerWeaponController uses.")]
        [SerializeField] private Transform _cameraTransform;
        [Tooltip("Optional. When assigned, CmdFireGrapple rejects requests while this reports the player as dead - a server-side backstop in case a modified client bypasses the owner-side check.")]
        [SerializeField] private PlayerLifecycleController _lifecycle;

        [Header("Hook Prefab")]
        [Tooltip("Must have a NetworkObject, NetworkTransform, Rigidbody, and a TRIGGER Collider in addition to the GrappleHook component.")]
        [SerializeField] private GrappleHook _hookPrefab;

        [Header("Flight")]
        [Min(0f)] [SerializeField] private float _hookSpeed = 55f;
        [Tooltip("Also used to derive the hook's lifetime (Range / Speed) - a hook that hasn't hit anything by then despawns as a miss.")]
        [Min(0f)] [SerializeField] private float _maxRange = 40f;
        [SerializeField] private bool _useGravity = false;
        [Tooltip("Distance the aim ray's origin is pushed forward along the camera's forward direction before being sent to the server - clears the player's own colliders, same purpose as PlayerWeaponController's muzzle clearance.")]
        [SerializeField, Min(0f)] private float _muzzleClearanceDistance = 0.6f;

        [Header("Targeting / Collision")]
        [Tooltip("If true, the hook passes through anything with an IDamageable in its parent chain (players, the Dummy target, ...) instead of attaching to it. See GrappleHook.cs for the full rule and why layers alone aren't enough in this project.")]
        [SerializeField] private bool _ignoreDamageableEntities = true;
        [Tooltip("Layers the hook can never attach to, regardless of the toggle above (e.g. water, glass, VFX-only colliders).")]
        [SerializeField] private LayerMask _excludedLayers = 0;

        [Header("Cooldown")]
        [Tooltip("Minimum seconds between grapple attempts.")]
        [SerializeField, Min(0.05f)] private float _cooldown = 0.4f;
        [Tooltip("Fraction of the cooldown the server allows as jitter grace, same purpose as PlayerWeaponController's fire-rate grace.")]
        [SerializeField, Range(0f, 0.5f)] private float _serverCooldownGrace = 0.05f;

        private GrappleState _state = GrappleState.Idle;
        private float _ownerNextAllowedFireTime;
        private float _serverNextAllowedFireTime;

        // Server-only - the hook currently owned by this player, if any.
        private GrappleHook _activeHook;

        public bool IsGrappling => _state == GrappleState.Pulling || _state == GrappleState.Holding;
        public bool IsHookInFlight => _state == GrappleState.HookInFlight;

        // ------------------------------------------------------------------
        // Lifecycle - subscribe only for the owning client
        // ------------------------------------------------------------------

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!base.IsOwner) return;
            if (_inputReader == null) return;

            _inputReader.GrappleStarted += HandleGrappleStarted;
            _inputReader.GrappleCanceled += HandleGrappleCanceled;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!base.IsOwner) return;
            if (_inputReader == null) return;

            _inputReader.GrappleStarted -= HandleGrappleStarted;
            _inputReader.GrappleCanceled -= HandleGrappleCanceled;
        }

        // ------------------------------------------------------------------
        // Owner-side input handling
        // ------------------------------------------------------------------

        private void HandleGrappleStarted()
        {
            if (_state != GrappleState.Idle) return;
            if (Time.time < _ownerNextAllowedFireTime) return;
            if (_lifecycle != null && _lifecycle.IsDead) return;
            if (_stateMachine != null && !_stateMachine.CanStartMovementAction()) return;

            _ownerNextAllowedFireTime = Time.time + _cooldown;

            GetAimRay(out Vector3 origin, out Vector3 direction);
            _state = GrappleState.HookInFlight;
            CmdFireGrapple(origin, direction);
        }

        private void HandleGrappleCanceled()
        {
            switch (_state)
            {
                case GrappleState.HookInFlight:
                    // Hook hasn't attached yet - recall it, no pull ever starts.
                    _state = GrappleState.Idle;
                    CmdCancelInFlightHook();
                    break;

                case GrappleState.Pulling:
                    // The slingshot cancel: InterruptCurrentAction() calls
                    // GrapplePullDriver.Exit(interrupted: true) synchronously,
                    // which invokes HandlePullExited below WITHOUT touching
                    // ctx.Velocity - momentum is kept.
                    _stateMachine?.InterruptCurrentAction();
                    break;

                // GrappleState.Holding is intentionally NOT handled here -
                // releasing/re-pressing the Grapple input does nothing while
                // held to the wall. The only way out early is Jump (read
                // directly by GrappleWallHoldDriver.Tick()); otherwise the
                // hold simply runs its course. See CancelGrapple() below for
                // the one path (death) that DOES force-end a Holding grapple.
            }
        }

        /// <summary>
        /// Owner-only. Force-ends whatever grapple activity is in progress -
        /// called by PlayerLifecycleController on death so a mid-flight hook,
        /// an active pull, or an active wall hold never survives past the
        /// moment the player dies. No-op if nothing is in progress. Unlike
        /// HandleGrappleCanceled() (wired to the player releasing the
        /// Grapple button), this DOES end a Holding grapple early - death
        /// isn't a normal cancel input, it must always win.
        /// </summary>
        public void CancelGrapple()
        {
            if (!base.IsOwner) return;

            switch (_state)
            {
                case GrappleState.HookInFlight:
                    _state = GrappleState.Idle;
                    CmdCancelInFlightHook();
                    break;

                case GrappleState.Pulling:
                case GrappleState.Holding:
                    _stateMachine?.InterruptCurrentAction();
                    break;
            }
        }

        private void GetAimRay(out Vector3 origin, out Vector3 direction)
        {
            origin = _cameraTransform != null ? _cameraTransform.position : transform.position;
            direction = _cameraTransform != null ? _cameraTransform.forward : transform.forward;
            origin += direction * _muzzleClearanceDistance;
        }

        // ------------------------------------------------------------------
        // Owner-only reaction to the server's resolution of the hook
        // ------------------------------------------------------------------

        [TargetRpc]
        private void TargetRpcHookAttached(NetworkConnection conn, Vector3 point)
        {
            // Re-check rather than trust the state from the moment of firing -
            // the player may have died, started sliding-with-abilities-disabled,
            // or otherwise changed state during the hook's flight time.
            if (_state != GrappleState.HookInFlight || _stateMachine == null || !_stateMachine.CanStartMovementAction())
            {
                _state = GrappleState.Idle;
                CmdReleaseGrapple();
                return;
            }

            _state = GrappleState.Pulling;
            _stateMachine.BeginAbilityMovement(new GrapplePullDriver(point, HandlePullExited));
        }

        [TargetRpc]
        private void TargetRpcHookMissed(NetworkConnection conn)
        {
            if (_state == GrappleState.HookInFlight)
                _state = GrappleState.Idle;
        }

        /// <summary>
        /// GrapplePullDriver's onExit callback - fires exactly once per pull
        /// activation. A real arrival (Arrived) begins the wall hold instead
        /// of releasing - the hook stays attached/spawned as its visual
        /// anchor for the duration. Every other reason
        /// (Interrupted/TimedOut/Obstructed) releases immediately, exactly
        /// as this method always has.
        /// </summary>
        private void HandlePullExited(GrapplePullExitReason reason)
        {
            if (reason == GrapplePullExitReason.Arrived)
            {
                _state = GrappleState.Holding;
                _stateMachine.BeginAbilityMovement(new GrappleWallHoldDriver(HandleHoldExited));
                return;
            }

            _state = GrappleState.Idle;
            CmdReleaseGrapple();
        }

        /// <summary>
        /// GrappleWallHoldDriver's onExit callback - fires exactly once,
        /// whether the hold ran its full GrappleWallHoldDuration, was cut
        /// short by a Jump press, or was interrupted (death/respawn). Either
        /// way the hook itself is now obsolete and must be despawned.
        /// </summary>
        private void HandleHoldExited()
        {
            _state = GrappleState.Idle;
            CmdReleaseGrapple();
        }

        // ------------------------------------------------------------------
        // Server-side path
        // ------------------------------------------------------------------

        [ServerRpc]
        private void CmdFireGrapple(Vector3 origin, Vector3 direction)
        {
            if (_hookPrefab == null) return;
            if (_lifecycle != null && _lifecycle.IsDead) return;

            // Defense in depth: the owner already gates one-hook-at-a-time via
            // _state, but a modified client could still spam this RPC.
            if (_activeHook != null) return;

            float now = Time.time;
            if (now < _serverNextAllowedFireTime) return;
            _serverNextAllowedFireTime = now + _cooldown * (1f - _serverCooldownGrace);

            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            NetworkObject instance = Instantiate(_hookPrefab.NetworkObject, origin, Quaternion.LookRotation(direction, Vector3.up));
            InstanceFinder.ServerManager.Spawn(instance, base.Owner);

            GrappleHook hook = instance.GetComponent<GrappleHook>();
            if (hook == null)
            {
                instance.Despawn();
                return;
            }

            _activeHook = hook;
            float lifetime = Mathf.Max(0.05f, _maxRange / Mathf.Max(0.01f, _hookSpeed));
            hook.ServerInitialize(base.NetworkObject, transform.root, direction * _hookSpeed, _useGravity, lifetime, _ignoreDamageableEntities, _excludedLayers, ServerOnHookResolved);
        }

        /// <summary>Server-only. GrappleHook's resolution callback - fires exactly once per hook.</summary>
        private void ServerOnHookResolved(bool didHit, Vector3 point, Vector3 normal)
        {
            if (!IsServerInitialized) return;

            if (didHit)
            {
                // Hook stays alive/spawned as the pull's visual anchor -
                // released later via CmdReleaseGrapple (see HandlePullExited).
                TargetRpcHookAttached(base.Owner, point);
            }
            else
            {
                // GrappleHook already despawns itself on a miss (see
                // GrappleHook.ResolveMiss) - nothing left to release.
                _activeHook = null;
                TargetRpcHookMissed(base.Owner);
            }
        }

        [ServerRpc]
        private void CmdReleaseGrapple() => ServerDespawnActiveHook();

        [ServerRpc]
        private void CmdCancelInFlightHook() => ServerDespawnActiveHook();

        private void ServerDespawnActiveHook()
        {
            if (_activeHook == null) return;
            _activeHook.ServerRelease();
            _activeHook = null;
        }
    }
}
