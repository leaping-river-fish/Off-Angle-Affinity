// =============================================================================
// PlayerLifecycleController — single source of truth for Alive/Dead and the
// ONLY component that flips other systems off/on because of it.
//
// ARCHITECTURE:
//   Mirrors NetworkPlayerController's role for ownership: that component is
//   the one place that reacts to IsOwner; this is the one place that reacts
//   to death. No other gameplay script gets an "if (IsDead) return;" check -
//   this class disables MovementStateMachine/CharacterController directly,
//   locks weapons through the seam Gun.CanFire() already exposes, and raises
//   events for ragdoll/camera/UI to react to. See the implementation plan
//   ("Player Death State System") for the full rationale.
//
// AUTHORITY:
//   The server owns PlayerLifeState (SyncVar, same idiom as Health/Shield).
//   Death is detected by subscribing to Health.OnServerDied directly - the
//   same event Respawner already subscribes to independently for its own
//   (unrelated) job of timing/resetting stats. Respawn is signalled by
//   Respawner calling ServerSetAlive() as the last step of its coroutine,
//   once health/shield/ammo are already restored.
//
// BROADCAST SHAPE:
//   RpcOnDied/RpcOnRespawned are ObserversRpc - every peer needs to react
//   (the ragdoll must be visible to everyone), but only the owner locks
//   input/state machine, swaps cameras, and raises the UI-facing events. That
//   split happens inside each RPC handler via an IsOwner check, not via a
//   second network message.
// =============================================================================

using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Movement;
using OffAngle.Networking;
using UnityEngine;

namespace OffAngle.Combat
{
    public class PlayerLifecycleController : NetworkBehaviour
    {
        [Header("Combat references")]
        [Tooltip("Leave null to auto-resolve via GetComponent on this GameObject.")]
        [SerializeField] private Health _health;
        [Tooltip("Leave null to auto-resolve via GetComponent on this GameObject. Read for RespawnDelay and driven by via ServerSetAlive().")]
        [SerializeField] private Respawner _respawner;
        [Tooltip("Optional - leave null for entities with no shield (e.g. dummies). Leave null to auto-resolve via GetComponent. Regen is locked on death and unlocked on respawn so it cannot creep up while dead.")]
        [SerializeField] private Shield _shield;

        [Header("Gameplay locks (owner-only)")]
        [SerializeField] private MovementStateMachine _stateMachine;
        [SerializeField] private CharacterController _characterController;
        [SerializeField] private PlayerWeaponController _weaponController;

        [Header("Corpse / visibility (all peers)")]
        [SerializeField] private PlayerRagdoll _ragdoll;
        [SerializeField] private PlayerVisibility _playerVisibility;

        [Header("Camera swap (owner-only)")]
        [SerializeField] private GameObject _firstPersonCameraRoot;
        [SerializeField] private GameObject _deathCameraRoot;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        // Default value is PlayerLifeState.Alive (enum value 0) - matches the
        // existing SyncVar<T>() no-arg convention used by Health/Shield.
        private readonly SyncVar<PlayerLifeState> _lifeState = new SyncVar<PlayerLifeState>();

        // ------------------------------------------------------------------
        // Public read state
        // ------------------------------------------------------------------

        public PlayerLifeState LifeState => _lifeState.Value;
        public bool IsDead => _lifeState.Value == PlayerLifeState.Dead;
        public bool IsAlive => _lifeState.Value == PlayerLifeState.Alive;

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------

        /// <summary>Owner-only. Fires locally when this player's death RPC arrives. Death UI subscribes here.</summary>
        public event Action<DeathInfo> OnLocalDied;

        /// <summary>Owner-only. Fires locally when this player's respawn RPC arrives. Death UI subscribes here.</summary>
        public event Action OnLocalRespawned;

