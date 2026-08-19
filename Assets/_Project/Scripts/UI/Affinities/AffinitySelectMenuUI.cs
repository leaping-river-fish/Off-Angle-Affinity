// =============================================================================
// AffinitySelectMenuUI — controller for the "Affinity Select Menu" prefab.
//
// ARCHITECTURE:
//   Owns 13 permanently-placed AffinityTreeNodeUI slots (1 affinity + 3
//   ultimates + 9 perks, row-major). The carousel swaps which AffinityDefinition
//   those slots display; it never itself registers a pick. Ultimate/perk nodes
//   only mutate LocalAffinitySelection once the browsed affinity has been
//   locked into the active slot via the Select button - see IsBrowsedAffinityLockedIn.
//
//   This is the only script that talks to LocalAffinitySelection /
//   AffinitySelectCoordinator; AffinityTreeNodeUI and AffinityDescriptionPanelUI
//   are dumb display/click components with no knowledge of either.
//
// SECONDARY RESTRICTIONS:
//   A secondary affinity never gets an ultimate and cannot draw a perk from
//   AffinityLoadoutRules.PrimaryOnlyRow - both are reflected by dimming
//   (CanvasGroup.alpha) and disabling (AffinityTreeNodeUI.SetInteractable) the
//   relevant rows while the Secondary slot is active. Nodes stay
//   click-to-inspect even while disabled (see AffinityTreeNodeUI).
// =============================================================================

