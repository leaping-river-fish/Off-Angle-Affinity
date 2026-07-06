// =============================================================================
// NetworkLauncher — temporary IMGUI panel to start a Host/Client locally.
//
// ARCHITECTURE:
//   This is a developer-only entry point, not a finished UI. It exposes the
//   absolute minimum surface area required to drive FishNet from inside the
//   editor without prefab wiring:
//
//     [Start Host]   → starts server + connects local client (most common)
//     [Start Client] → connects to the IP shown in the text field
//     [Stop]         → tears down whichever side(s) are running
//
//   When you build a real main menu, delete this script and the GameObject
//   it lives on. Nothing else in the project references it.
//
// PLACEMENT:
//   Attach to an empty GameObject named "NetworkLauncher" in the scene that
//   also contains the FishNet NetworkManager.
//
// SCOPE:
//   - No matchmaking, no Relay, no NAT punch-through. Pure direct IP.
//   - No StartServer-only mode; dedicated-server builds bypass this script
//     entirely and call ServerManager.StartConnection() from their own boot.
// =============================================================================

using FishNet;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkLauncher : MonoBehaviour
    {
        [Header("Defaults")]
        [Tooltip("Address used by [Start Client]. Editable at runtime in the IMGUI text field.")]
        [SerializeField] private string _defaultAddress = "127.0.0.1";

        private string _addressInput;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            _addressInput = _defaultAddress;
        }

        // ------------------------------------------------------------------
        // IMGUI panel
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            // FishNet may not be fully initialized for one frame after scene load;
            // bail silently rather than throw a NullReferenceException.
            if (InstanceFinder.NetworkManager == null)
                return;

            const float panelWidth  = 240f;
            const float panelHeight = 180f;
            GUILayout.BeginArea(new Rect(12f, 12f, panelWidth, panelHeight), GUI.skin.box);
            GUILayout.Label("Off-Angle — Network");

            bool serverStarted = InstanceFinder.IsServerStarted;
            bool clientStarted = InstanceFinder.IsClientStarted;

            if (!serverStarted && !clientStarted)
            {
                DrawIdleControls();
            }
            else
            {
                DrawRunningControls(serverStarted, clientStarted);
            }

            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------
        // GUI sections
        // ------------------------------------------------------------------

        private void DrawIdleControls()
        {
            if (GUILayout.Button("Start Host"))
            {
                // Host = server + local client. Start server first so the
                // local client has something to connect to.
                InstanceFinder.ServerManager.StartConnection();
                InstanceFinder.ClientManager.StartConnection(_addressInput);
            }

            GUILayout.Space(4f);
            GUILayout.Label("Server Address:");
            _addressInput = GUILayout.TextField(_addressInput);

            if (GUILayout.Button("Start Client"))
            {
                InstanceFinder.ClientManager.StartConnection(_addressInput);
            }
        }

        private void DrawRunningControls(bool serverStarted, bool clientStarted)
        {
            string mode = (serverStarted && clientStarted) ? "HOST"
                        : serverStarted                    ? "SERVER"
                                                           : "CLIENT";
            GUILayout.Label($"Running as: {mode}");

            if (GUILayout.Button("Stop"))
            {
                // Stop client first so the server sees a clean disconnect
                // before its own shutdown begins.
                if (clientStarted)
                    InstanceFinder.ClientManager.StopConnection();

                if (serverStarted)
                    InstanceFinder.ServerManager.StopConnection(sendDisconnectMessage: true);
            }
        }
    }
}
