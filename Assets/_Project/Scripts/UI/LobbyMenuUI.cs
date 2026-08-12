// =============================================================================
// LobbyMenuUI — shows the current session's join code and the live list of
// connected players once NetworkMenuController reports a connection.
//
// ARCHITECTURE:
//   Framework-agnostic like NetworkMenuUI: no FishNet import here. Player
//   connect/disconnect comes from LobbyPlayerList.Instance.ConnectionIds
//   (a synced SyncList any peer can read), and host/client status comes from
//   NetworkMenuController.IsHost.
//
// PLACEMENT:
//   Root of the "Lobby Menu" prefab, inactive by default -- it only shows
//   itself once NetworkMenuController.Connected fires.
// =============================================================================

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using OffAngle.Networking;

namespace OffAngle.UI
{
    public class LobbyMenuUI : MonoBehaviour
    {
        [Header("Controller")]
        [Tooltip("Same NetworkMenuController the Network Menu uses. Usually lives on the NetworkManager GameObject.")]
        [SerializeField] private NetworkMenuController _controller;

        [Header("Panel Root")]
        [Tooltip("GameObject to show once connected and hide on disconnect. Usually this component's own GameObject.")]
        [SerializeField] private GameObject _panelRoot;

        [Header("Widgets")]
        [SerializeField] private TMP_Text _codeText;
        [SerializeField] private RectTransform _rowContainer;
        [SerializeField] private PlayerRowUI _playerRowPrefab;
        [SerializeField] private Button _startGameButton;

        /// <summary>Fires when the host clicks Start Game. Nothing implemented downstream yet -- hook up map loading here later.</summary>
        public event Action StartGameRequested;

        private readonly List<PlayerRowUI> _rows = new List<PlayerRowUI>();

        private void Awake()
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(false);

            if (_startGameButton != null)
                _startGameButton.onClick.AddListener(OnStartGameClicked);
        }

        private void OnEnable()
        {
            if (_controller == null)
                return;

            _controller.Connected += HandleConnected;
            _controller.Disconnected += HandleDisconnected;
            _controller.SessionCodeReady += HandleSessionCodeReady;
        }

        private void OnDisable()
        {
            if (_controller == null)
                return;

            _controller.Connected -= HandleConnected;
            _controller.Disconnected -= HandleDisconnected;
            _controller.SessionCodeReady -= HandleSessionCodeReady;

            UnsubscribeFromPlayerList();
        }

        private void OnDestroy()
        {
            if (_startGameButton != null)
                _startGameButton.onClick.RemoveListener(OnStartGameClicked);
        }

        // ------------------------------------------------------------------
        // Controller callbacks
        // ------------------------------------------------------------------

        private void HandleConnected()
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(true);

            if (_startGameButton != null)
                _startGameButton.interactable = _controller.IsHost;

            SubscribeToPlayerList();
        }

        private void HandleDisconnected(string reason)
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(false);

            UnsubscribeFromPlayerList();
            ClearRows();

            if (_codeText != null)
                _codeText.text = "";
        }

        private void HandleSessionCodeReady(string code)
        {
            if (_codeText == null)
                return;

            _codeText.text = string.IsNullOrEmpty(code) ? "" : $"Code: {code}";
        }

        // ------------------------------------------------------------------
        // Player list (LobbyPlayerList.Instance is a scene object, present
        // before Connected can ever fire, so it's safe to grab here)
        // ------------------------------------------------------------------

        private void SubscribeToPlayerList()
        {
            if (LobbyPlayerList.Instance == null)
                return;

            LobbyPlayerList.Instance.ConnectionIds.OnChange += HandlePlayerListChanged;
            LobbyPlayerList.Instance.GameStarted += HandleGameStarted;
            RebuildRows();
        }

        private void UnsubscribeFromPlayerList()
        {
            if (LobbyPlayerList.Instance == null)
                return;

            LobbyPlayerList.Instance.ConnectionIds.OnChange -= HandlePlayerListChanged;
            LobbyPlayerList.Instance.GameStarted -= HandleGameStarted;
        }

        private void HandlePlayerListChanged(FishNet.Object.Synchronizing.SyncListOperation op, int index, int oldItem, int newItem, bool asServer) =>
            RebuildRows();

        // Fires on every peer once PlayerSpawner.SpawnAll() has run -- this is
        // what actually closes the lobby, for the host AND every joiner, not
        // just whoever clicked Start Game.
        private void HandleGameStarted()
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(false);
        }

        private void RebuildRows()
        {
            ClearRows();

            if (LobbyPlayerList.Instance == null || _playerRowPrefab == null || _rowContainer == null)
                return;

            foreach (int connectionId in LobbyPlayerList.Instance.ConnectionIds)
            {
                PlayerRowUI row = Instantiate(_playerRowPrefab, _rowContainer);
                row.SetLabel(LobbyPlayerList.LabelFor(connectionId));
                _rows.Add(row);
            }
        }

        private void ClearRows()
        {
            foreach (PlayerRowUI row in _rows)
            {
                if (row != null)
                    Destroy(row.gameObject);
            }
            _rows.Clear();
        }

        // ------------------------------------------------------------------
        // Button handlers
        // ------------------------------------------------------------------

        private void OnStartGameClicked() => StartGameRequested?.Invoke();
    }
}