using OffAngle.Affinities;
using OffAngle.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Affinities
{
    public class AffinitySelectMenuUI : MonoBehaviour
    {
        [Header("Slot Toggle")]
        [SerializeField] private Button _primaryToggleButton;
        [SerializeField] private Button _secondaryToggleButton;
        [SerializeField] private GameObject _primaryToggleHighlight;
        [SerializeField] private GameObject _secondaryToggleHighlight;

        [Header("Status")]
        [SerializeField] private TMP_Text _statusText;
        [SerializeField] private TMP_Text _countdownText;

        [Header("Carousel")]
        [Tooltip("The whole browse row (arrows + name). Hidden once the active slot has a locked-in affinity.")]
        [SerializeField] private GameObject _carouselRow;
        [SerializeField] private Button _carouselLeftButton;
        [SerializeField] private Button _carouselRightButton;
        [SerializeField] private TMP_Text _browsedAffinityNameText;
        [SerializeField] private Image _browsedAffinityIcon;

        [Header("Tree")]
        [Tooltip("The scrollable tree container. Shown only once the active slot has a locked-in affinity.")]
        [SerializeField] private GameObject _treeScrollView;
        [SerializeField] private AffinityTreeNodeUI _affinityNode;

        [Tooltip("Expected size 3, in the same order as AffinityDefinition.Ultimates.")]
        [SerializeField] private AffinityTreeNodeUI[] _ultimateNodes = new AffinityTreeNodeUI[3];

        [Tooltip("Expected size 9, row-major: index = row * 3 + column.")]
        [SerializeField] private AffinityTreeNodeUI[] _perkNodes = new AffinityTreeNodeUI[9];

        [Tooltip("CanvasGroup wrapping the ultimates row. Dimmed while editing Secondary - a secondary affinity never gets an ultimate.")]
        [SerializeField] private CanvasGroup _ultimatesCanvasGroup;

        [Tooltip("CanvasGroup wrapping perk row AffinityLoadoutRules.PrimaryOnlyRow. Dimmed while editing Secondary.")]
        [SerializeField] private CanvasGroup _primaryOnlyRowCanvasGroup;

        [Header("Select")]
        [Tooltip("The whole Select row. Hidden once the active slot has a locked-in affinity.")]
        [SerializeField] private GameObject _selectRow;
        [SerializeField] private Button _selectButton;
        [SerializeField] private TMP_Text _selectButtonLabel;

        [Tooltip("Shown only once the active slot has a locked-in affinity - clears that slot's pick so the player can browse again.")]
        [SerializeField] private Button _deselectButton;

        [Header("Confirm / Ready")]
        [SerializeField] private Button _confirmButton;
        [SerializeField] private TMP_Text _confirmButtonLabel;

        [Header("Description Panel")]
        [SerializeField] private AffinityDescriptionPanelUI _descriptionPanel;

        private AffinityRegistry _registry;
        private int _carouselIndex;
        private bool _activeSlotIsSecondary;
        private bool _hasSubmitted;
        private int _lastDisplayedSeconds = -1;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_primaryToggleButton != null)
                _primaryToggleButton.onClick.AddListener(HandlePrimaryToggleClicked);
            if (_secondaryToggleButton != null)
                _secondaryToggleButton.onClick.AddListener(HandleSecondaryToggleClicked);
            if (_carouselLeftButton != null)
                _carouselLeftButton.onClick.AddListener(HandleCarouselLeftClicked);
            if (_carouselRightButton != null)
                _carouselRightButton.onClick.AddListener(HandleCarouselRightClicked);
            if (_selectButton != null)
                _selectButton.onClick.AddListener(HandleSelectClicked);
            if (_confirmButton != null)
                _confirmButton.onClick.AddListener(HandleConfirmButtonClicked);
            if (_deselectButton != null)
                _deselectButton.onClick.AddListener(HandleDeselectClicked);

            if (_affinityNode != null)
                _affinityNode.Clicked += HandleAffinityNodeClicked;

            for (int i = 0; i < _ultimateNodes.Length; i++)
            {
                if (_ultimateNodes[i] == null) continue;
                int index = i; // capture per-iteration, not the shared loop variable
                _ultimateNodes[i].Clicked += () => HandleUltimateClicked(index);
            }

            for (int i = 0; i < _perkNodes.Length; i++)
            {
                if (_perkNodes[i] == null) continue;
                int index = i;
                _perkNodes[i].Clicked += () => HandlePerkClicked(index);
            }

            _descriptionPanel?.Clear();
        }

        private void OnEnable()
        {
            _registry = LocalAffinitySelection.Instance != null ? LocalAffinitySelection.Instance.Registry : null;

            if (LocalAffinitySelection.Instance != null)
                LocalAffinitySelection.Instance.SelectionChanged += HandleSelectionChanged;

            AffinitySelectCoordinator.InstanceReady += HandleCoordinatorReady;
            if (AffinitySelectCoordinator.Instance != null)
                HandleCoordinatorReady();

            SetActiveSlot(false);
            RefreshBrowsedAffinity();
        }

        private void OnDisable()
        {
            if (LocalAffinitySelection.Instance != null)
                LocalAffinitySelection.Instance.SelectionChanged -= HandleSelectionChanged;

            AffinitySelectCoordinator.InstanceReady -= HandleCoordinatorReady;
        }

        private void OnDestroy()
        {
            if (_primaryToggleButton != null)
                _primaryToggleButton.onClick.RemoveListener(HandlePrimaryToggleClicked);
            if (_secondaryToggleButton != null)
                _secondaryToggleButton.onClick.RemoveListener(HandleSecondaryToggleClicked);
            if (_carouselLeftButton != null)
                _carouselLeftButton.onClick.RemoveListener(HandleCarouselLeftClicked);
            if (_carouselRightButton != null)
                _carouselRightButton.onClick.RemoveListener(HandleCarouselRightClicked);
            if (_selectButton != null)
                _selectButton.onClick.RemoveListener(HandleSelectClicked);
            if (_confirmButton != null)
                _confirmButton.onClick.RemoveListener(HandleConfirmButtonClicked);
            if (_deselectButton != null)
                _deselectButton.onClick.RemoveListener(HandleDeselectClicked);
        }

        private void Update()
        {
            if (AffinitySelectCoordinator.Instance == null) return;

            int seconds = AffinitySelectCoordinator.Instance.SecondsRemaining;
            if (seconds == _lastDisplayedSeconds) return;

            _lastDisplayedSeconds = seconds;
            RefreshConfirmButton();
            RefreshCountdownText();
        }

        // ------------------------------------------------------------------
        // Carousel / slot toggle
        // ------------------------------------------------------------------

        private void HandlePrimaryToggleClicked() => SetActiveSlot(false);
        private void HandleSecondaryToggleClicked() => SetActiveSlot(true);
        private void HandleCarouselLeftClicked() => Step(-1);
        private void HandleCarouselRightClicked() => Step(1);

        private void Step(int delta)
        {
            if (_registry == null || _registry.AllAffinities == null || _registry.AllAffinities.Count == 0) return;

            int count = _registry.AllAffinities.Count;
            _carouselIndex = ((_carouselIndex + delta) % count + count) % count;
            RefreshBrowsedAffinity();
        }

        private void SetActiveSlot(bool asSecondary)
        {
            _activeSlotIsSecondary = asSecondary;

            // If this slot already has an affinity locked in, jump the carousel to
            // it so the tree (which reads CurrentBrowsedAffinity()) shows the right
            // one instead of whatever was being browsed under the other slot.
            SnapCarouselToActiveSlotAffinity();

            RefreshToggleVisuals();
            RefreshVisibilityState();
            RefreshTreeInteractability();
            RefreshSelectedHighlights();
            RefreshStatusText();
            RefreshSelectButtonLabel();
        }

        private void RefreshToggleVisuals()
        {
            if (_primaryToggleHighlight != null)
                _primaryToggleHighlight.SetActive(!_activeSlotIsSecondary);
            if (_secondaryToggleHighlight != null)
                _secondaryToggleHighlight.SetActive(_activeSlotIsSecondary);
        }

        private void SnapCarouselToActiveSlotAffinity()
        {
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (selection == null || _registry == null || _registry.AllAffinities == null) return;

            AffinityDefinition current = _activeSlotIsSecondary ? selection.Secondary : selection.Primary;
            if (current == null) return;

            int index = _registry.AllAffinities.IndexOf(current);
            if (index >= 0)
            {
                _carouselIndex = index;
                RefreshBrowsedAffinity();
            }
        }

        // Carousel/Select are for browsing before a pick is locked in; the tree is
        // for editing ultimate/perks after. Only one half of that is ever visible
        // for the active slot at a time - see the user-facing flow this mirrors:
        // browse -> Select -> (carousel/select hide, tree/Deselect show) -> Deselect
        // to go back to browsing. Applies independently to Primary and Secondary.
        private void RefreshVisibilityState()
        {
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            bool hasAffinity = selection != null && (_activeSlotIsSecondary ? selection.Secondary != null : selection.Primary != null);

            if (_carouselRow != null)
                _carouselRow.SetActive(!hasAffinity);
            if (_selectRow != null)
                _selectRow.SetActive(!hasAffinity);
            if (_treeScrollView != null)
            {
                _treeScrollView.SetActive(hasAffinity);

                // Unity doesn't reliably re-run nested Layout Group / Content Size
                // Fitter passes the moment a previously-inactive hierarchy is
                // reactivated - without forcing it, Content can be left at a stale
                // (often zero) size: nodes appear to vanish and there's nothing to
                // scroll even though every component is configured correctly.
                // ForceUpdateCanvases first flushes whatever layout work Unity
                // already queued from the SetActive above - calling
                // ForceRebuildLayoutImmediate without it can rebuild against the
                // still-stale pre-activation state.
                if (hasAffinity)
                {
                    Canvas.ForceUpdateCanvases();
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_treeScrollView.transform);
                }
            }
            if (_deselectButton != null)
                _deselectButton.gameObject.SetActive(hasAffinity);
        }

        private AffinityDefinition CurrentBrowsedAffinity()
        {
            if (_registry == null || _registry.AllAffinities == null) return null;
            if (_carouselIndex < 0 || _carouselIndex >= _registry.AllAffinities.Count) return null;
            return _registry.AllAffinities[_carouselIndex];
        }

        private bool IsBrowsedAffinityLockedIn()
        {
            AffinityDefinition affinity = CurrentBrowsedAffinity();
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (affinity == null || selection == null) return false;

            return _activeSlotIsSecondary ? selection.Secondary == affinity : selection.Primary == affinity;
        }

        // ------------------------------------------------------------------
        // Refresh — content (carousel change)
        // ------------------------------------------------------------------

        private void RefreshBrowsedAffinity()
        {
            AffinityDefinition affinity = CurrentBrowsedAffinity();

            if (_browsedAffinityNameText != null)
                _browsedAffinityNameText.text = affinity != null ? affinity.DisplayName : "No Affinities";

            if (_browsedAffinityIcon != null)
            {
                Sprite icon = affinity != null ? affinity.Icon : null;
                _browsedAffinityIcon.sprite = icon;
                _browsedAffinityIcon.gameObject.SetActive(icon != null);
            }

            if (_affinityNode != null)
                _affinityNode.SetContent(affinity != null ? affinity.Icon : null, affinity != null ? affinity.DisplayName : "");

            for (int i = 0; i < _ultimateNodes.Length; i++)
            {
                if (_ultimateNodes[i] == null) continue;
                UltimateDefinition ultimate = GetUltimate(affinity, i);
                _ultimateNodes[i].SetContent(ultimate != null ? ultimate.Icon : null, ultimate != null ? ultimate.DisplayName : "");
            }

            for (int i = 0; i < _perkNodes.Length; i++)
            {
                if (_perkNodes[i] == null) continue;
                PerkDefinition perk = affinity != null ? affinity.GetPerk(i / 3, i % 3) : null;
                _perkNodes[i].SetContent(perk != null ? perk.Icon : null, perk != null ? perk.DisplayName : "");
            }

            // Browsing the carousel previews the AFFINITY itself, not its passive -
            // the passive is a specific thing the player has to click the top node
            // to inspect, same as any ultimate/perk. This only ever runs on a
            // carousel/slot change, never on a node click - see HandleAffinityNodeClicked
            // for the click-triggered passive inspect.
            if (affinity != null)
                _descriptionPanel?.Inspect(affinity.Icon, affinity.DisplayName, "");

            RefreshVisibilityState();
            RefreshTreeInteractability();
            RefreshSelectedHighlights();
            RefreshStatusText();
            RefreshSelectButtonLabel();
        }

        private static UltimateDefinition GetUltimate(AffinityDefinition affinity, int index)
        {
            if (affinity == null || affinity.Ultimates == null || index < 0 || index >= affinity.Ultimates.Count) return null;
            return affinity.Ultimates[index];
        }

        // ------------------------------------------------------------------
        // Refresh — interactability / highlights / status (selection change)
        // ------------------------------------------------------------------

        private void RefreshTreeInteractability()
        {
            bool locked = IsBrowsedAffinityLockedIn();

            if (_affinityNode != null)
                _affinityNode.SetInteractable(true);

            bool ultimatesAvailable = !_activeSlotIsSecondary;
            for (int i = 0; i < _ultimateNodes.Length; i++)
                _ultimateNodes[i]?.SetInteractable(locked && ultimatesAvailable);
            if (_ultimatesCanvasGroup != null)
                _ultimatesCanvasGroup.alpha = ultimatesAvailable ? 1f : 0.4f;

            for (int i = 0; i < _perkNodes.Length; i++)
            {
                int row = i / 3;
                bool rowAvailable = AffinityLoadoutRules.IsRowAvailable(row, _activeSlotIsSecondary);
                _perkNodes[i]?.SetInteractable(locked && rowAvailable);
            }

            if (_primaryOnlyRowCanvasGroup != null)
                _primaryOnlyRowCanvasGroup.alpha = AffinityLoadoutRules.IsRowAvailable(AffinityLoadoutRules.PrimaryOnlyRow, _activeSlotIsSecondary) ? 1f : 0.4f;
        }

        private void RefreshSelectedHighlights()
        {
            AffinityDefinition affinity = CurrentBrowsedAffinity();
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            bool locked = IsBrowsedAffinityLockedIn();

            _affinityNode?.SetSelected(locked);

            for (int i = 0; i < _ultimateNodes.Length; i++)
            {
                UltimateDefinition ultimate = GetUltimate(affinity, i);
                bool selected = locked && !_activeSlotIsSecondary && selection != null && ultimate != null && selection.Ultimate == ultimate;
                _ultimateNodes[i]?.SetSelected(selected);
            }

            for (int i = 0; i < _perkNodes.Length; i++)
            {
                int row = i / 3;
                int column = i % 3;
                PerkDefinition perk = affinity != null ? affinity.GetPerk(row, column) : null;
                bool selected = locked && selection != null && perk != null && selection.GetPerk(row, _activeSlotIsSecondary) == perk;
                _perkNodes[i]?.SetSelected(selected);
            }
        }

        private void RefreshStatusText()
        {
            if (_statusText == null) return;

            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            _statusText.text = selection != null && selection.IsComplete ? "Loadout Complete" : "Loadout Incomplete";
        }

        private void RefreshSelectButtonLabel()
        {
            // The Select row is only ever visible while the active slot has no
            // locked-in affinity yet (see RefreshVisibilityState), so there is no
            // "already selected" state to reflect here.
            if (_selectButtonLabel != null)
                _selectButtonLabel.text = "Select";
            if (_selectButton != null)
                _selectButton.interactable = CurrentBrowsedAffinity() != null;
        }

        private void RefreshCountdownText()
        {
            if (_countdownText == null) return;
            if (AffinitySelectCoordinator.Instance == null)
            {
                _countdownText.text = "";
                return;
            }

            _countdownText.text = AffinitySelectCoordinator.Instance.SecondsRemaining.ToString();
        }

        private void RefreshConfirmButton()
        {
            if (_confirmButton == null) return;

            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            bool complete = selection != null && selection.IsComplete;

            if (!_hasSubmitted)
            {
                _confirmButton.interactable = complete;
                if (_confirmButtonLabel != null)
                    _confirmButtonLabel.text = "Confirm Loadout";
            }
            else
            {
                _confirmButton.interactable = true;
                if (_confirmButtonLabel != null)
                {
                    int seconds = AffinitySelectCoordinator.Instance != null ? AffinitySelectCoordinator.Instance.SecondsRemaining : 0;
                    _confirmButtonLabel.text = $"Waiting for other players... ({seconds}s)\n<size=70%>Click to edit</size>";
                }
            }
        }

        // ------------------------------------------------------------------
        // Node clicks
        // ------------------------------------------------------------------

        private void HandleAffinityNodeClicked()
        {
            AffinityDefinition affinity = CurrentBrowsedAffinity();
            if (affinity == null) return;

            InspectAffinityPassive(affinity);
        }

        // Passives don't always have their own icon (older data predates the
        // field) - fall back to the parent affinity's icon rather than showing
        // a blank image.
        private void InspectAffinityPassive(AffinityDefinition affinity)
        {
            if (affinity.Passive != null)
            {
                Sprite icon = affinity.Passive.Icon != null ? affinity.Passive.Icon : affinity.Icon;
                _descriptionPanel?.Inspect(icon, affinity.Passive.DisplayName, affinity.Passive.Description);
            }
            else
            {
                _descriptionPanel?.Inspect(affinity.Icon, affinity.DisplayName, "");
            }
        }

        private void HandleUltimateClicked(int index)
        {
            AffinityDefinition affinity = CurrentBrowsedAffinity();
            UltimateDefinition ultimate = GetUltimate(affinity, index);
            if (ultimate == null) return;

            _descriptionPanel?.Inspect(ultimate.Icon, ultimate.DisplayName, ultimate.Description);

            if (!IsBrowsedAffinityLockedIn() || _activeSlotIsSecondary) return;

            LocalAffinitySelection.Instance?.SetUltimate(ultimate);
        }

        private void HandlePerkClicked(int index)
        {
            int row = index / 3;
            int column = index % 3;

            AffinityDefinition affinity = CurrentBrowsedAffinity();
            PerkDefinition perk = affinity != null ? affinity.GetPerk(row, column) : null;
            if (perk == null) return;

            _descriptionPanel?.Inspect(perk.Icon, perk.DisplayName, perk.Description);

            if (!IsBrowsedAffinityLockedIn() || !AffinityLoadoutRules.IsRowAvailable(row, _activeSlotIsSecondary)) return;

            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (selection == null) return;

            if (selection.GetPerk(row, _activeSlotIsSecondary) == perk)
                selection.ClearPerk(row, _activeSlotIsSecondary);
            else
                selection.SetPerk(perk, _activeSlotIsSecondary);
        }

        private void HandleSelectClicked()
        {
            AffinityDefinition affinity = CurrentBrowsedAffinity();
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (affinity == null || selection == null) return;

            if (_activeSlotIsSecondary)
                selection.SetSecondary(affinity);
            else
                selection.SetPrimary(affinity);
        }

        // Clears the active slot's affinity so the player can browse and pick
        // again. SelectionChanged -> RefreshVisibilityState is what flips the UI
        // back from tree+Deselect to carousel+Select.
        private void HandleDeselectClicked()
        {
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (selection == null) return;

            if (_activeSlotIsSecondary)
                selection.SetSecondary(null);
            else
                selection.SetPrimary(null);
        }

        private void HandleConfirmButtonClicked()
        {
            AffinitySelectCoordinator coordinator = AffinitySelectCoordinator.Instance;
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (coordinator == null || selection == null)
            {
                Debug.LogWarning($"[{nameof(AffinitySelectMenuUI)}] Cannot submit - coordinator or local selection not ready yet.", this);
                return;
            }

            if (!_hasSubmitted)
            {
                coordinator.CmdSubmitLoadout(AffinityLoadoutCodec.Encode(selection.GetSelection()));
                _hasSubmitted = true;
            }
            else
            {
                coordinator.CmdClearReady();
                _hasSubmitted = false;
            }

            RefreshConfirmButton();
        }

        // ------------------------------------------------------------------
        // LocalAffinitySelection / AffinitySelectCoordinator callbacks
        // ------------------------------------------------------------------

        private void HandleSelectionChanged()
        {
            RefreshVisibilityState();
            RefreshTreeInteractability();
            RefreshSelectedHighlights();
            RefreshStatusText();
            RefreshConfirmButton();
            RefreshSelectButtonLabel();
        }

        // FishNet activates scene NetworkObjects a beat later than plain scene
        // MonoBehaviours, so Instance can still be null the first time OnEnable
        // runs. InstanceReady calls this again once it's actually set - same
        // caveat LobbyPlayerList/LobbyMenuUI document.
        private void HandleCoordinatorReady()
        {
            if (AffinitySelectCoordinator.Instance == null) return;

            RefreshConfirmButton();
            RefreshCountdownText();
        }
    }
}
