// =============================================================================
// AffinitySelectionService — the server's authoritative record of what every
// connection picked, and who is ready to play.
//
// WHY IT IS SEPARATE FROM THE COORDINATOR:
//   AffinitySelectCoordinator is a SCENE NetworkObject and dies with the
//   AffinitySelect scene, which is unloaded by ReplaceOption.All the instant
//   the match starts. The answers it collected must outlive it - PlayerAffinity
//   reads them when players spawn, several scene loads later. So the network
//   surface lives on the coordinator and the STATE lives here, on the
//   persistent NetworkManager GameObject.
//
// WHY IT IS NOT A NetworkBehaviour:
//   Nothing here is replicated. Clients never read this; they read the
//   SyncVar/SyncList the coordinator exposes and, once spawned, their own
//   PlayerAffinity. Same reasoning PlayerSpawner documents for being a plain
//   MonoBehaviour.
//
// KEYED BY ClientId:
//   Not by NetworkConnection reference, matching LobbyPlayerList. A connection
//   object outliving its usefulness in a dictionary is a leak; an int is not.
//
// PER-SESSION RESET:
//   Everything here is per-match but this object is DontDestroyOnLoad. Without
//   the reset on server stop, match 2 would inherit match 1's picks and its
//   ready flags - and every player would spawn instantly with a stale build.
//   Same trap PlayerSpawner and GameFlowController both guard against.
//
// MANUAL SETUP:
//   Add to the persistent NetworkManager GameObject in the Bootstrap scene,
//   alongside GameFlowController / PlayerSpawner / LocalAffinitySelection.
// =============================================================================

using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Transporting;
using OffAngle.Affinities;
using UnityEngine;

namespace OffAngle.Networking
{
    public class AffinitySelectionService : MonoBehaviour
    {
        /// <summary>
        /// Persistent singleton, same convention as PlayerSpawner.Instance.
        /// Consumers must null-check: the Game scene can be entered directly when
        /// testing, in which case nobody ever picked anything.
        /// </summary>
        public static AffinitySelectionService Instance { get; private set; }

        private readonly Dictionary<int, AffinityLoadout> _loadouts = new Dictionary<int, AffinityLoadout>();
        private readonly HashSet<int> _ready = new HashSet<int>();

        /// <summary>
        /// Server-only. Raised whenever a connection becomes ready or stops being
        /// ready. PlayerSpawner subscribes so a player who finishes picking AFTER
        /// the match started still gets spawned.
        /// </summary>
        public event Action<NetworkConnection> ServerReadyStateChanged;

        /// <summary>How many connections have submitted a selection.</summary>
        public int ReadyCount => _ready.Count;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(AffinitySelectionService)}] Duplicate instance detected; keeping the first on '{Instance.name}'.", this);
                enabled = false;
                return;
            }

            Instance = this;
        }

        // Start rather than OnEnable, so every Awake (and therefore the
        // NetworkManager singleton) has already run - same reasoning PlayerSpawner
        // documents.
        private void Start()
        {
            if (InstanceFinder.ServerManager == null) return;

            InstanceFinder.ServerManager.OnRemoteConnectionState += HandleRemoteConnectionState;
            InstanceFinder.ServerManager.OnServerConnectionState += HandleServerConnectionState;
        }

        private void OnDestroy()
        {
            if (InstanceFinder.ServerManager != null)
            {
                InstanceFinder.ServerManager.OnRemoteConnectionState -= HandleRemoteConnectionState;
                InstanceFinder.ServerManager.OnServerConnectionState -= HandleServerConnectionState;
            }

            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------------
        // Server API
        // ------------------------------------------------------------------

        /// <summary>
        /// Records an ALREADY-SANITIZED loadout for a connection and marks it ready.
        /// Submitting is what makes a connection ready - there is no separate ready
        /// step. Callers must have run AffinityLoadoutRules.Sanitize first; this
        /// stores what it is given.
        /// </summary>
        public void ServerSetLoadout(NetworkConnection conn, AffinityLoadout sanitized)
        {
            if (conn == null || sanitized == null) return;

            _loadouts[conn.ClientId] = sanitized;
            _ready.Add(conn.ClientId);

            // Raised even on a re-submission: the loadout changed, and a listener
            // that already spawned this player simply finds nothing to do.
            ServerReadyStateChanged?.Invoke(conn);
        }

        /// <summary>Marks a connection not-ready, keeping whatever it had picked.</summary>
        public void ServerClearReady(NetworkConnection conn)
        {
            if (conn == null) return;
            if (!_ready.Remove(conn.ClientId)) return;

            ServerReadyStateChanged?.Invoke(conn);
        }

        /// <summary>True once this connection has submitted a selection.</summary>
        public bool IsReady(NetworkConnection conn) => conn != null && _ready.Contains(conn.ClientId);

        /// <summary>
        /// The stored loadout, or null if this connection never submitted one.
        /// Null is a normal answer - PlayerAffinity turns it into the default build
        /// via Sanitize.
        /// </summary>
        public AffinityLoadout GetLoadoutFor(NetworkConnection conn)
        {
            if (conn == null) return null;
            return _loadouts.TryGetValue(conn.ClientId, out AffinityLoadout loadout) ? loadout : null;
        }

        /// <summary>True if every currently-connected client has submitted. False when nobody is connected.</summary>
        public bool AreAllConnectedReady()
        {
            if (InstanceFinder.ServerManager == null) return false;

            int connected = 0;

            foreach (NetworkConnection conn in InstanceFinder.ServerManager.Clients.Values)
            {
                if (conn == null || !conn.IsActive) continue;

                connected++;
                if (!_ready.Contains(conn.ClientId)) return false;
            }

            return connected > 0;
        }

        /// <summary>Every connected client that has not submitted. Used to auto-fill defaults when the countdown expires.</summary>
        public void CollectUnready(List<NetworkConnection> into)
        {
            into.Clear();
            if (InstanceFinder.ServerManager == null) return;

            foreach (NetworkConnection conn in InstanceFinder.ServerManager.Clients.Values)
            {
                if (conn == null || !conn.IsActive) continue;
                if (_ready.Contains(conn.ClientId)) continue;

                into.Add(conn);
            }
        }

        // ------------------------------------------------------------------
        // Session bookkeeping
        // ------------------------------------------------------------------

        private void HandleRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;
            if (conn == null) return;

            _loadouts.Remove(conn.ClientId);
            _ready.Remove(conn.ClientId);
        }

        private void HandleServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            _loadouts.Clear();
            _ready.Clear();
        }
    }
}
