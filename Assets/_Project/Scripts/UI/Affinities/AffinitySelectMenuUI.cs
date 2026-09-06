// =============================================================================
// AffinitySelectMenuUI — controller for the "Affinity Select Menu" prefab.
//
// ARCHITECTURE:
//   One page, two columns. Path-icon arrays (registry order) pick Primary and
//   Secondary immediately. Each column owns its own tree nodes. Ultimate/perk
//   clicks inspect first, then mutate LocalAffinitySelection only when that
//   column already has an affinity.
//
//   This is the only script that talks to LocalAffinitySelection /
//   AffinitySelectCoordinator; AffinityTreeNodeUI and AffinityDescriptionPanelUI
//   are dumb display/click components with no knowledge of either.
//
// SECONDARY:
//   Cannot be picked until Primary is set. The path icon whose registry index
//   matches Primary is hidden, not greyed. The secondary tree shows perk rows
//   0 and 2 only (AffinityLoadoutRules.PrimaryOnlyRow and ultimates are omitted
//   from the hierarchy entirely).
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
        [Header("Status")]
        [SerializeField] private TMP_Text _statusText;
        [SerializeField] private TMP_Text _countdownText;

        [Header("Primary Path")]
        [SerializeField] private AffinityTreeNodeUI _primarySelectedNode;
        [Tooltip("Size 6, same order as AffinityRegistry.AllAffinities.")]
        [SerializeField] private AffinityTreeNodeUI[] _primaryPathIcons = new AffinityTreeNodeUI[6];

        [Header("Secondary Path")]
        [SerializeField] private AffinityTreeNodeUI _secondarySelectedNode;
        [Tooltip("Size 6, same order as AffinityRegistry.AllAffinities. The primary's index is hidden at runtime.")]
        [SerializeField] private AffinityTreeNodeUI[] _secondaryPathIcons = new AffinityTreeNodeUI[6];

        [Header("Primary Tree")]
        [SerializeField] private GameObject _primaryTreeRoot;
        [SerializeField] private GameObject _primaryEmptyPrompt;
        [Tooltip("Expected size 3, in the same order as AffinityDefinition.Ultimates.")]
        [SerializeField] private AffinityTreeNodeUI[] _primaryUltimateNodes = new AffinityTreeNodeUI[3];
        [Tooltip("Expected size 9, row-major: index = row * 3 + column.")]
        [SerializeField] private AffinityTreeNodeUI[] _primaryPerkNodes = new AffinityTreeNodeUI[9];

        [Header("Secondary Tree")]
        [SerializeField] private GameObject _secondaryTreeRoot;
        [Tooltip("Shown until a primary affinity is picked.")]
        [SerializeField] private GameObject _secondaryLockedPrompt;
        [Tooltip("Shown after primary is picked, until a secondary affinity is picked.")]
        [SerializeField] private GameObject _secondaryEmptyPrompt;
        [Tooltip("Expected size 6: 0-2 = perk row 1 (index 0), 3-5 = perk row 3 (index 2).")]
        [SerializeField] private AffinityTreeNodeUI[] _secondaryPerkNodes = new AffinityTreeNodeUI[6];

        [Header("Primary Passive")]
        [SerializeField] private AffinityTreeNodeUI _primaryPassiveNode;

        [Header("Confirm / Ready")]
        [SerializeField] private Button _confirmButton;
        [SerializeField] private TMP_Text _confirmButtonLabel;

        [Header("Description Panel")]
        [SerializeField] private AffinityDescriptionPanelUI _descriptionPanel;

        private AffinityRegistry _registry;
        private bool _hasSubmitted;
        private int _lastDisplayedSeconds = -1;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            BindNode(_primarySelectedNode, HandlePrimarySelectedNodeClicked);
            BindNode(_secondarySelectedNode, HandleSecondarySelectedNodeClicked);
            BindNode(_primaryPassiveNode, HandlePrimaryPassiveClicked);

            BindPathIcons(_primaryPathIcons, asSecondary: false);
            BindPathIcons(_secondaryPathIcons, asSecondary: true);

            for (int i = 0; i < _primaryUltimateNodes.Length; i++)
            {
                int index = i;
                BindNode(_primaryUltimateNodes[i], () => HandlePrimaryUltimateClicked(index));
            }

            for (int i = 0; i < _primaryPerkNodes.Length; i++)
            {
                int index = i;
                BindNode(_primaryPerkNodes[i], () => HandlePrimaryPerkClicked(index));
            }

            for (int i = 0; i < _secondaryPerkNodes.Length; i++)
            {
                int index = i;
                BindNode(_secondaryPerkNodes[i], () => HandleSecondaryPerkClicked(index));
            }

            if (_confirmButton != null)
                _confirmButton.onClick.AddListener(HandleConfirmButtonClicked);

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

            RefreshAll();
        }

        private void OnDisable()
        {
            if (LocalAffinitySelection.Instance != null)
                LocalAffinitySelection.Instance.SelectionChanged -= HandleSelectionChanged;

            AffinitySelectCoordinator.InstanceReady -= HandleCoordinatorReady;
        }

        private void OnDestroy()
        {
            if (_confirmButton != null)
                _confirmButton.onClick.RemoveListener(HandleConfirmButtonClicked);
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

        private static void BindNode(AffinityTreeNodeUI node, System.Action handler)
        {
            if (node != null)
                node.Clicked += handler;
        }

        private void BindPathIcons(AffinityTreeNodeUI[] icons, bool asSecondary)
        {
            if (icons == null) return;

            for (int i = 0; i < icons.Length; i++)
            {
                int index = i;
                BindNode(icons[i], () => HandlePathIconClicked(index, asSecondary));
            }
        }

        // ------------------------------------------------------------------
        // Refresh
        // ------------------------------------------------------------------

        private void RefreshAll()
        {
            RefreshPathIcons();
            RefreshSelectedAffinityDisplays();
            RefreshPrimaryTreeContent();
            RefreshSecondaryTreeContent();
            RefreshPassiveNode();
            RefreshVisibilityState();
            RefreshSelectedHighlights();
            RefreshStatusText();
            RefreshConfirmButton();
            RefreshCountdownText();
        }

        private void RefreshPathIcons()
        {
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            AffinityDefinition primary = selection != null ? selection.Primary : null;
            bool hasPrimary = primary != null;

            RefreshPathIconRow(_primaryPathIcons, alwaysInteractable: true, hideIndex: -1);
            RefreshPathIconRow(_secondaryPathIcons, alwaysInteractable: hasPrimary, hideIndex: IndexOf(primary));
        }

        private void RefreshPathIconRow(AffinityTreeNodeUI[] icons, bool alwaysInteractable, int hideIndex)
        {
            if (icons == null) return;

            int count = AffinityCount();
            for (int i = 0; i < icons.Length; i++)
            {
                if (icons[i] == null) continue;

                AffinityDefinition affinity = GetAffinity(i);
                bool inRange = i < count && affinity != null;
                bool hidden = inRange && i == hideIndex;

                icons[i].gameObject.SetActive(inRange && !hidden);
                if (!inRange || hidden) continue;

                icons[i].SetContent(affinity.Icon, affinity.DisplayName);
                icons[i].SetInteractable(alwaysInteractable);
            }
        }

        private void RefreshSelectedAffinityDisplays()
        {
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            SetAffinityDisplay(_primarySelectedNode, selection != null ? selection.Primary : null);
            SetAffinityDisplay(_secondarySelectedNode, selection != null ? selection.Secondary : null);
        }

        private static void SetAffinityDisplay(AffinityTreeNodeUI node, AffinityDefinition affinity)
        {
            if (node == null) return;

            if (affinity != null)
            {
                node.SetContent(affinity.Icon, affinity.DisplayName);
                node.SetSelected(true);
            }
            else
            {
                node.SetContent(null, "");
                node.SetSelected(false);
            }

            node.SetInteractable(true);
        }

        private void RefreshPrimaryTreeContent()
        {
            AffinityDefinition affinity = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Primary
                : null;

            for (int i = 0; i < _primaryUltimateNodes.Length; i++)
            {
                if (_primaryUltimateNodes[i] == null) continue;
                UltimateDefinition ultimate = GetUltimate(affinity, i);
                _primaryUltimateNodes[i].SetContent(
                    ultimate != null ? ultimate.Icon : null,
                    ultimate != null ? ultimate.DisplayName : "");
                _primaryUltimateNodes[i].SetInteractable(affinity != null && ultimate != null);
            }

            for (int i = 0; i < _primaryPerkNodes.Length; i++)
            {
                if (_primaryPerkNodes[i] == null) continue;
                PerkDefinition perk = affinity != null ? affinity.GetPerk(i / 3, i % 3) : null;
                _primaryPerkNodes[i].SetContent(
                    perk != null ? perk.Icon : null,
                    perk != null ? perk.DisplayName : "");
                _primaryPerkNodes[i].SetInteractable(affinity != null && perk != null);
            }
        }

        private void RefreshSecondaryTreeContent()
        {
            AffinityDefinition affinity = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Secondary
                : null;

            for (int i = 0; i < _secondaryPerkNodes.Length; i++)
            {
                if (_secondaryPerkNodes[i] == null) continue;

                SecondaryPerkSlot(i, out int row, out int column);
                PerkDefinition perk = affinity != null ? affinity.GetPerk(row, column) : null;
                _secondaryPerkNodes[i].SetContent(
                    perk != null ? perk.Icon : null,
                    perk != null ? perk.DisplayName : "");
                _secondaryPerkNodes[i].SetInteractable(affinity != null && perk != null);
            }
        }

        private void RefreshPassiveNode()
        {
            if (_primaryPassiveNode == null) return;

            AffinityDefinition primary = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Primary
                : null;
            AffinityPassive passive = primary != null ? primary.Passive : null;

            if (passive != null)
            {
                Sprite icon = passive.Icon != null ? passive.Icon : primary.Icon;
                _primaryPassiveNode.SetContent(icon, passive.DisplayName);
            }
            else
            {
                _primaryPassiveNode.SetContent(null, "");
            }

            _primaryPassiveNode.SetSelected(false);
            _primaryPassiveNode.SetInteractable(true);
        }

        private void RefreshVisibilityState()
        {
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            bool hasPrimary = selection != null && selection.Primary != null;
            bool hasSecondary = selection != null && selection.Secondary != null;

            if (_primaryEmptyPrompt != null)
                _primaryEmptyPrompt.SetActive(!hasPrimary);
            SetTreeActive(_primaryTreeRoot, hasPrimary);

            if (_secondaryLockedPrompt != null)
                _secondaryLockedPrompt.SetActive(!hasPrimary);
            if (_secondaryEmptyPrompt != null)
                _secondaryEmptyPrompt.SetActive(hasPrimary && !hasSecondary);
            SetTreeActive(_secondaryTreeRoot, hasSecondary);
        }

        private static void SetTreeActive(GameObject treeRoot, bool active)
        {
            if (treeRoot == null) return;

            treeRoot.SetActive(active);
            if (!active) return;

            // Unity doesn't reliably re-run nested Layout Group / Content Size
            // Fitter passes the moment a previously-inactive hierarchy is
            // reactivated - without forcing it, Content can be left at a stale
            // (often zero) size.
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)treeRoot.transform);
        }

        private void RefreshSelectedHighlights()
        {
            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            AffinityDefinition primary = selection != null ? selection.Primary : null;
            AffinityDefinition secondary = selection != null ? selection.Secondary : null;

            HighlightPathIcons(_primaryPathIcons, primary);
            HighlightPathIcons(_secondaryPathIcons, secondary);

            for (int i = 0; i < _primaryUltimateNodes.Length; i++)
            {
                UltimateDefinition ultimate = GetUltimate(primary, i);
                bool selected = primary != null && selection != null && ultimate != null && selection.Ultimate == ultimate;
                _primaryUltimateNodes[i]?.SetSelected(selected);
            }

            for (int i = 0; i < _primaryPerkNodes.Length; i++)
            {
                int row = i / 3;
                int column = i % 3;
                PerkDefinition perk = primary != null ? primary.GetPerk(row, column) : null;
                bool selected = selection != null && perk != null && selection.GetPerk(row, asSecondary: false) == perk;
                _primaryPerkNodes[i]?.SetSelected(selected);
            }

            for (int i = 0; i < _secondaryPerkNodes.Length; i++)
            {
                SecondaryPerkSlot(i, out int row, out int column);
                PerkDefinition perk = secondary != null ? secondary.GetPerk(row, column) : null;
                bool selected = selection != null && perk != null && selection.GetPerk(row, asSecondary: true) == perk;
                _secondaryPerkNodes[i]?.SetSelected(selected);
            }
        }

        private void HighlightPathIcons(AffinityTreeNodeUI[] icons, AffinityDefinition selected)
        {
            if (icons == null) return;

            for (int i = 0; i < icons.Length; i++)
                icons[i]?.SetSelected(selected != null && GetAffinity(i) == selected);
        }

        private void RefreshStatusText()
        {
            if (_statusText == null) return;

            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            _statusText.text = selection != null && selection.IsComplete ? "Loadout Complete" : "Loadout Incomplete";
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
        // Clicks
        // ------------------------------------------------------------------

        private void HandlePathIconClicked(int index, bool asSecondary)
        {
            AffinityDefinition affinity = GetAffinity(index);
            if (affinity == null) return;

            InspectAffinityPreview(affinity);

            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (selection == null) return;

            if (asSecondary)
            {
                if (selection.Primary == null || affinity == selection.Primary) return;
                if (selection.Secondary == affinity) return;
                selection.SetSecondary(affinity);
            }
            else
            {
                if (selection.Primary == affinity) return;
                selection.SetPrimary(affinity);
            }
        }

        private void HandlePrimarySelectedNodeClicked()
        {
            AffinityDefinition primary = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Primary
                : null;
            if (primary != null)
                InspectAffinityPreview(primary);
        }

        private void HandleSecondarySelectedNodeClicked()
        {
            AffinityDefinition secondary = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Secondary
                : null;
            if (secondary != null)
                InspectAffinityPreview(secondary);
        }

        private void HandlePrimaryPassiveClicked()
        {
            AffinityDefinition primary = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Primary
                : null;
            if (primary != null)
                InspectAffinityPassive(primary);
        }

        private void HandlePrimaryUltimateClicked(int index)
        {
            AffinityDefinition affinity = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Primary
                : null;
            UltimateDefinition ultimate = GetUltimate(affinity, index);
            if (ultimate == null) return;

            _descriptionPanel?.Inspect(ultimate.Icon, ultimate.DisplayName, ultimate.Description);

            if (affinity == null) return;
            LocalAffinitySelection.Instance?.SetUltimate(ultimate);
        }

        private void HandlePrimaryPerkClicked(int index)
        {
            int row = index / 3;
            int column = index % 3;

            AffinityDefinition affinity = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Primary
                : null;
            PerkDefinition perk = affinity != null ? affinity.GetPerk(row, column) : null;
            if (perk == null) return;

            _descriptionPanel?.Inspect(perk.Icon, perk.DisplayName, perk.Description);

            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (selection == null || affinity == null) return;
            if (!AffinityLoadoutRules.IsRowAvailable(row, asSecondary: false)) return;

            if (selection.GetPerk(row, asSecondary: false) == perk)
                selection.ClearPerk(row, asSecondary: false);
            else
                selection.SetPerk(perk, asSecondary: false);
        }

        private void HandleSecondaryPerkClicked(int index)
        {
            SecondaryPerkSlot(index, out int row, out int column);

            AffinityDefinition affinity = LocalAffinitySelection.Instance != null
                ? LocalAffinitySelection.Instance.Secondary
                : null;
            PerkDefinition perk = affinity != null ? affinity.GetPerk(row, column) : null;
            if (perk == null) return;

            _descriptionPanel?.Inspect(perk.Icon, perk.DisplayName, perk.Description);

            LocalAffinitySelection selection = LocalAffinitySelection.Instance;
            if (selection == null || affinity == null) return;
            if (!AffinityLoadoutRules.IsRowAvailable(row, asSecondary: true)) return;

            if (selection.GetPerk(row, asSecondary: true) == perk)
                selection.ClearPerk(row, asSecondary: true);
            else
                selection.SetPerk(perk, asSecondary: true);
        }

        private void InspectAffinityPreview(AffinityDefinition affinity)
        {
            _descriptionPanel?.Inspect(affinity.Icon, affinity.DisplayName, "");
        }

        private void InspectAffinityPassive(AffinityDefinition affinity)
        {
            if (affinity.Passive != null)
            {
                Sprite icon = affinity.Passive.Icon != null ? affinity.Passive.Icon : affinity.Icon;
                _descriptionPanel?.Inspect(icon, affinity.Passive.DisplayName, affinity.Passive.Description);
            }
            else
            {
                InspectAffinityPreview(affinity);
            }
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
        // Callbacks
        // ------------------------------------------------------------------

        private void HandleSelectionChanged()
        {
            RefreshAll();
        }

        private void HandleCoordinatorReady()
        {
            if (AffinitySelectCoordinator.Instance == null) return;

            RefreshConfirmButton();
            RefreshCountdownText();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private int AffinityCount()
        {
            if (_registry == null || _registry.AllAffinities == null) return 0;
            return _registry.AllAffinities.Count;
        }

        private AffinityDefinition GetAffinity(int index)
        {
            if (_registry == null || _registry.AllAffinities == null) return null;
            if (index < 0 || index >= _registry.AllAffinities.Count) return null;
            return _registry.AllAffinities[index];
        }

        private int IndexOf(AffinityDefinition affinity)
        {
            if (affinity == null || _registry == null || _registry.AllAffinities == null) return -1;
            return _registry.AllAffinities.IndexOf(affinity);
        }

        private static UltimateDefinition GetUltimate(AffinityDefinition affinity, int index)
        {
            if (affinity == null || affinity.Ultimates == null || index < 0 || index >= affinity.Ultimates.Count)
                return null;
            return affinity.Ultimates[index];
        }

        private static void SecondaryPerkSlot(int index, out int row, out int column)
        {
            column = index % 3;
            row = index < 3 ? 0 : 2;
        }

    }
}
