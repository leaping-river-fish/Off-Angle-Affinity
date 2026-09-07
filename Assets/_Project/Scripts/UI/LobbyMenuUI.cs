// =============================================================================
// LobbyMenuUI — shows the current session's join code and the live list of
// connected players.
//
// ARCHITECTURE:
//   Framework-agnostic like NetworkMenuUI: no FishNet import here. Player
//   connect/disconnect comes from LobbyPlayerList.Instance.ConnectionIds
//   (a synced SyncList any peer can read), and host/client status comes from
//   NetworkMenuController.IsHost.
//
// PLACEMENT:
//   Root of the "Lobby Menu" prefab, in the Lobby scene. Since Lobby only
//   ever loads for an already-connected peer (see OnEnable), this shows
//   itself immediately rather than waiting on a Connected event.
// =============================================================================

using System.Collections;
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
        [Tooltip("Leave null to auto-resolve via FindFirstObjectByType. Same NetworkMenuController the Network Menu uses, on the persistent NetworkManager GameObject (a different scene than this one, so it can't be Inspector-dragged).")]
        [SerializeField] private NetworkMenuController _controller;

        [Header("Panel Root")]
        [Tooltip("GameObject to show once connected and hide on disconnect. Usually this component's own GameObject.")]
        [SerializeField] private GameObject _panelRoot;

        [Header("Widgets")]
        [SerializeField] private TMP_Text _codeText;
        [SerializeField] private RectTransform _rowContainer;
        [SerializeField] private PlayerRowUI _playerRowPrefab;
        [SerializeField] private Button _startGameButton;
        [SerializeField] private Button _leaveGameButton;
        [Tooltip("Copies the current join code (not the host address) to the system clipboard. Assign on the Lobby Menu prefab.")]
        [SerializeField] private Button _copyCodeButton;

        [Header("Copy Feedback")]
        [Tooltip("Starts hidden. Shown briefly after a successful copy. Parent it under the copy button so it follows that control.")]
        [SerializeField] private GameObject _copiedTooltip;
        [Tooltip("Optional. If assigned, set to Copied Tooltip Message when shown.")]
        [SerializeField] private TMP_Text _copiedTooltipText;
        [SerializeField] private string _copiedTooltipMessage = "Copied to clipboard";
        [Tooltip("Seconds the tooltip stays fully visible before the fade starts.")]
        [SerializeField] private float _copiedTooltipDuration = 1.5f;
        [Tooltip("Seconds to fade the tooltip out. 0 skips the fade and hides immediately.")]
        [SerializeField] private float _copiedTooltipFadeDuration = 0.35f;

        private readonly List<PlayerRowUI> _rows = new List<PlayerRowUI>();

        private string _sessionCode = "";
        private Coroutine _copiedTooltipRoutine;
        private CanvasGroup _copiedTooltipCanvasGroup;

        private void Awake()
        {
            if (_controller == null)
                _controller = FindFirstObjectByType<NetworkMenuController>();

            CacheCopiedTooltipCanvasGroup();
            HideCopiedTooltip();

            if (_startGameButton != null)
                _startGameButton.onClick.AddListener(OnStartGameClicked);

            if (_leaveGameButton != null)
                _leaveGameButton.onClick.AddListener(OnLeaveGameClicked);

            if (_copyCodeButton != null)
                _copyCodeButton.onClick.AddListener(OnCopyCodeClicked);
        }

        private void OnEnable()
        {
            if (_controller == null)
                return;

            _controller.Disconnected += HandleDisconnected;
            _controller.SessionCodeReady += HandleSessionCodeReady;
            LobbyPlayerList.InstanceReady += SubscribeToPlayerList;
            PlayerNameRegistry.InstanceReady += SubscribeToNameRegistry;

            // Lobby is its own FishNet scene now: it only ever loads for a peer
            // that's already connected (the host right after the server starts,
            // or a joining client via FishNet's automatic global-scene sync on
            // connect). Waiting for NetworkMenuController.Connected here is racy
            // -- the host's own loopback client can reach Started before this
            // scene has even finished loading, so the event fires before
            // anything exists to catch it, and this panel would stay hidden
            // forever. Treat "this object exists" as "connected" instead.
            InitializeConnectedView();
        }

        private void OnDisable()
        {
            if (_controller == null)
                return;

            _controller.Disconnected -= HandleDisconnected;
            _controller.SessionCodeReady -= HandleSessionCodeReady;
            LobbyPlayerList.InstanceReady -= SubscribeToPlayerList;
            PlayerNameRegistry.InstanceReady -= SubscribeToNameRegistry;

            UnsubscribeFromPlayerList();
            UnsubscribeFromNameRegistry();
            HideCopiedTooltip();
        }

        private void OnDestroy()
        {
            if (_startGameButton != null)
                _startGameButton.onClick.RemoveListener(OnStartGameClicked);

            if (_leaveGameButton != null)
                _leaveGameButton.onClick.RemoveListener(OnLeaveGameClicked);

            if (_copyCodeButton != null)
                _copyCodeButton.onClick.RemoveListener(OnCopyCodeClicked);
        }

        // ------------------------------------------------------------------
        // Controller callbacks
        // ------------------------------------------------------------------

        private void InitializeConnectedView()
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(true);

            if (_startGameButton != null)
                _startGameButton.interactable = _controller.IsHost;

            // The code is announced right when the server starts, which is
            // what triggers the Lobby scene to load in the first place -- so
            // this object never exists in time to catch that event firing.
            // Pull whatever NetworkMenuController already has.
            _sessionCode = _controller.CurrentSessionCode;
            RefreshCodeText();

            SubscribeToPlayerList();
            SubscribeToNameRegistry();
        }

        private void HandleDisconnected(string reason)
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(false);

            UnsubscribeFromPlayerList();
            UnsubscribeFromNameRegistry();
            ClearRows();

            _sessionCode = "";
            RefreshCodeText();
            HideCopiedTooltip();
        }

        private void HandleSessionCodeReady(string code)
        {
            _sessionCode = code ?? "";
            RefreshCodeText();
        }

        private void RefreshCodeText()
        {
            if (_codeText != null)
            {
                _codeText.text = string.IsNullOrEmpty(_sessionCode)
                    ? ""
                    : $"Code: {_sessionCode}";
            }

            RefreshCopyCodeButton();
        }

        private void RefreshCopyCodeButton()
        {
            if (_copyCodeButton != null)
                _copyCodeButton.interactable = !string.IsNullOrEmpty(_sessionCode);
        }

        // ------------------------------------------------------------------
        // Player list
        // ------------------------------------------------------------------

        // FishNet activates scene NetworkObjects like LobbyPlayerList a beat
        // later than plain scene MonoBehaviours, so Instance can still be null
        // the first time this runs from OnEnable. LobbyPlayerList.InstanceReady
        // calls this again once it's actually set.
        private void SubscribeToPlayerList()
        {
            if (LobbyPlayerList.Instance == null)
                return;

            LobbyPlayerList.Instance.ConnectionIds.OnChange -= HandlePlayerListChanged;
            LobbyPlayerList.Instance.ConnectionIds.OnChange += HandlePlayerListChanged;
            RebuildRows();
        }

        private void UnsubscribeFromPlayerList()
        {
            if (LobbyPlayerList.Instance == null)
                return;

            LobbyPlayerList.Instance.ConnectionIds.OnChange -= HandlePlayerListChanged;
        }

        private void HandlePlayerListChanged(FishNet.Object.Synchronizing.SyncListOperation op, int index, int oldItem, int newItem, bool asServer) =>
            RebuildRows();

        // Same "any change -> rebuild everything" idiom as the player list
        // above, so a rename while everyone is sitting in the lobby shows up
        // immediately instead of waiting for the next join/leave.
        private void SubscribeToNameRegistry()
        {
            if (PlayerNameRegistry.Instance == null)
                return;

            PlayerNameRegistry.Instance.Names.OnChange -= HandleNameRegistryChanged;
            PlayerNameRegistry.Instance.Names.OnChange += HandleNameRegistryChanged;
            RebuildRows();
        }

        private void UnsubscribeFromNameRegistry()
        {
            if (PlayerNameRegistry.Instance == null)
                return;

            PlayerNameRegistry.Instance.Names.OnChange -= HandleNameRegistryChanged;
        }

        private void HandleNameRegistryChanged(FishNet.Object.Synchronizing.SyncDictionaryOperation op, int key, string value, bool asServer) =>
            RebuildRows();

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

        // Loads the AffinitySelect scene, not the Game scene - the match starts
        // from there once players have chosen. See GameFlowController.
        private void OnStartGameClicked() => GameFlowController.Instance?.RequestStartMatch();

        // Same LeaveSession() path as PauseMenuUI/MatchEndUI: stops the
        // connection (and the server too, if host) and returns to the main
        // menu once the resulting Stopped callback fires.
        private void OnLeaveGameClicked() => _controller?.LeaveSession();

        private void OnCopyCodeClicked()
        {
            if (string.IsNullOrEmpty(_sessionCode))
                return;

            GUIUtility.systemCopyBuffer = _sessionCode;
            ShowCopiedTooltip();
        }

        private void ShowCopiedTooltip()
        {
            if (_copiedTooltip == null)
                return;

            CacheCopiedTooltipCanvasGroup();

            if (_copiedTooltipText != null)
                _copiedTooltipText.text = _copiedTooltipMessage;

            if (_copiedTooltipRoutine != null)
                StopCoroutine(_copiedTooltipRoutine);

            _copiedTooltip.SetActive(true);
            SetCopiedTooltipAlpha(1f);
            _copiedTooltipRoutine = StartCoroutine(FadeCopiedTooltipOut());
        }

        private IEnumerator FadeCopiedTooltipOut()
        {
            yield return new WaitForSecondsRealtime(_copiedTooltipDuration);

            float fadeDuration = Mathf.Max(0f, _copiedTooltipFadeDuration);
            if (fadeDuration > 0f)
            {
                float elapsed = 0f;
                while (elapsed < fadeDuration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    SetCopiedTooltipAlpha(1f - Mathf.Clamp01(elapsed / fadeDuration));
                    yield return null;
                }
            }

            HideCopiedTooltip();
        }

        private void HideCopiedTooltip()
        {
            if (_copiedTooltipRoutine != null)
            {
                StopCoroutine(_copiedTooltipRoutine);
                _copiedTooltipRoutine = null;
            }

            SetCopiedTooltipAlpha(0f);

            if (_copiedTooltip != null)
                _copiedTooltip.SetActive(false);
        }

        private void CacheCopiedTooltipCanvasGroup()
        {
            if (_copiedTooltip == null || _copiedTooltipCanvasGroup != null)
                return;

            _copiedTooltipCanvasGroup = _copiedTooltip.GetComponent<CanvasGroup>();
            if (_copiedTooltipCanvasGroup == null)
                _copiedTooltipCanvasGroup = _copiedTooltip.AddComponent<CanvasGroup>();

            _copiedTooltipCanvasGroup.blocksRaycasts = false;
            _copiedTooltipCanvasGroup.interactable = false;
        }

        private void SetCopiedTooltipAlpha(float alpha)
        {
            if (_copiedTooltipCanvasGroup != null)
                _copiedTooltipCanvasGroup.alpha = alpha;
        }
    }
}
