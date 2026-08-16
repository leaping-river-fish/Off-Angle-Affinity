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

                // Disabling here is what stops Start() running on the duplicate.
                // Returning alone is not enough: Start() would still subscribe
                // OnRemoteConnectionState a second time and double-spawn players.
                enabled = false;
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
            InstanceFinder.ServerManager.OnServerConnectionState += OnServerConnectionState;
        }

        private void OnDestroy()
        {
            if (InstanceFinder.ServerManager != null)
            {
                InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
                InstanceFinder.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            }

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

        // Everything this spawner tracks is per-session, but the spawner itself
        // is DontDestroyOnLoad. Without this reset the next match inherits the
        // last one's state: _hasGameStarted in particular would make every
        // joining player spawn immediately as join-in-progress instead of being
        // queued for SpawnAll().
        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;

            _hasGameStarted = false;
            _pendingConnections.Clear();

            // Point at Transforms in the now-unloaded Game scene.
            _spawnPoints = null;
        }

        private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            // Defensive — this event only fires when we ARE the server, but
            // guard explicitly so a future refactor cannot misuse it.
            if (!InstanceFinder.IsServerStarted)
                return;

            if (args.ConnectionState == RemoteConnectionState.Started)
            {
                if (_hasGameStarted)
                    SpawnOnceSceneLoaded(conn);
                else if (!_pendingConnections.Contains(conn))
                    _pendingConnections.Add(conn);
            }
            else if (args.ConnectionState == RemoteConnectionState.Stopped)
            {
                _pendingConnections.Remove(conn);
            }
        }

        /// <summary>
        /// Join-in-progress spawn: waits for this connection's own Game scene
        /// load to be confirmed before spawning a player for it.
        /// RemoteConnectionState.Started fires the instant the raw transport
        /// connection is accepted - well before authentication or scene load
        /// - so spawning immediately here handed this brand-new connection
        /// observer visibility of already-spawned scene objects (e.g. Dummy
        /// targets in the Game scene) before its own client had registered
        /// that scene's NetworkObjects locally, producing FishNet's "SceneId
        /// not found in SceneObjects" error. SpawnAll() already has an
        /// equivalent guard for match-start players via GameFlowController
        /// waiting on the SERVER's own scene load; this mirrors that for a
        /// single late-joining CLIENT's own scene load.
        /// </summary>
        private void SpawnOnceSceneLoaded(NetworkConnection conn)
        {
            if (conn.LoadedStartScenes(true))
            {
                SpawnFor(conn, null);
                return;
            }

            conn.OnLoadedStartScenes += HandleConnectionLoadedStartScenes;

            void HandleConnectionLoadedStartScenes(NetworkConnection loadedConn, bool asServer)
            {
                if (!asServer || loadedConn != conn) return;

                conn.OnLoadedStartScenes -= HandleConnectionLoadedStartScenes;

                // Connection may have disconnected while its scene load was
                // still pending - do not spawn a player for a dead connection.
                if (conn.IsActive)
                    SpawnFor(conn, null);
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
