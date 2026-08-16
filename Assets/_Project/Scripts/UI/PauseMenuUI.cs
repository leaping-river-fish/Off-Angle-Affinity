// =============================================================================
// PauseMenuUI — controller for the Esc pause menu.
//
// Same shape as WeaponSelectionMenu: CanvasGroup-based show/hide, auto-resolved
// PlayerInputReader/PlayerInputStateController via GetComponentInParent, and
// Open()/Close()/Toggle() driving state transitions. Must live under the
// Player's live hierarchy (e.g. under HUD Canvas) so auto-resolution finds the
// instantiated player, not the Player prefab asset.
// =============================================================================

using OffAngle.Core;
using OffAngle.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OffAngle.UI
{
    public class PauseMenuUI : MonoBehaviour
    {
        [Header("Panel")]
        [Tooltip("CanvasGroup on this GameObject or a parent. Used to show/hide the menu. Will be auto-added if missing.")]
        [SerializeField] private CanvasGroup _canvasGroup;

        [Tooltip("Root containing Resume / Settings / Exit to Main Menu / Exit to Desktop buttons.")]
        [SerializeField] private GameObject _mainPanelRoot;

        [Tooltip("Root containing the placeholder settings panel (title + Back button).")]
        [SerializeField] private GameObject _settingsPanelRoot;

        [Header("Scene")]
        [Tooltip("Fallback only. Exit to Main Menu normally goes through NetworkMenuController.LeaveSession(), which owns the scene name; this is used only when no controller exists (e.g. the Game scene was entered directly for a solo test).")]
        [SerializeField] private string _mainMenuSceneName = "MainMenu";

        [Tooltip("Leave null to auto-resolve via FindFirstObjectByType. Lives on the persistent NetworkManager GameObject, a different scene than this prefab, so it cannot be Inspector-dragged.")]
        [SerializeField] private NetworkMenuController _networkMenuController;

        [Header("Input & Camera")]
        [Tooltip("Leave null to auto-resolve via GetComponentInParent. PauseToggleStarted (Esc) toggles this menu.\n\nDo NOT hand-assign this by dragging the Player prefab asset's PlayerInputReader in - that points at the static prefab asset, not the live instantiated player, and will silently never fire. This menu must live under the Player's hierarchy (e.g. under HUD Canvas) so the auto-resolve can find the correct live instance.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent. Used to request Paused state when opening and Gameplay state when closing.")]
        [SerializeField] private PlayerInputStateController _stateController;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_inputReader == null)
                _inputReader = GetComponentInParent<PlayerInputReader>();
            if (_stateController == null)
                _stateController = GetComponentInParent<PlayerInputStateController>();

            if (_canvasGroup == null)
            {
                _canvasGroup = GetComponent<CanvasGroup>();
                if (_canvasGroup == null)
                    _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            ShowMainPanel();
            SetMenuVisible(false);
        }

        private void OnEnable()
        {
            if (_inputReader != null)
                _inputReader.PauseToggleStarted += HandlePauseToggle;
        }

        private void OnDisable()
        {
            if (_inputReader != null)
                _inputReader.PauseToggleStarted -= HandlePauseToggle;
        }

        // ------------------------------------------------------------------
        // Open / close
        // ------------------------------------------------------------------

        public void Open()
        {
            if (_canvasGroup == null || _stateController == null)
                return;

            // Unlock cursor / switch maps BEFORE showing UI so the first click works
            _stateController.EnterPausedState();

            ShowMainPanel();
            SetMenuVisible(true);
        }

        public void Close()
        {
            if (_canvasGroup != null)
                SetMenuVisible(false);
            if (_stateController != null)
                _stateController.EnterGameplayState();
        }

        public void Toggle()
        {
            if (IsMenuVisible()) Close();
            else Open();
        }

        private void HandlePauseToggle()
        {
            if (IsMenuVisible())
            {
                Close();
                return;
            }

            // Don't open over the Weapon Menu or the Death screen.
            if (_stateController == null || _stateController.CurrentState != PlayerInputState.Gameplay)
                return;

            Open();
        }

        private void SetMenuVisible(bool visible)
        {
            if (_canvasGroup == null) return;

            _canvasGroup.alpha = visible ? 1f : 0f;
            _canvasGroup.interactable = visible;
            _canvasGroup.blocksRaycasts = visible;
        }

        private bool IsMenuVisible()
        {
            return _canvasGroup != null && _canvasGroup.alpha > 0.5f;
        }

        // ------------------------------------------------------------------
        // Panel switching
        // ------------------------------------------------------------------

        private void ShowMainPanel()
        {
            if (_mainPanelRoot != null)
                _mainPanelRoot.SetActive(true);
            if (_settingsPanelRoot != null)
                _settingsPanelRoot.SetActive(false);
        }

        private void ShowSettingsPanel()
        {
            if (_mainPanelRoot != null)
                _mainPanelRoot.SetActive(false);
            if (_settingsPanelRoot != null)
                _settingsPanelRoot.SetActive(true);
        }

        // ------------------------------------------------------------------
        // Button handlers (wire these to OnClick in the Inspector)
        // ------------------------------------------------------------------

        public void OnResumeClicked() => Close();

        public void OnSettingsClicked() => ShowSettingsPanel();

        public void OnSettingsBackClicked() => ShowMainPanel();

        public void OnExitToMainMenuClicked()
        {
            // Must go through the controller, not straight to LoadScene: a bare
            // scene load only moves THIS player's local view. They stay
            // connected, so the server keeps their character spawned and it goes
            // on taking damage, dying and respawning while they sit in the menu.
            // LeaveSession() stops the connection first and loads the menu once
            // the disconnect is confirmed.
            if (_networkMenuController == null)
                _networkMenuController = FindFirstObjectByType<NetworkMenuController>();

            if (_networkMenuController != null)
            {
                _networkMenuController.LeaveSession();
                return;
            }

            // No controller at all means no NetworkManager, so there is no
            // connection to leave and a plain load is the correct behaviour.
            Debug.LogWarning(
                $"[{nameof(PauseMenuUI)}] No {nameof(NetworkMenuController)} found; loading '{_mainMenuSceneName}' without a network shutdown. " +
                "Expected only when the Game scene was entered directly rather than through Bootstrap.",
                this);
            SceneManager.LoadScene(_mainMenuSceneName);
        }

        public void OnExitToDesktopClicked()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
