// =============================================================================
// WeaponSelectionMenu — controller for the Weapon Selection Menu prefab.
//
// ARCHITECTURE:
//   Does NOT create any UI. Every WeaponChoiceUI under this object is placed
//   by hand in the prefab (one per weapon, grouped into Primary/Sidearm/...
//   sections you build yourself). This script just finds them, listens for
//   clicks, and forwards the choice to LoadoutManager - the single source of
//   truth other systems (PlayerWeaponEquipper) read from.
//
//   Lives on a GameObject that stays active (same convention as
//   DeathScreenController living on "HUD Canvas"); Open()/Close()/Toggle()
//   show/hide a separate _panelRoot child that starts inactive. That way
//   this script's own Awake/OnEnable always run, regardless of menu visibility.
// =============================================================================

using System.Collections.Generic;
using OffAngle.Core;
using OffAngle.Player;
using OffAngle.Weapons;
using UnityEngine;

namespace OffAngle.UI.Weapons
{
    public class WeaponSelectionMenu : MonoBehaviour
    {
        [Tooltip("GameObject shown/hidden by Open()/Close()/Toggle(). Usually the panel containing every category section. Should start INACTIVE in the prefab.")]
        [SerializeField] private GameObject _panelRoot;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent. OpenLoadoutMenuStarted (bound to a keybind in the Input Actions asset) toggles this menu.\n\nDo NOT hand-assign this by dragging the Player prefab asset's PlayerInputReader in - that points at the static prefab asset, not the live instantiated player, and will silently never fire. This menu must live under the Player's hierarchy (e.g. under HUD Canvas) so the auto-resolve can find the correct live instance.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("Leave null to auto-resolve. Disabled while the menu is open (and re-enabled on close) so its own OnEnable/OnDisable unlock the cursor and pause look - see PlayerCameraController.\n\nLives under a sibling branch (Camera Pivot) rather than a direct ancestor of this menu, so auto-resolve searches the whole player hierarchy via the already-resolved PlayerInputReader's GameObject rather than GetComponentInParent.")]
        [SerializeField] private PlayerCameraController _cameraController;

        private readonly List<WeaponChoiceUI> _choices = new();

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_inputReader == null)
                _inputReader = GetComponentInParent<PlayerInputReader>();
            if (_cameraController == null && _inputReader != null)
                _cameraController = _inputReader.GetComponentInChildren<PlayerCameraController>(true);

            GetComponentsInChildren(true, _choices);
            foreach (WeaponChoiceUI choice in _choices)
                choice.Chosen += HandleChosen;
        }

        private void OnEnable()
        {
            if (LoadoutManager.Instance != null)
                LoadoutManager.Instance.SelectionChanged += HandleSelectionChanged;
            if (_inputReader != null)
                _inputReader.OpenLoadoutMenuStarted += Toggle;

            RefreshHighlights();
        }

        private void OnDisable()
        {
            if (LoadoutManager.Instance != null)
                LoadoutManager.Instance.SelectionChanged -= HandleSelectionChanged;
            if (_inputReader != null)
                _inputReader.OpenLoadoutMenuStarted -= Toggle;
        }

        private void OnDestroy()
        {
            foreach (WeaponChoiceUI choice in _choices)
            {
                if (choice != null)
                    choice.Chosen -= HandleChosen;
            }
        }

        // ------------------------------------------------------------------
        // Open / close
        // ------------------------------------------------------------------

        public void Open()
        {
            if (_panelRoot == null) return;
            _panelRoot.SetActive(true);
            if (_cameraController != null)
                _cameraController.enabled = false;
            RefreshHighlights();
        }

        public void Close()
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(false);
            if (_cameraController != null)
                _cameraController.enabled = true;
        }

        public void Toggle()
        {
            if (_panelRoot == null) return;
            if (_panelRoot.activeSelf) Close();
            else Open();
        }

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------

        private void HandleChosen(WeaponDefinition definition)
        {
            if (definition == null || definition.Category == null) return;

            if (LoadoutManager.Instance == null)
            {
                Debug.LogWarning($"[{nameof(WeaponSelectionMenu)}] No LoadoutManager found in the scene.", this);
                return;
            }

            LoadoutManager.Instance.SetSelected(definition.Category, definition);
        }

        private void HandleSelectionChanged(WeaponCategory category, WeaponDefinition definition)
        {
            RefreshHighlights();
        }

        private void RefreshHighlights()
        {
            if (LoadoutManager.Instance == null) return;

            foreach (WeaponChoiceUI choice in _choices)
            {
                if (choice == null || choice.Definition == null) continue;

                WeaponDefinition selected = LoadoutManager.Instance.GetSelected(choice.Definition.Category);
                choice.SetSelectedVisual(selected == choice.Definition);
            }
        }
    }
}
