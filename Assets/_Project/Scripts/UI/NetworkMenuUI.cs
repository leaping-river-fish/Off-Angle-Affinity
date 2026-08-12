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

using System.Net;
using System.Net.Sockets;
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
        [Tooltip("Shown as greyed-out placeholder text in the empty address field -- a format hint only, never submitted as real input.")]
        [SerializeField] private string _defaultAddress = "127.0.0.1";

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            // Clear any initial text so the placeholder shows instead. The
            // placeholder itself is set here (rather than left as a static
            // Inspector value) so this is the one place that decides what it
            // says, keeping it in sync with _defaultAddress.
            if (_addressField != null)
            {
                _addressField.text = "";
                if (_addressField.placeholder is TMP_Text placeholderText)
                    placeholderText.text = _defaultAddress;
            }

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

            // Host and Join share one address field, so whatever is sitting in
            // it may well be a join code from a previous attempt. Only treat it
            // as a host IP override when it is a bare IPv4; anything else is
            // ignored so a leftover code can't break hosting. Blank (the common
            // case) means StartHost auto-detects as before.
            _controller.StartHost(ReadIPv4Override());
        }

        private void OnJoinClicked()
        {
            if (_controller == null)
            {
                SetStatus("Controller not assigned");
                return;
            }

            // Pass the field's text through as-is -- StartClient already shows
            // "Enter a server address" for an empty field. Silently substituting
            // a default here previously meant an empty field connected to
            // 127.0.0.1 without any indication that nothing was actually typed.
            string typed = _addressField != null ? _addressField.text : null;

            // TEMP DIAGNOSTIC -- remove once "Enter a server address" false
            // positives are confirmed fixed. Shows exactly what the field held
            // at the moment Join was clicked.
            Debug.Log($"[NetworkMenuUI] OnJoinClicked: _addressField.text = '{typed}' (null: {_addressField == null}, length: {typed?.Length ?? -1})");

            _controller.StartClient(typed);
        }

        // Returns the address field's contents only when they parse as a bare
        // IPv4, otherwise null. Escape hatch for when LAN IP auto-detection
        // picks the wrong network adapter.
        private string ReadIPv4Override()
        {
            if (_addressField == null || string.IsNullOrWhiteSpace(_addressField.text))
                return null;

            string trimmed = _addressField.text.Trim();
            bool isIPv4 = IPAddress.TryParse(trimmed, out IPAddress parsed) &&
                          parsed.AddressFamily == AddressFamily.InterNetwork;

            return isIPv4 ? trimmed : null;
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
