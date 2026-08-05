// =============================================================================
// PlayerInputStateController — centralized player input/UI state management.
//
// ARCHITECTURE:
//   Single source of truth for whether the player is in Gameplay, Menu, or Dead
//   state. Controls Input Action map switching, cursor lock/visibility, and
//   HUD/menu UI visibility. All systems that care about input state should
//   subscribe to OnStateChanged or poll CurrentState rather than implementing
//   their own menu-checking logic.
// =============================================================================

using System;
using UnityEngine;
using OffAngle.Player;

namespace OffAngle.Core
{
    public class PlayerInputStateController : MonoBehaviour
    {
        [Header("Input & Camera")]
        [Tooltip("Leave null to auto-resolve via GetComponent.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("Leave null to auto-resolve via GetComponentInChildren. Used to override cursor control during state transitions.")]
        [SerializeField] private PlayerCameraController _cameraController;

        [Header("UI Roots")]
        [Tooltip("Root GameObject containing all combat HUD elements (health, ammo, crosshair, etc.). Shown in Gameplay, hidden in Menu/Dead.")]
        [SerializeField] private GameObject _combatHudRoot;

        [Tooltip("DEPRECATED: Menu now controls its own visibility via CanvasGroup. This field is no longer used.")]
        [SerializeField] private GameObject _menuUiRoot;

        private PlayerInputState _currentState = PlayerInputState.Gameplay;

        /// <summary>The current input state. Poll this or subscribe to OnStateChanged.</summary>
        public PlayerInputState CurrentState => _currentState;

        /// <summary>
        /// Raised whenever the state changes. Parameters: (oldState, newState).
        /// Systems should subscribe here to react to state transitions.
        /// </summary>
        public event Action<PlayerInputState, PlayerInputState> OnStateChanged;

        private void Awake()
        {
            if (_inputReader == null)
                _inputReader = GetComponent<PlayerInputReader>();

            if (_cameraController == null)
                _cameraController = GetComponentInChildren<PlayerCameraController>(true);
        }

        private void Start()
        {
            // After NetworkPlayerController activates HUD Canvas parent
            TransitionTo(PlayerInputState.Gameplay, force: true);
        }

        private void OnDestroy()
        {
            OnStateChanged = null;
        }

        public void EnterGameplayState()
        {
            TransitionTo(PlayerInputState.Gameplay);
        }

        public void EnterMenuState()
        {
            TransitionTo(PlayerInputState.Menu);
        }

        public void EnterDeathState()
        {
            TransitionTo(PlayerInputState.Dead);
        }

        private void TransitionTo(PlayerInputState newState, bool force = false)
        {
            if (_currentState == newState && !force)
                return;

            PlayerInputState oldState = _currentState;
            _currentState = newState;

            switch (newState)
            {
                case PlayerInputState.Gameplay:
                    ApplyGameplayState();
                    break;
                case PlayerInputState.Menu:
                    ApplyMenuState();
                    break;
                case PlayerInputState.Dead:
                    ApplyDeathState();
                    break;
            }

            OnStateChanged?.Invoke(oldState, newState);
        }

        private void ApplyGameplayState()
        {
            if (_inputReader != null && _inputReader)
            {
                _inputReader.EnablePlayerMap();
                _inputReader.DisableUIMap();
            }

            if (_cameraController != null && _cameraController && !_cameraController.enabled)
                _cameraController.enabled = true;

            SetCursorLocked(true);
            SetCombatHudVisible(true);
        }

        private void ApplyMenuState()
        {
            if (_inputReader != null && _inputReader)
            {
                _inputReader.DisablePlayerMap(); // keeps WeaponMenu (P) alive
                _inputReader.EnableUIMap();
            }

            if (_cameraController != null && _cameraController && _cameraController.enabled)
                _cameraController.enabled = false;

            SetCursorLocked(false);
            SetCombatHudVisible(false);
        }

        private void ApplyDeathState()
        {
            if (_inputReader != null && _inputReader)
                _inputReader.DisableAllMaps();

            if (_cameraController != null && _cameraController && _cameraController.enabled)
                _cameraController.enabled = false;

            SetCursorLocked(false);
            SetCombatHudVisible(false);
        }

        private static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void SetCombatHudVisible(bool visible)
        {
            if (_combatHudRoot == null || !_combatHudRoot)
                return;

            // Repair zero-scale UI that can get baked into nested canvases
            EnsureCombatHudScales();
            _combatHudRoot.SetActive(visible);
        }

        private void EnsureCombatHudScales()
        {
            RectTransform[] rects = _combatHudRoot.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rt = rects[i];
                if (rt.localScale == Vector3.zero)
                    rt.localScale = Vector3.one;
            }
        }
    }
}
