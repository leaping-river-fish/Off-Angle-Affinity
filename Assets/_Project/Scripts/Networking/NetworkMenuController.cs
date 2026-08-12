// =============================================================================
// NetworkMenuController — networking-side driver for the dev connection menu.
//
// ARCHITECTURE:
//   Owns every FishNet call the dev menu needs (StartHost / StartClient) and
//   translates FishNet's low-level connection-state callbacks into three
//   plain C# events the UI can listen to without ever importing FishNet:
//
//     StatusChanged(string) → free-form message for the status label.
//     Connected             → local client successfully reached the server.
//     Disconnected(reason)  → local client stopped (failed attempt OR drop).
//
//   Keeping this split means NetworkMenuUI (or any future replacement UI) can
//   be swapped, deleted, or reskinned without touching FishNet code — and this
//   controller can be reused headlessly (e.g. from an integration test) with
//   no UI present.
//
// PLACEMENT:
//   Attach to the same GameObject that hosts the FishNet NetworkManager so its
//   lifetime matches networking's. The Canvas can be hidden or destroyed
//   without affecting this controller.
// =============================================================================

using System;
using System.Net;
using FishNet;
using FishNet.Transporting;
using OffAngle.Networking.JoinCode;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkMenuController : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Customizable Status Messages
        // ------------------------------------------------------------------

        [Header("Status Messages")]
        [Tooltip("Status message shown on startup before any connection attempts.")]
        [SerializeField] private string _idleMessage = "Idle";

        [Tooltip("Status message shown when attempting to connect.")]
        [SerializeField] private string _connectingMessage = "Connecting...";

        [Tooltip("Status message shown when successfully connected.")]
        [SerializeField] private string _connectedMessage = "Connected";

        [Tooltip("Status message shown when disconnecting.")]
        [SerializeField] private string _disconnectingMessage = "Disconnecting...";

        [Tooltip("Status message shown when disconnected after being connected.")]
        [SerializeField] private string _disconnectedMessage = "Disconnected";

        [Tooltip("Status message shown when a connection attempt fails.")]
        [SerializeField] private string _connectionFailedMessage = "Connection failed";

        [Tooltip("Status message shown when stopped without connecting.")]
        [SerializeField] private string _stoppedMessage = "Stopped";

        [Tooltip("Status message shown when starting host mode.")]
        [SerializeField] private string _startingHostMessage = "Starting host...";

        [Tooltip("Status message shown when already running.")]
        [SerializeField] private string _alreadyRunningMessage = "Already running";

        [Tooltip("Status message shown when already connected.")]
        [SerializeField] private string _alreadyConnectedMessage = "Already connected";

        [Tooltip("Status message shown when NetworkManager is not ready.")]
        [SerializeField] private string _notReadyMessage = "NetworkManager not ready";

        [Tooltip("Status message shown when no server address is provided.")]
        [SerializeField] private string _noAddressMessage = "Enter a server address";

        [Tooltip("Status message shown when a join code could not be generated for the host.")]
        [SerializeField] private string _joinCodeUnavailableMessage = "Couldn't generate a join code - share your IP instead";

        // ------------------------------------------------------------------
        // Public events
        // ------------------------------------------------------------------

        /// <summary>Fires on every meaningful status change (connect attempt, success, failure, disconnect).</summary>
        public event Action<string> StatusChanged;

        /// <summary>Fires when the local client transitions into the Started state.</summary>
        public event Action Connected;

        /// <summary>Fires when the local client stops, with a short reason string.</summary>
        public event Action<string> Disconnected;

        /// <summary>
        /// Fires after StartHost or StartClient with a join code representing the
        /// current session's address/port, or an empty string if one couldn't be
        /// derived (e.g. connected via hostname rather than a bare IP). Intended
        /// for a lobby UI to display so any peer can invite further friends.
        /// </summary>
        public event Action<string> SessionCodeReady;

        // ------------------------------------------------------------------
        // Join code provider
        // ------------------------------------------------------------------

        // Swap this implementation for a relay/rendezvous-backed provider
        // when internet play (different networks) is added later; neither
        // StartHost nor StartClient below need to change.
        private readonly IJoinCodeProvider _joinCodeProvider = new LanJoinCodeProvider();

        // ------------------------------------------------------------------
        // Internal state tracking
        // ------------------------------------------------------------------

        // Previous FishNet client state, used to distinguish "failed to
        // connect" (Starting → Stopped) from "disconnected after connecting"
        // (Started → Stopping → Stopped).
        private LocalConnectionState _lastClientState = LocalConnectionState.Stopped;
        private bool _hasReachedStarted;

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        // Start (not Awake): FishNet's NetworkManager initialises in its own
        // Awake, so subscribing here guarantees InstanceFinder is populated
        // regardless of script execution order.
        private void Start()
        {
            if (InstanceFinder.NetworkManager == null)
            {
                Debug.LogError(
                    $"[{nameof(NetworkMenuController)}] No FishNet NetworkManager found in the scene. " +
                    "Add one via Hierarchy > Fish-Networking > NetworkManager before pressing Play.",
                    this);
                enabled = false;
                return;
            }

            // Only the client-side event is required for menu behavior:
            //   - Client goes Starting → Started         → menu hides.
            //   - Client goes Starting → Stopped         → "Connection failed".
            //   - Client goes Started  → Stopping/Stopped → "Disconnected".
            // A server-side bind failure in host mode still surfaces here as a
            // failed client connection (no one is listening on 127.0.0.1).
            InstanceFinder.ClientManager.OnClientConnectionState += HandleClientConnectionState;

            RaiseStatus(_idleMessage);
        }

        private void OnDestroy()
        {
            // Guard: NetworkManager may already have been destroyed if scenes
            // are unloading in a different order.
            if (InstanceFinder.NetworkManager != null && InstanceFinder.ClientManager != null)
                InstanceFinder.ClientManager.OnClientConnectionState -= HandleClientConnectionState;
        }

        // ------------------------------------------------------------------
        // Public API (called by the UI)
        // ------------------------------------------------------------------

        /// <summary>True on the peer that started the server (the host). Lets UI gate host-only actions (e.g. Start Game) without importing FishNet.</summary>
        public bool IsHost => InstanceFinder.IsServerStarted;

        /// <summary>
        /// Host = server + local (loopback) client. The server is started
        /// first so the local client always has something to connect to.
        /// </summary>
        public void StartHost()
        {
            if (InstanceFinder.ServerManager == null || InstanceFinder.ClientManager == null)
            {
                RaiseStatus(_notReadyMessage);
                return;
            }

            if (InstanceFinder.IsServerStarted || InstanceFinder.IsClientStarted)
            {
                RaiseStatus(_alreadyRunningMessage);
                return;
            }

            RaiseStatus(_startingHostMessage);

            // Read the transport's live/configured port (Tugboat) rather than
            // hardcoding it, so a future port change needs no code edits here.
            ushort port = InstanceFinder.NetworkManager.TransportManager.Transport.GetPort();
            if (_joinCodeProvider.TryCreateHostCode(port, out string code, out string errorReason))
            {
                SessionCodeReady?.Invoke(code);
            }
            else
            {
                SessionCodeReady?.Invoke(string.Empty);
                RaiseStatus(_joinCodeUnavailableMessage);
            }

            // ServerManager.StartConnection() uses the port from the transport
            // component (Tugboat) on the same GameObject. It returns false on
            // e.g. a port bind failure; we don't branch on it here because the
            // ClientManager below will emit Starting → Stopped in that case,
            // which the shared state handler already reports as a failure.
            InstanceFinder.ServerManager.StartConnection();
            InstanceFinder.ClientManager.StartConnection("127.0.0.1");
        }

        /// <summary>
        /// Starts a client-only connection using the given join code or raw
        /// address. Text that decodes as a valid join code connects to the
        /// address/port it encodes; anything else falls back to being treated
        /// as a raw address, so typing a plain IP still works.
        /// </summary>
        public void StartClient(string address)
        {
            if (InstanceFinder.ClientManager == null)
            {
                RaiseStatus(_notReadyMessage);
                return;
            }

            if (InstanceFinder.IsClientStarted)
            {
                RaiseStatus(_alreadyConnectedMessage);
                return;
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                RaiseStatus(_noAddressMessage);
                return;
            }

            string trimmed = address.Trim();
            RaiseStatus($"Connecting to {trimmed}...");

            // StartConnection returns true when the request was accepted, NOT
            // when the connection succeeded. The real result lands later in
            // HandleClientConnectionState.
            _joinCodeProvider.ResolveCode(trimmed, resolution =>
            {
                string connectAddress;
                ushort connectPort;

                if (resolution.Success)
                {
                    connectAddress = resolution.Address;
                    connectPort = resolution.Port;
                    InstanceFinder.ClientManager.StartConnection(connectAddress, connectPort);
                }
                else
                {
                    connectAddress = trimmed;
                    connectPort = InstanceFinder.NetworkManager.TransportManager.Transport.GetPort();
                    InstanceFinder.ClientManager.StartConnection(trimmed);
                }

                RaiseSessionCodeFor(connectAddress, connectPort);
            });
        }

        // Lets a joining client display the same short code back to the lobby
        // (e.g. to invite another friend), by re-encoding whatever address/port
        // it actually used to connect. Silently no-ops if the address isn't a
        // bare IPv4 (e.g. a hostname like "localhost").
        private void RaiseSessionCodeFor(string address, ushort port)
        {
            if (IPAddress.TryParse(address, out IPAddress ip) && LanJoinCodec.TryEncode(ip, port, out string code))
                SessionCodeReady?.Invoke(code);
            else
                SessionCodeReady?.Invoke(string.Empty);
        }

        // ------------------------------------------------------------------
        // FishNet callbacks
        // ------------------------------------------------------------------

        private void HandleClientConnectionState(ClientConnectionStateArgs args)
        {
            LocalConnectionState previous = _lastClientState;
            _lastClientState = args.ConnectionState;

            switch (args.ConnectionState)
            {
                case LocalConnectionState.Starting:
                    RaiseStatus(_connectingMessage);
                    break;

                case LocalConnectionState.Started:
                    _hasReachedStarted = true;
                    RaiseStatus(_connectedMessage);
                    Connected?.Invoke();
                    break;

                case LocalConnectionState.Stopping:
                    RaiseStatus(_disconnectingMessage);
                    break;

                case LocalConnectionState.Stopped:
                    // Classify why we stopped:
                    //   - We reached Started at some point → clean/unclean drop.
                    //   - We only reached Starting → the transport rejected the
                    //     attempt (bad IP, unreachable host, port bind failure
                    //     in host mode, etc.).
                    string reason = _hasReachedStarted
                        ? _disconnectedMessage
                        : previous == LocalConnectionState.Starting
                            ? _connectionFailedMessage
                            : _stoppedMessage;

                    _hasReachedStarted = false;
                    RaiseStatus(reason);
                    Disconnected?.Invoke(reason);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void RaiseStatus(string message) => StatusChanged?.Invoke(message);
    }
}
