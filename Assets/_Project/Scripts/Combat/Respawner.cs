// =============================================================================
// Respawner — server-side death → wait → reset controller.
//
// Attach next to a Health component. Two modes controlled by inspector flags:
//   - _teleportOnRespawn = false: dummies. Health resets in place after delay.
//   - _teleportOnRespawn = true:  players. Server picks a spawn point from
//     PlayerSpawner, sends a TargetRpc to the owning client telling it to
//     warp locally (client-authoritative NetworkTransform propagates the new
//     position to everyone else).
//
// Only ONE respawn coroutine can run at a time; further OnServerDied signals
// during the wait are ignored.
// =============================================================================

using System.Collections;
using FishNet.Connection;
using FishNet.Object;
using OffAngle.Networking;
using UnityEngine;

namespace OffAngle.Combat
{
    [RequireComponent(typeof(Health))]
    public class Respawner : NetworkBehaviour
    {
        [Header("Timing")]
        [SerializeField, Min(0f)] private float _respawnDelay = 3f;

        [Header("Behaviour")]
        [Tooltip("Players: on. Dummies: off. When on, the server picks a spawn point and warps the owner via TargetRpc.")]
        [SerializeField] private bool _teleportOnRespawn = false;

        [SerializeField] private bool _restoreFullHealth = true;

        [Tooltip("Optional - leave empty for entities with no weapon (e.g. dummies). Resets ammo on respawn.")]
        [SerializeField] private PlayerWeaponController _weaponController;
        [SerializeField] private bool _restoreFullAmmo = true;

        [Tooltip("Optional - leave empty for entities with no shield (e.g. dummies). Restores shield to full on respawn.")]
        [SerializeField] private Shield _shield;

        private Health _health;
        private Coroutine _respawnRoutine;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            _health = GetComponent<Health>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _health.OnServerDied += HandleServerDied;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            _health.OnServerDied -= HandleServerDied;

            if (_respawnRoutine != null)
            {
                StopCoroutine(_respawnRoutine);
                _respawnRoutine = null;
            }
        }

        // ------------------------------------------------------------------
        // Death → respawn
        // ------------------------------------------------------------------

        private void HandleServerDied(DamageInfo _)
        {
            if (_respawnRoutine != null) return;
            _respawnRoutine = StartCoroutine(RespawnRoutine());
        }

        private IEnumerator RespawnRoutine()
        {
            yield return new WaitForSeconds(_respawnDelay);

            if (_teleportOnRespawn)
            {
                Transform spawn = PlayerSpawner.Instance != null
                    ? PlayerSpawner.Instance.GetSpawnPoint()
                    : null;

                if (spawn != null)
                    TeleportForOwner(spawn.position, spawn.rotation);
                else
                    Debug.LogWarning($"[{nameof(Respawner)}] No spawn point available; skipping teleport for '{name}'.", this);
            }

            if (_restoreFullHealth)
                _health.ResetHealth();

            if (_shield != null)
                _shield.ResetShield();

            if (_restoreFullAmmo && _weaponController != null)
                _weaponController.ServerResetAmmo();

            _respawnRoutine = null;
        }

        // ------------------------------------------------------------------
        // Teleport — respects client-authoritative NetworkTransform
        // ------------------------------------------------------------------

        private void TeleportForOwner(Vector3 position, Quaternion rotation)
        {
            NetworkConnection owner = base.Owner;

            // No owner (e.g. scene-placed dummy that never had a client owner) — set server transform directly.
            if (owner == null || !owner.IsValid)
            {
                transform.SetPositionAndRotation(position, rotation);
                return;
            }

            RpcTeleport(owner, position, rotation);
        }

        [TargetRpc]
        private void RpcTeleport(NetworkConnection conn, Vector3 position, Quaternion rotation)
        {
            // CharacterController toggle: internal collision resolution otherwise fights
            // the immediate teleport when the destination overlaps other colliders.
            var cc = GetComponent<CharacterController>();
            bool wasEnabled = cc != null && cc.enabled;
            if (wasEnabled) cc.enabled = false;

            transform.SetPositionAndRotation(position, rotation);

            if (wasEnabled) cc.enabled = true;
        }
    }
}
