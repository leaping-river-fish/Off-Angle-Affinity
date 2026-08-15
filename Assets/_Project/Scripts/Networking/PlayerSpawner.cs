// =============================================================================
// PlayerSpawner — server-only spawner for the player prefab.
//
// ARCHITECTURE:
//   Subscribes to ServerManager.OnRemoteConnectionState. Before the match
//   starts, a connection reaching Started (including the host's own loopback
//   client) is only queued -- nothing spawns yet, so players stay lobby-
//   locked. When GameFlowController's Game scene load completes, SpawnAll()
//   fires once: every queued connection gets a shuffled, non-repeating spawn
//   point (first-mode deathmatch wants guaranteed-unique starts). Connections
//   that arrive after the match has started (join-in-progress) spawn
//   immediately at a random point, same as the original always-spawn-on-
//   connect behavior.
//
//   Despawn-on-disconnect is handled automatically by FishNet because the
//   prefab has a NetworkObject and the spawn passes a connection. A
//   connection that disconnects while still queued (pre-start) is simply
//   dropped from the queue.
//
// WHY THIS LIVES ON A REGULAR MonoBehaviour:
//   The spawner is server-side infrastructure, not a networked entity. Making
//   it a NetworkBehaviour would require its own NetworkObject and spawn
//   dance, which adds nothing. It lives on the persistent NetworkManager
//   GameObject (created in MainMenu, survives every scene transition) rather
//   than in the Game scene, since it must already be tracking connections
//   throughout the Lobby phase before the Game scene ever loads.
//
// MANUAL SETUP:
//   1. Attach this component to the persistent NetworkManager GameObject.
//   2. Drag the player prefab's root NetworkObject into _playerPrefab.
//   3. Spawn points are resolved at runtime via ResolveSpawnPoints() (called
//      by GameFlowController once the Game scene loads) by finding every
//      SpawnPoint component in the scene -- no Inspector wiring needed here,
//      since those Transforms don't exist at edit time in this GameObject's
//      own scene (MainMenu).
// =============================================================================

using System.Collections.Generic;
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

        // Resolved at runtime via ResolveSpawnPoints() once the Game scene loads
        // (see GameFlowController.HandleGameSceneLoaded). Must be a field, not a
        // local variable, because Respawner calls GetSpawnPoint() on every
        // mid-match respawn, not just the initial spawn.
        private SpawnPoint[] _spawnPoints;

        // ------------------------------------------------------------------
        // Persistent singleton — Respawner queries GetSpawnPoint() here.
        // ------------------------------------------------------------------

        /// <summary>
        /// Singleton reference, set in Awake, cleared in OnDestroy. Lives on the
        /// persistent NetworkManager GameObject from MainMenu onward. Consumers
        /// should still null-check, since it only exists once the host/server
        /// has actually started.
        /// </summary>
        public static PlayerSpawner Instance { get; private set; }

        private readonly List<NetworkConnection> _pendingConnections = new List<NetworkConnection>();
        private bool _hasGameStarted;

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
        // Spawn point resolution (called by GameFlowController once the Game
        // scene finishes loading)
        // ------------------------------------------------------------------

        public void ResolveSpawnPoints()
            => _spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);

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
            return _spawnPoints[idx].transform;
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

            if (args.ConnectionState == RemoteConnectionState.Started)
            {
                if (_hasGameStarted)
                    SpawnFor(conn, null);
                else if (!_pendingConnections.Contains(conn))
                    _pendingConnections.Add(conn);
            }
            else if (args.ConnectionState == RemoteConnectionState.Stopped)
            {
                _pendingConnections.Remove(conn);
            }
        }

        // ------------------------------------------------------------------
        // Spawn logic (called by GameFlowController once the Game scene loads)
        // ------------------------------------------------------------------

        public void SpawnAll()
        {
            _hasGameStarted = true;

            List<NetworkConnection> connections = new List<NetworkConnection>(_pendingConnections);
            _pendingConnections.Clear();

            List<Transform> shuffledPoints = null;
            if (_spawnPoints != null && _spawnPoints.Length > 0)
            {
                shuffledPoints = new List<Transform>();
                foreach (SpawnPoint sp in _spawnPoints)
                    shuffledPoints.Add(sp.transform);
                Shuffle(shuffledPoints);
            }

            for (int i = 0; i < connections.Count; i++)
            {
                Transform point = null;
                if (shuffledPoints != null && shuffledPoints.Count > 0)
                {
                    if (i >= shuffledPoints.Count)
                        Debug.LogWarning($"[{nameof(PlayerSpawner)}] {connections.Count} players but only {shuffledPoints.Count} spawn points; some will share a spawn point.");

                    point = shuffledPoints[i % shuffledPoints.Count];
                }

                SpawnFor(connections[i], point);
            }
        }

        private void SpawnFor(NetworkConnection conn, Transform explicitPoint)
        {
            (Vector3 position, Quaternion rotation) = explicitPoint != null
                ? (explicitPoint.position, explicitPoint.rotation)
                : NextSpawnPoint();

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

        private static void Shuffle(List<Transform> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
