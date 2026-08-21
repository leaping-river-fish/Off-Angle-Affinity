// =============================================================================
// PlayerSpawner — server-only spawner for the player prefab.
//
// ARCHITECTURE:
//   Subscribes to ServerManager.OnRemoteConnectionState. Before the match
//   starts, a connection reaching Started (including the host's own loopback
//   client) is only queued -- nothing spawns yet, so players stay lobby-
//   locked. When GameFlowController's Game scene load completes, SpawnAll()
//   fires once: every queued connection gets a shuffled, non-repeating spawn
//   point (first-mode deathmatch wants guaranteed-unique starts). There is no
//   join-in-progress: a connection that arrives once the match has already
//   started is kicked immediately (see OnRemoteConnectionState) rather than
//   spawned with a default build or given its own Affinity picker mid-match -
//   every player who actually plays picked their build before the match began.
//
//   Despawn-on-disconnect is handled automatically by FishNet because the
//   prefab has a NetworkObject and the spawn passes a connection. A
//   connection that disconnects while still queued (pre-start) is simply
//   dropped from the queue.
//
// AFFINITY READINESS GATE:
//   SpawnAll() only spawns connections that have submitted an Affinity
//   selection; anyone who has not stays QUEUED rather than being dropped, and
//   is spawned by ServerSpawnIfReady the moment they submit. That is what makes
//   "players who aren't ready simply aren't spawned yet" work without stalling
//   the match for everyone else.
//
//   When no AffinitySelectionService exists - launching straight into the Game
//   scene while testing - the gate is skipped entirely and behaviour is exactly
//   as it was before: everyone spawns.
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
using FishNet.Managing.Logging;
using FishNet.Managing.Scened;
using FishNet.Managing.Server;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        // Captured alongside _spawnPoints, at the same moment GameFlowController
        // has just forced Game to be the active scene - see SpawnOnceSceneLoaded
        // for why this is needed rather than checking a connection's general
        // "has this connection loaded anything yet" state.
        private Scene _gameScene;

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

            // Both singletons are set in Awake, so by Start this is resolved if the
            // service exists at all.
            if (AffinitySelectionService.Instance != null)
                AffinitySelectionService.Instance.ServerReadyStateChanged += HandleReadyStateChanged;
        }

        private void OnDestroy()
        {
            if (InstanceFinder.ServerManager != null)
            {
                InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
                InstanceFinder.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            }

            if (AffinitySelectionService.Instance != null)
                AffinitySelectionService.Instance.ServerReadyStateChanged -= HandleReadyStateChanged;

            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------------
        // Spawn point resolution (called by GameFlowController once the Game
        // scene finishes loading)
        // ------------------------------------------------------------------

        public void ResolveSpawnPoints()
        {
            _spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);

            // GameFlowController.HandleGameSceneLoaded calls SetActiveScene(Game)
            // immediately before this, so the active scene right now IS Game -
            // capturing it here needs no scene name of our own to hardcode.
            _gameScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
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
            return _spawnPoints[idx].transform;
        }

        // ------------------------------------------------------------------
        // Server callbacks
        // ------------------------------------------------------------------

        // Everything this spawner tracks is per-session, but the spawner itself
        // is DontDestroyOnLoad. Without this reset the next match inherits the
        // last one's state: _hasGameStarted in particular would make every
        // joining player get rejected as if the (previous) match had already
        // started, instead of being queued for the new match's SpawnAll().
        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;

            ClearMatchState();
        }

        // Shared by a full server stop (nobody is connected by the time that
        // runs, so _pendingConnections is correctly left empty) and
        // ServerResetForNewMatch (a return-to-lobby mid-session, where every
        // still-active connection gets re-queued afterward) - the per-match
        // state itself is cleared identically either way.
        private void ClearMatchState()
        {
            _hasGameStarted = false;
            _pendingConnections.Clear();

            // Point at Transforms in the now-unloaded Game scene.
            _spawnPoints = null;
            _gameScene = default;
        }

        /// <summary>
        /// Server-only. Despawns every connection's player object. Called by
        /// GameFlowController.RequestReturnToLobby before the Game scene
        /// unloads - see that method's comment for why this isn't just left
        /// to the unload itself.
        /// </summary>
        public void ServerDespawnAll()
        {
            if (!InstanceFinder.IsServerStarted) return;

            // Snapshot first: despawning mutates the same connection/object
            // bookkeeping this loop would otherwise be iterating live.
            List<NetworkObject> playerObjects = new List<NetworkObject>();
            foreach (NetworkConnection conn in InstanceFinder.ServerManager.Clients.Values)
            {
                if (conn.FirstObject != null)
                    playerObjects.Add(conn.FirstObject);
            }

            foreach (NetworkObject nob in playerObjects)
                InstanceFinder.ServerManager.Despawn(nob);
        }

        /// <summary>
        /// Server-only. Resets this spawner for a new match without a full
        /// server stop - called by GameFlowController.RequestReturnToLobby.
        /// Unlike OnServerConnectionState's reset, every currently-active
        /// connection is re-queued afterward so the next SpawnAll() actually
        /// spawns them instead of iterating an empty queue.
        /// </summary>
        public void ServerResetForNewMatch()
        {
            if (!InstanceFinder.IsServerStarted) return;

            ClearMatchState();

            foreach (NetworkConnection conn in InstanceFinder.ServerManager.Clients.Values)
            {
                if (conn.IsActive)
                    _pendingConnections.Add(conn);
            }
        }

        private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            // Defensive — this event only fires when we ARE the server, but
            // guard explicitly so a future refactor cannot misuse it.
            if (!InstanceFinder.IsServerStarted)
                return;

            if (args.ConnectionState == RemoteConnectionState.Started)
            {
                // No join-in-progress: everyone who actually plays picked an
                // Affinity before the match began, so a connection arriving
                // after Game has already loaded is rejected outright rather
                // than spawned with a default build or made to pick mid-match.
                if (_hasGameStarted)
                {
                    // Kick() never sends KickReason to the client - only this
                    // broadcast, sent first, lets NetworkMenuController show
                    // something more specific than the generic "Disconnected"
                    // it would otherwise be indistinguishable from.
                    // requireAuthenticated: false because this connection has
                    // only just reached Started - it may not have completed
                    // FishNet's own authentication handshake yet.
                    InstanceFinder.ServerManager.Broadcast(conn, new MatchAlreadyStartedBroadcast(), requireAuthenticated: false);

                    conn.Kick(KickReason.Unset, LoggingType.Common,
                        $"[{nameof(PlayerSpawner)}] Rejected connection {conn.ClientId} - the match has already started.");
                    return;
                }

                if (!_pendingConnections.Contains(conn))
                    _pendingConnections.Add(conn);
            }
            else if (args.ConnectionState == RemoteConnectionState.Stopped)
            {
                _pendingConnections.Remove(conn);
            }
        }

        /// <summary>
        /// Waits for this connection's own Game scene load to be confirmed
        /// before spawning a player for it, then uses <paramref name="explicitPoint"/>
        /// if given or a random configured point otherwise.
        /// RemoteConnectionState.Started fires the instant the raw transport
        /// connection is accepted - well before authentication or scene load
        /// - so spawning immediately here handed this brand-new connection
        /// observer visibility of already-spawned scene objects (e.g. Dummy
        /// targets in the Game scene) before its own client had registered
        /// that scene's NetworkObjects locally, producing FishNet's "SceneId
        /// not found in SceneObjects" error - the same race SpawnAll() used to
        /// hit for match-start players, since the server's own Game-scene load
        /// completing (which is all GameFlowController waited on) says nothing
        /// about whether any given CLIENT has finished loading it yet. A
        /// connection whose own load lagged received its spawn message before
        /// it had a scene to put the object in, and FishNet parked it in its
        /// internal MovedObjectsHolder scene instead - a player with no camera
        /// and no working input, forever. Every caller now waits here.
        ///
        /// conn.Scenes, not conn.LoadedStartScenes(true), is the correct check:
        /// LoadedStartScenes is a one-time, permanent latch that flips true the
        /// first time a connection EVER finishes loading its very first scene
        /// (Lobby, for every player here) and never resets - by the time Game
        /// loads, every already-connected player's flag is already true
        /// regardless of whether Game specifically has finished loading for
        /// them, so it does not actually wait for anything at match start.
        /// conn.Scenes is FishNet's own per-scene membership set, only gaining
        /// _gameScene once this specific connection has confirmed loading it.
        /// </summary>
        private void SpawnOnceSceneLoaded(NetworkConnection conn, Transform explicitPoint = null)
        {
            if (conn.Scenes.Contains(_gameScene))
            {
                SpawnFor(conn, explicitPoint);
                return;
            }

            InstanceFinder.SceneManager.OnClientPresenceChangeEnd += HandlePresenceChangeEnd;

            void HandlePresenceChangeEnd(ClientPresenceChangeEventArgs args)
            {
                if (!args.Added || args.Connection != conn || args.Scene != _gameScene) return;

                InstanceFinder.SceneManager.OnClientPresenceChangeEnd -= HandlePresenceChangeEnd;

                // Connection may have disconnected while its scene load was
                // still pending - do not spawn a player for a dead connection.
                if (conn.IsActive)
                    SpawnFor(conn, explicitPoint);
            }
        }

        // ------------------------------------------------------------------
        // Spawn logic (called by GameFlowController once the Game scene loads)
        // ------------------------------------------------------------------

        public void SpawnAll()
        {
            _hasGameStarted = true;

            // Only the ready ones spawn now. The rest STAY queued - dropping them
            // here would leave them permanently unspawnable when they do submit.
            List<NetworkConnection> connections = new List<NetworkConnection>();
            List<NetworkConnection> stillWaiting = new List<NetworkConnection>();

            for (int i = 0; i < _pendingConnections.Count; i++)
            {
                NetworkConnection conn = _pendingConnections[i];
                if (IsSelectionReady(conn))
                    connections.Add(conn);
                else
                    stillWaiting.Add(conn);
            }

            _pendingConnections.Clear();
            _pendingConnections.AddRange(stillWaiting);

            if (stillWaiting.Count > 0)
                Debug.Log($"[{nameof(PlayerSpawner)}] {stillWaiting.Count} player(s) have not submitted an Affinity selection and will spawn when they do.");

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

                // Point assignment happens up front, here, so the shuffled/
                // unique-per-player guarantee survives the wait below even
                // though connections resolve their own scene load at different
                // times. See SpawnOnceSceneLoaded for why this waits at all.
                SpawnOnceSceneLoaded(connections[i], point);
            }
        }

        /// <summary>
        /// Spawns a connection that was left queued by SpawnAll because it had not
        /// picked an Affinity yet. No-op if the match has not started, if the
        /// connection is not queued (already spawned), or if it is still not ready.
        /// </summary>
        public void ServerSpawnIfReady(NetworkConnection conn)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (!_hasGameStarted)
            {
                Debug.Log($"[{nameof(PlayerSpawner)}] ServerSpawnIfReady({conn?.ClientId}) no-op: game hasn't started yet.");
                return;
            }
            if (conn == null || !conn.IsActive)
            {
                Debug.Log($"[{nameof(PlayerSpawner)}] ServerSpawnIfReady no-op: connection null or inactive.");
                return;
            }
            if (!_pendingConnections.Contains(conn))
            {
                Debug.Log($"[{nameof(PlayerSpawner)}] ServerSpawnIfReady({conn.ClientId}) no-op: not in _pendingConnections (already spawned, or never queued).");
                return;
            }
            if (!IsSelectionReady(conn))
            {
                Debug.Log($"[{nameof(PlayerSpawner)}] ServerSpawnIfReady({conn.ClientId}) no-op: not ready yet.");
                return;
            }

            Debug.Log($"[{nameof(PlayerSpawner)}] ServerSpawnIfReady({conn.ClientId}) - spawning now.");
            _pendingConnections.Remove(conn);

            // Reuses the join-in-progress path, which already waits on this
            // connection's own scene load before spawning.
            SpawnOnceSceneLoaded(conn);
        }

        private void HandleReadyStateChanged(NetworkConnection conn) => ServerSpawnIfReady(conn);

        /// <summary>
        /// True when this connection may spawn. With no AffinitySelectionService in
        /// the session the gate is open, preserving the original always-spawn
        /// behaviour for direct-to-Game testing.
        /// </summary>
        private static bool IsSelectionReady(NetworkConnection conn)
        {
            AffinitySelectionService service = AffinitySelectionService.Instance;
            return service == null || service.IsReady(conn);
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

            Debug.Log($"[{nameof(PlayerSpawner)}] SpawnFor({conn.ClientId}) - Instantiate + Spawn called at {position}, active scene at instantiation time was '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'.");
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
