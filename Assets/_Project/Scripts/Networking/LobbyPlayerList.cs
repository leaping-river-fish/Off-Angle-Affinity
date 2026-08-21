// =============================================================================
// LobbyPlayerList — networked backend for a lobby's "who's here" list.
//
// ARCHITECTURE:
//   A scene NetworkObject (not a spawned prefab) so it exists on every peer
//   from scene load and FishNet syncs it automatically once the server starts
//   -- no manual Spawn() call needed, mirroring how PlayerSpawner treats the
//   host's loopback client the same as any remote one.
//
//   ConnectionIds is the source of truth a future lobby UI binds to directly
//   (subscribe to ConnectionIds.OnChange, or read it at OnEnable to populate
//   an initial scroll view). LabelFor() checks PlayerNameRegistry for a real
//   chosen name first, falling back to the "Player #" placeholder for a
//   connection that never submitted one.
//
// MANUAL SETUP:
//   1. Empty GameObject (e.g. "Lobby State") in the same scene as
//      NetworkManager, saved as part of the scene so FishNet bakes it a
//      scene ID.
//   2. Add a NetworkObject component, then this component.
// =============================================================================

using System;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using UnityEngine;

namespace OffAngle.Networking
{
    public class LobbyPlayerList : NetworkBehaviour
    {
        public readonly SyncList<int> ConnectionIds = new SyncList<int>();

        /// <summary>Scene singleton, set once the object initializes on every peer.</summary>
        public static LobbyPlayerList Instance { get; private set; }

        /// <summary>
        /// Fires right after Instance is set. FishNet activates scene
        /// NetworkObjects a beat later than plain scene MonoBehaviours, so a
        /// UI object that awakens/enables in the same scene load can't just
        /// check Instance once -- it needs to hear about it if it's still
        /// null at that point.
        /// </summary>
        public static event Action InstanceReady;

        /// <summary>The player's chosen display name, or a "Player #" placeholder if they never set one.</summary>
        public static string LabelFor(int connectionId) =>
            PlayerNameRegistry.TryGetName(connectionId, out string name) ? name : $"Player {connectionId}";

        private void Awake()
        {
            // Guards only the static pointer, not the whole component: unlike
            // PlayerSpawner/GameFlowController (plain MonoBehaviours where a
            // duplicate would double-subscribe events in their own Start()),
            // this is a FishNet-driven NetworkBehaviour - its real lifecycle
            // runs through OnStartServer/OnStartClient, not Unity's
            // Awake/Start/enabled, so disabling the component here wouldn't be
            // a verified-safe way to stop FishNet's own callbacks and isn't
            // needed for the one thing actually at risk: a second instance's
            // Awake() silently clobbering Instance out from under whichever
            // peer/UI is still reading the first one.
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(LobbyPlayerList)}] Duplicate instance detected; keeping the first.", this);
                return;
            }

            Instance = this;
            InstanceReady?.Invoke();
        }

        private void OnDestroy()
        {
            // Also here, not just OnStopServer. This object lives only in the
            // Lobby scene, and starting a match loads Game with
            // ReplaceOption.All - which unloads Lobby and destroys it. If
            // OnStopServer does not run during that unload, the ServerManager
            // delegate below keeps pointing at a destroyed object for the whole
            // match and every disconnect writes to a despawned SyncList.
            // Unsubscribing twice is harmless; not unsubscribing is not.
            //
            // InstanceFinder rather than the inherited ServerManager property,
            // which is unreliable once despawned.
            if (InstanceFinder.ServerManager != null)
                InstanceFinder.ServerManager.OnRemoteConnectionState -= HandleRemoteConnectionState;

            if (Instance == this)
                Instance = null;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            ServerManager.OnRemoteConnectionState += HandleRemoteConnectionState;

            foreach (NetworkConnection conn in ServerManager.Clients.Values)
            {
                if (!ConnectionIds.Contains(conn.ClientId))
                    ConnectionIds.Add(conn.ClientId);
            }
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            ServerManager.OnRemoteConnectionState -= HandleRemoteConnectionState;
            ConnectionIds.Clear();
        }

        private void HandleRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            // Belt and braces against a stale invocation reaching the SyncList
            // after this object's scene was unloaded. `this == null` is Unity's
            // destroyed-object check, not a real null.
            if (this == null || !base.IsSpawned)
                return;

            switch (args.ConnectionState)
            {
                case RemoteConnectionState.Started:
                    if (!ConnectionIds.Contains(conn.ClientId))
                        ConnectionIds.Add(conn.ClientId);
                    break;

                case RemoteConnectionState.Stopped:
                    ConnectionIds.Remove(conn.ClientId);
                    break;
            }
        }
    }
}
