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
using FishNet;
using FishNet.Transporting;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkMenuController : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Public events
        // ------------------------------------------------------------------

        /// <summary>Fires on every meaningful status change (connect attempt, success, failure, disconnect).</summary>
        public event Action<string> StatusChanged;

        /// <summary>Fires when the local client transitions into the Started state.</summary>
        public event Action Connected;

        /// <summary>Fires when the local client stops, with a short reason string.</summary>
        public event Action<string> Disconnected;

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

            RaiseStatus("Idle");
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

        /// <summary>
        /// Host = server + local (loopback) client. The server is started
        /// first so the local client always has something to connect to.
        /// </summary>
        public void StartHost()
        {
            if (InstanceFinder.ServerManager == null || InstanceFinder.ClientManager == null)
            {
                RaiseStatus("NetworkManager not ready");
                return;
            }

            if (InstanceFinder.IsServerStarted || InstanceFinder.IsClientStarted)
            {
                RaiseStatus("Already running");
                return;
            }

            RaiseStatus("Starting host...");

            // ServerManager.StartConnection() uses the port from the transport
            // component (Tugboat) on the same GameObject. It returns false on
            // e.g. a port bind failure; we don't branch on it here because the
            // ClientManager below will emit Starting → Stopped in that case,
            // which the shared state handler already reports as a failure.
            InstanceFinder.ServerManager.StartConnection();
            InstanceFinder.ClientManager.StartConnection("127.0.0.1");
        }

        /// <summary>
        /// Starts a client-only connection to the given server address.
        /// The address is trimmed but not otherwise validated; Tugboat will
        /// resolve hostnames and surface any DNS/parse failures via the
        /// connection-state callback.
        /// </summary>
        public void StartClient(string address)
        {
            if (InstanceFinder.ClientManager == null)
            {
                RaiseStatus("NetworkManager not ready");
                return;
            }

            if (InstanceFinder.IsClientStarted)
            {
                RaiseStatus("Already connected");
                return;
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                RaiseStatus("Enter a server address");
                return;
            }

            string trimmed = address.Trim();
            RaiseStatus($"Connecting to {trimmed}...");

            // StartConnection returns true when the request was accepted, NOT
            // when the connection succeeded. The real result lands later in
            // HandleClientConnectionState.
            InstanceFinder.ClientManager.StartConnection(trimmed);
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
                    RaiseStatus("Connecting...");
                    break;

                case LocalConnectionState.Started:
                    _hasReachedStarted = true;
                    RaiseStatus("Connected");
                    Connected?.Invoke();
                    break;

                case LocalConnectionState.Stopping:
                    RaiseStatus("Disconnecting...");
                    break;

                case LocalConnectionState.Stopped:
                    // Classify why we stopped:
                    //   - We reached Started at some point → clean/unclean drop.
                    //   - We only reached Starting → the transport rejected the
                    //     attempt (bad IP, unreachable host, port bind failure
                    //     in host mode, etc.).
                    string reason = _hasReachedStarted
                        ? "Disconnected"
                        : previous == LocalConnectionState.Starting
                            ? "Connection failed"
                            : "Stopped";

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
