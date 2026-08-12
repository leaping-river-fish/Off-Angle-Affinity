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
//   an initial scroll view). LabelFor() is a placeholder formatter -- swap it
//   out once the project has real player display names.
//
// MANUAL SETUP:
//   1. Empty GameObject (e.g. "Lobby State") in the same scene as
//      NetworkManager, saved as part of the scene so FishNet bakes it a
//      scene ID.
//   2. Add a NetworkObject component, then this component.
// =============================================================================

using System;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

namespace OffAngle.Networking
{
    public class LobbyPlayerList : NetworkBehaviour
    {
        public readonly SyncList<int> ConnectionIds = new SyncList<int>();

        /// <summary>Scene singleton, set once the object initializes on every peer.</summary>
        public static LobbyPlayerList Instance { get; private set; }

        /// <summary>Fires on every peer (host and clients alike) once the server calls AnnounceGameStarted.</summary>
        public event Action GameStarted;

        /// <summary>Placeholder label until the project has real player display names.</summary>
        public static string LabelFor(int connectionId) => $"Player {connectionId}";

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
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

        // ------------------------------------------------------------------
        // Game start broadcast — every lobby UI (host's and every joiner's)
        // needs this, not just the host who clicked Start Game, since only
        // the host's button click ever happens locally.
        // ------------------------------------------------------------------

        /// <summary>Server-only. Tells every connected peer's lobby UI the match has started.</summary>
        public void AnnounceGameStarted()
        {
            if (!IsServerInitialized)
                return;

            RpcGameStarted();
        }

        [ObserversRpc]
        private void RpcGameStarted()
        {
            GameStarted?.Invoke();
        }
    }
}
