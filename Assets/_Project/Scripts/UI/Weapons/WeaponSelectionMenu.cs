// =============================================================================
// WeaponSelectionMenu — controller for the Weapon Selection Menu prefab.
//
// ARCHITECTURE (AUTO-POPULATION):
//   Dynamically creates WeaponChoiceUI instances at runtime by reading from a
//   WeaponRegistry asset. Weapons are grouped by category (Primary, Sidearm,
//   etc.) and spawned into their respective container transforms.
//
//   Adding new weapons = just create the WeaponDefinition asset, refresh the
//   WeaponRegistry, and they appear automatically. No manual prefab editing.
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
        [Header("Menu Panel")]
        [Tooltip("DEPRECATED: Leave empty. The menu now uses CanvasGroup for visibility control instead of a panel GameObject.")]
        [SerializeField] private GameObject _panelRoot;

        [Tooltip("CanvasGroup on this GameObject or a parent. Used to show/hide the menu. Will be auto-added if missing.")]
        [SerializeField] private CanvasGroup _canvasGroup;

        [Header("Auto-Population")]
        [Tooltip("Registry containing all weapons in the project. Weapons are read from this and spawned dynamically at runtime.")]
        [SerializeField] private WeaponRegistry _weaponRegistry;

        [Tooltip("Prefab template for individual weapon choices. Instantiated once per weapon in the registry.")]
        [SerializeField] private WeaponChoiceUI _choicePrefab;

        [Tooltip("Container transform where Primary weapon choices will be spawned.")]
        [SerializeField] private Transform _primaryContainer;

        [Tooltip("Container transform where Sidearm weapon choices will be spawned.")]
        [SerializeField] private Transform _sidearmContainer;

        [Header("Input & Camera")]
        [Tooltip("Leave null to auto-resolve via GetComponentInParent. OpenLoadoutMenuStarted (bound to a keybind in the Input Actions asset) toggles this menu.\n\nDo NOT hand-assign this by dragging the Player prefab asset's PlayerInputReader in - that points at the static prefab asset, not the live instantiated player, and will silently never fire. This menu must live under the Player's hierarchy (e.g. under HUD Canvas) so the auto-resolve can find the correct live instance.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent. Used to request Menu state when opening and Gameplay state when closing.")]
        [SerializeField] private PlayerInputStateController _stateController;

        private readonly List<WeaponChoiceUI> _choices = new();
        private readonly Dictionary<WeaponCategory, Transform> _categoryContainers = new();

        // Button.onClick + IPointerClickHandler can both fire, and a physical
        // double-click sends a second Chosen before the first equip settles.
        // Ignore extra picks until this clears in LateUpdate.
        private bool _selectionBusy;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_inputReader == null)
                _inputReader = GetComponentInParent<PlayerInputReader>();
            if (_stateController == null)
                _stateController = GetComponentInParent<PlayerInputStateController>();

            // Get or add CanvasGroup for visibility control
            if (_canvasGroup == null)
            {
                _canvasGroup = GetComponent<CanvasGroup>();
                if (_canvasGroup == null)
                    _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            // Fix RectTransform positioning if needed
            FixRectTransformAnchors();
            FixPanelChildAnchors();
            EnsureMenuCanvasSorting();

            // Start hidden
            SetMenuVisible(false);

            PopulateWeaponChoices();
        }

        private void FixRectTransformAnchors()
        {
            RectTransform rectTransform = GetComponent<RectTransform>();
            if (rectTransform == null) return;

            bool needsFix = rectTransform.anchorMin.x < 0.1f || rectTransform.anchorMin.y < 0.1f ||
                           rectTransform.anchorMax.x < 0.9f || rectTransform.anchorMax.y < 0.9f ||
                           rectTransform.localScale == Vector3.zero;

            if (!needsFix) return;

            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            if (rectTransform.localScale == Vector3.zero)
                rectTransform.localScale = Vector3.one;
        }

        private void FixPanelChildAnchors()
        {
            Transform panelChild = transform.Find("Panel");
            if (panelChild == null) return;

            RectTransform panelRect = panelChild.GetComponent<RectTransform>();
            if (panelRect == null) return;

            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            if (panelRect.localScale == Vector3.zero)
                panelRect.localScale = Vector3.one;
        }

        private void EnsureMenuCanvasSorting()
        {
            // Nested overlay canvases need a higher sort order to receive raycasts
            // above the combat HUD canvas.
            Canvas canvas = GetComponent<Canvas>();
            if (canvas == null) return;

            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;

            if (!TryGetComponent<UnityEngine.UI.GraphicRaycaster>(out _))
                gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        }

        private void PopulateWeaponChoices()
        {
            if (_weaponRegistry == null)
                return;

            if (_choicePrefab == null)
                return;

            ClearExistingChoices();
            BuildCategoryContainerMap();

            foreach (WeaponDefinition weapon in _weaponRegistry.AllWeapons)
            {
                if (weapon == null || weapon.Category == null) continue;

                if (!_categoryContainers.TryGetValue(weapon.Category, out Transform container))
                    continue;

                if (container == null) continue;

                WeaponChoiceUI choice = Instantiate(_choicePrefab, container);
                choice.SetDefinition(weapon);
                choice.Chosen += HandleChosen;
                _choices.Add(choice);
            }
        }

        private void ClearExistingChoices()
        {
            // Find and destroy any existing WeaponChoiceUI instances (from old manual setup)
            WeaponChoiceUI[] existingChoices = GetComponentsInChildren<WeaponChoiceUI>(true);
            foreach (WeaponChoiceUI choice in existingChoices)
            {
                if (choice != null)
                {
                    choice.Chosen -= HandleChosen;
                    Destroy(choice.gameObject);
                }
            }
            _choices.Clear();
        }

        private void BuildCategoryContainerMap()
        {
            _categoryContainers.Clear();

            if (_weaponRegistry == null) return;

            foreach (WeaponCategory category in _weaponRegistry.GetAllCategories())
            {
                if (category == null) continue;

                Transform container = GetContainerForCategory(category);
                if (container != null)
                {
                    _categoryContainers[category] = container;
                }
            }
        }

        private Transform GetContainerForCategory(WeaponCategory category)
        {
            if (category == null) return null;

            if (string.Equals(category.Id, "Primary", System.StringComparison.OrdinalIgnoreCase) && _primaryContainer != null)
                return _primaryContainer;

            if (string.Equals(category.Id, "Sidearm", System.StringComparison.OrdinalIgnoreCase) && _sidearmContainer != null)
                return _sidearmContainer;

            return null;
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
            if (_canvasGroup == null)
                return;

            // Unlock cursor / switch maps BEFORE showing UI so the first click works
            if (_stateController != null)
                _stateController.EnterMenuState();

            SetMenuVisible(true);
            RefreshHighlights();
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
            if (_canvasGroup == null)
                return;
            if (IsMenuVisible()) Close();
            else Open();
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
            // Check alpha to determine visibility
            return _canvasGroup != null && _canvasGroup.alpha > 0.5f;
        }

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------

        private void LateUpdate()
        {
            _selectionBusy = false;
        }

        private void HandleChosen(WeaponDefinition definition)
        {
            if (_selectionBusy) return;
            if (definition == null || definition.Category == null) return;

            if (LoadoutManager.Instance == null)
            {
                Debug.LogWarning($"[{nameof(WeaponSelectionMenu)}] No LoadoutManager found in the scene.", this);
                return;
            }

            if (LoadoutManager.Instance.GetSelected(definition.Category) == definition)
                return;

            _selectionBusy = true;
            LoadoutManager.Instance.SetSelected(definition.Category, definition);
            RefreshHighlights();
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