        /// <summary>
        /// Fires on every peer whenever ANY player dies (mirrors Health.DamageFeedback's
        /// static-broadcast convention). Future kill feed / spectator systems subscribe
        /// here without requiring changes to this class.
        /// </summary>
        public static event Action<DeathInfo> AnyPlayerDied;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_health == null) _health = GetComponent<Health>();
            if (_respawner == null) _respawner = GetComponent<Respawner>();
            if (_shield == null) _shield = GetComponent<Shield>();
            if (_characterController == null) _characterController = GetComponent<CharacterController>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (_health != null)
                _health.OnServerDied += HandleServerDied;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (_health != null)
                _health.OnServerDied -= HandleServerDied;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // SyncVar.OnChange only fires on future writes, not the initial
            // synced value (same caveat Health/Shield document) - a client
            // that connects while this player is already dead needs to be
            // seeded directly from the current value.
            if (_lifeState.Value == PlayerLifeState.Dead)
                ApplyDeadVisualsOnly();
        }

        // ------------------------------------------------------------------
        // Server: death
        // ------------------------------------------------------------------

        private void HandleServerDied(DamageInfo info)
        {
            if (!IsServerInitialized) return;

            _lifeState.Value = PlayerLifeState.Dead;
            _shield?.SetRegenLocked(true);

            string weaponLabel = info.Weapon != null ? info.Weapon.name : "Unknown";
            float respawnDuration = _respawner != null ? _respawner.RespawnDelay : 0f;

            // NOTE: info.Attacker is always populated today (PlayerWeaponController.
            // CmdFire always passes base.NetworkObject) - hitscan is the only damage
            // source. If a future damage source (hazards, self-damage) can leave
            // Attacker null, verify the installed FishNet version handles a null
            // NetworkObject RPC argument cleanly (older builds logged an error for
            // this; fixed upstream in 4.3.8+).
            RpcOnDied(info.Attacker, weaponLabel, respawnDuration);
        }

        // ------------------------------------------------------------------
        // Server: respawn — called by Respawner as the last step of its
        // coroutine, after health/shield/ammo are already restored.
        // ------------------------------------------------------------------

        public void ServerSetAlive()
        {
            if (!IsServerInitialized) return;

            _lifeState.Value = PlayerLifeState.Alive;
            _shield?.SetRegenLocked(false);
            RpcOnRespawned();
        }

        // ------------------------------------------------------------------
        // Client: death reaction (every peer, then owner-only extras)
        // ------------------------------------------------------------------

        [ObserversRpc]
        private void RpcOnDied(NetworkObject attacker, string weaponLabel, float respawnDuration)
        {
            var info = new DeathInfo(base.NetworkObject, attacker, weaponLabel, respawnDuration);

            // Cosmetic — every peer sees the corpse fall, not just the owner.
            _ragdoll?.EnterRagdoll();

            // Global broadcast for future systems (kill feed, spectator, etc.).
            AnyPlayerDied?.Invoke(info);

            if (!base.IsOwner) return;

            SetOwnerGameplayLocked(true);
            SwapToDeathCamera();
            _playerVisibility?.ForceVisibleToOwner(true);

            OnLocalDied?.Invoke(info);
        }

        // ------------------------------------------------------------------
        // Client: respawn reaction (every peer, then owner-only extras)
        // ------------------------------------------------------------------

        [ObserversRpc]
        private void RpcOnRespawned()
        {
            _ragdoll?.ExitRagdoll();

            if (!base.IsOwner) return;

            // Clear stale pending input BEFORE re-enabling the state machine -
            // see MovementStateMachine.ResetTransientInput for why this matters.
            _stateMachine?.ResetTransientInput();

            SetOwnerGameplayLocked(false);
            SwapToFirstPersonCamera();
            _playerVisibility?.ForceVisibleToOwner(false);

            OnLocalRespawned?.Invoke();
        }

        // ------------------------------------------------------------------
        // Late-join / reconnect seeding (ragdoll + owner lock only — no
        // attacker/weapon/duration is available for a transition that already
        // happened before this peer connected, so no UI event is raised).
        // ------------------------------------------------------------------

        private void ApplyDeadVisualsOnly()
        {
            _ragdoll?.EnterRagdoll();

            if (!base.IsOwner) return;

            SetOwnerGameplayLocked(true);
            SwapToDeathCamera();
            _playerVisibility?.ForceVisibleToOwner(true);
        }

        // ------------------------------------------------------------------
        // Owner-only gameplay lock — the ONE place these toggles happen.
        // ------------------------------------------------------------------

        private void SetOwnerGameplayLocked(bool locked)
        {
            if (_stateMachine != null) _stateMachine.enabled = !locked;
            if (_characterController != null) _characterController.enabled = !locked;
            _weaponController?.SetFireLocked(locked);
        }

        // ------------------------------------------------------------------
        // Owner-only camera swap. Order matters: disable the outgoing camera
        // BEFORE enabling the incoming one, so the incoming camera's OnEnable
        // (which re-locks the cursor) always runs last regardless of which
        // direction the swap is going. See PlayerCameraController/
        // DeathCameraController OnEnable/OnDisable for the cursor lock side effect.
        // ------------------------------------------------------------------

        private void SwapToDeathCamera()
        {
            if (_firstPersonCameraRoot != null) _firstPersonCameraRoot.SetActive(false);
            if (_deathCameraRoot != null) _deathCameraRoot.SetActive(true);
        }

        private void SwapToFirstPersonCamera()
        {
            if (_deathCameraRoot != null) _deathCameraRoot.SetActive(false);
            if (_firstPersonCameraRoot != null) _firstPersonCameraRoot.SetActive(true);
        }

        // ------------------------------------------------------------------
        // Editor sanity check
        // ------------------------------------------------------------------

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying) return;

            if (_deathCameraRoot != null && _deathCameraRoot.activeSelf)
                Debug.LogWarning($"[{nameof(PlayerLifecycleController)}] Death Camera on '{name}' should be INACTIVE by default (same convention as Camera Pivot).", this);
        }
#endif
    }
}
