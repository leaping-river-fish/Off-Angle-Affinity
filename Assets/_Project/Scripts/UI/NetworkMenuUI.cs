// =============================================================================
// NetworkMenuUI — UI-only glue for the dev connection menu.
//
// ARCHITECTURE:
//   Deliberately framework-agnostic: this script does NOT import FishNet.
//   All networking work is delegated to NetworkMenuController via three
//   plain C# events. That means you can:
//     - swap the UI for a different look without touching networking, or
//     - delete this file entirely once the real main-menu ships.
//
//   Responsibilities:
//     - Populate the address field with a default value on first show.
//     - Route button clicks to controller methods.
//     - Reflect controller status/connect/disconnect events into the label
//       and the panel's visibility.
//
// PLACEMENT:
//   Attach to the root Canvas GameObject of the menu scene. Assign every
//   serialized field in the inspector — the script tolerates missing refs
//   defensively but cannot function without them.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.UI;
using OffAngle.Networking;

namespace OffAngle.UI
{
    public class NetworkMenuUI : MonoBehaviour
    {
        [Header("Controller")]
        [Tooltip("NetworkMenuController that owns all FishNet calls. Usually lives on the NetworkManager GameObject.")]
        [SerializeField] private NetworkMenuController _controller;

        [Header("Panel Root")]
        [Tooltip("GameObject to hide when a connection succeeds and re-show if it drops. Usually the panel that contains the buttons/input.")]
        [SerializeField] private GameObject _panelRoot;

        [Header("Widgets")]
        [SerializeField] private TMP_InputField _addressField;
        [SerializeField] private Button          _hostButton;
        [SerializeField] private Button          _joinButton;
        [SerializeField] private TMP_Text        _statusText;

        [Header("Defaults")]
        [Tooltip("Written into _addressField at Awake if the field is empty.")]
        [SerializeField] private string _defaultAddress = "127.0.0.1";

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_addressField != null && string.IsNullOrEmpty(_addressField.text))
                _addressField.text = _defaultAddress;

            // AddListener rather than assigning onClick so any UnityEvents
            // authored in the inspector still fire alongside this script.
            if (_hostButton != null) _hostButton.onClick.AddListener(OnHostClicked);
            if (_joinButton != null) _joinButton.onClick.AddListener(OnJoinClicked);
        }

        // OnEnable/OnDisable, not Awake/OnDestroy, so hiding-and-reshowing the
        // panel doesn't leak subscriptions or double-subscribe.
        private void OnEnable()
        {
            if (_controller == null)
                return;

            _controller.StatusChanged += HandleStatusChanged;
            _controller.Connected     += HandleConnected;
            _controller.Disconnected  += HandleDisconnected;
        }

        private void OnDisable()
        {
            if (_controller == null)
                return;

            _controller.StatusChanged -= HandleStatusChanged;
            _controller.Connected     -= HandleConnected;
            _controller.Disconnected  -= HandleDisconnected;
        }

        private void OnDestroy()
        {
            if (_hostButton != null) _hostButton.onClick.RemoveListener(OnHostClicked);
            if (_joinButton != null) _joinButton.onClick.RemoveListener(OnJoinClicked);
        }

        // ------------------------------------------------------------------
        // Button handlers
        // ------------------------------------------------------------------

        private void OnHostClicked()
        {
            if (_controller == null)
            {
                SetStatus("Controller not assigned");
                return;
            }

            _controller.StartHost();
        }

        private void OnJoinClicked()
        {
            if (_controller == null)
            {
                SetStatus("Controller not assigned");
                return;
            }

            string address = _addressField != null ? _addressField.text : _defaultAddress;
            _controller.StartClient(address);
        }

        // ------------------------------------------------------------------
        // Controller callbacks
        // ------------------------------------------------------------------

        private void HandleStatusChanged(string message) => SetStatus(message);

        private void HandleConnected()
        {
            // Hide the menu once we have a live client. The controller keeps
            // running on its own GameObject so the Disconnected event can
            // still fire and re-show the panel later.
            if (_panelRoot != null)
                _panelRoot.SetActive(false);
        }

        private void HandleDisconnected(string reason)
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(true);

            SetStatus(reason);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }
    }
}
