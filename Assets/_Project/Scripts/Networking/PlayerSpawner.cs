// =============================================================================
// PlayerSpawner — server-only spawner for the player prefab.
//
// ARCHITECTURE:
//   Subscribes to ServerManager.OnRemoteConnectionState. When any client
//   (including the host's own loopback client) enters the Started state, the
//   spawner picks a random spawn point and calls Spawn(obj, conn),
//   which atomically instantiates the prefab AND assigns the connecting peer
//   as its owner. That ownership assignment is what later lets
//   NetworkPlayerController distinguish IsOwner from a remote view.
//
//   Despawn-on-disconnect is handled automatically by FishNet because the
//   prefab has a NetworkObject and the spawn passes a connection. No manual
//   tracking is needed for the foundation pass; if we add lobby/replay
//   features later, this is where that bookkeeping would live.
//
// WHY THIS LIVES ON A REGULAR MonoBehaviour:
//   The spawner is server-side infrastructure tied to the scene, not a
//   networked entity. Making it a NetworkBehaviour would require its own
//   NetworkObject and spawn dance, which adds nothing.
//
// MANUAL SETUP:
//   1. Empty GameObject named "PlayerSpawner" in the same scene as
//      NetworkManager.
//   2. Attach this component.
//   3. Drag the player prefab's root NetworkObject into _playerPrefab.
//   4. Create 2-4 empty GameObjects positioned where you want spawns, drag
//      them into _spawnPoints. (Leaving the array empty falls back to the
//      world origin — fine for first-light testing.)
// =============================================================================

using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace OffAngle.Networking
{
    public class PlayerSpawner : MonoBehaviour
    {
        [Header("Prefab")]
        [Tooltip("Root NetworkObject of the player prefab. Must be registered in DefaultPrefabObjects (FishNet auto-detects new prefabs containing a NetworkObject).")]
        [SerializeField] private NetworkObject _playerPrefab;

        [Header("Spawn Points")]
        [Tooltip("Empty Transforms placed in the scene. Leave empty to spawn everyone at world origin (useful for first smoke test).")]
        [SerializeField] private Transform[] _spawnPoints;

        // ------------------------------------------------------------------
        // Scene singleton — Respawner queries GetSpawnPoint() here.
        // ------------------------------------------------------------------

        /// <summary>
        /// Scene singleton reference. Set in Awake on every peer (scene object),
        /// cleared in OnDestroy. Consumers should null-check because the spawner
        /// only exists once the gameplay scene is loaded.
        /// </summary>
        public static PlayerSpawner Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(PlayerSpawner)}] Multiple instances found. Keeping the first on '{Instance.name}'.", this);
                return;
            }
            Instance = this;
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        // Start (not OnEnable): by Start time every Awake has run, so the
        // NetworkManager singleton is guaranteed to be initialised even if
        // FishNet's script execution order shifts in future versions.
        private void Start()
        {
            if (InstanceFinder.ServerManager == null)
            {
                Debug.LogError(
                    $"[{nameof(PlayerSpawner)}] No FishNet NetworkManager found in the scene. " +
                    "Add one via Hierarchy > Fish-Networking > NetworkManager before pressing Play.",
                    this);
                enabled = false;
                return;
            }

            if (_playerPrefab == null)
            {
                Debug.LogError($"[{nameof(PlayerSpawner)}] _playerPrefab is not assigned.", this);
                enabled = false;
                return;
            }

            InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        }

        private void OnDestroy()
        {
            if (InstanceFinder.ServerManager != null)
                InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;

            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------------
        // Public spawn point query (used by Respawner)
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns a random configured spawn point, or null if none are set.
        /// Safe to call on any peer.
        /// </summary>
        public Transform GetSpawnPoint()
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0)
                return null;

            int idx = Random.Range(0, _spawnPoints.Length);
            return _spawnPoints[idx];
        }

        // ------------------------------------------------------------------
        // Server callbacks
        // ------------------------------------------------------------------

        private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            // Defensive — this event only fires when we ARE the server, but
            // guard explicitly so a future refactor cannot misuse it.
            if (!InstanceFinder.IsServerStarted)
                return;

            if (args.ConnectionState != RemoteConnectionState.Started)
                return;

            SpawnFor(conn);
        }

        // ------------------------------------------------------------------
        // Spawn logic
        // ------------------------------------------------------------------

        private void SpawnFor(NetworkConnection conn)
        {
            (Vector3 position, Quaternion rotation) = NextSpawnPoint();

            // Instantiating the prefab here creates a normal scene object on
            // the server. Spawn(instance, conn) is what promotes it into a
            // networked object visible to all clients with `conn` as owner.
            NetworkObject instance = Instantiate(_playerPrefab, position, rotation);
            InstanceFinder.ServerManager.Spawn(instance, conn);
        }

        private (Vector3 position, Quaternion rotation) NextSpawnPoint()
        {
            Transform t = GetSpawnPoint();
            if (t == null)
                return (Vector3.zero, Quaternion.identity);

            return (t.position, t.rotation);
        }
    }
}
