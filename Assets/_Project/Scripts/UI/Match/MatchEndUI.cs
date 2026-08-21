// =============================================================================
// MatchEndUI — win/lose screen shown once MatchManager declares a winner.
//
// ARCHITECTURE:
//   Same overlay shape as PauseMenuUI/WeaponSelectionMenu: EnterMenuState()
//   BEFORE showing the panel (unlocks cursor / enables UI map so the first
//   button click works). The panel itself is a plain SetActive(true)/false,
//   same convention as DeathScreenController's panel - there's no toggling
//   back and forth needed, since once the match ends there is nothing to
//   un-show until the next match.
//
//   Binds to MatchManager via BOTH an immediate Instance check and
//   InstanceReady, guarded so it only ever binds once - same defensive
//   double-path LobbyMenuUI uses for LobbyPlayerList, though here the race
//   almost always resolves before this ever runs (MatchManager is a Game-
//   scene object that loads before the Player prefab spawns into it).
//
// CRITICAL PLACEMENT NOTE:
//   This must live under HUD Canvas as a SIBLING of the combat HUD root
//   (_combatHudRoot on PlayerInputStateController), never a child of it.
//   EnterMenuState() calls SetCombatHudVisible(false), which SetActive(false)s
//   that root - if this panel were nested inside it, showing the result
//   screen would deactivate itself in the same method call that was supposed
//   to display it. PauseMenuUI avoids this today by being a sibling; mirror
//   that placement here.
//
// RESPAWN RACE:
//   Respawner.RespawnRoutine bails out once MatchManager.HasEnded, so a
//   player who was mid-countdown when the match ended will not pop back into
//   gameplay underneath this screen - see that guard for details. Without
//   it, EnterMenuState()'s lock here would get silently undone a few seconds
//   later by RpcOnRespawned's own EnterGameplayState() call.
// =============================================================================

using System.Collections.Generic;
using OffAngle.Combat;
using OffAngle.Core;
using OffAngle.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Match
{
    public class MatchEndUI : MonoBehaviour
    {
        [Tooltip("The result panel this controller shows. Should start INACTIVE in the prefab, same convention as the Death Screen panel.")]
        [SerializeField] private GameObject _panel;

        [SerializeField] private TMP_Text _resultText;
        [SerializeField] private RectTransform _rowContainer;
        [SerializeField] private ScoreboardRowUI _rowPrefab;
        [SerializeField] private Button _returnToLobbyButton;
        [SerializeField] private Button _leaveToMainMenuButton;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent.\n\nDo NOT hand-assign this by dragging the Player prefab asset's component in - that points at the static prefab asset, not the live instantiated player, and will silently never fire.")]
        [SerializeField] private PlayerInputStateController _stateController;

        [Tooltip("Leave null to auto-resolve via FindFirstObjectByType. Lives on the persistent NetworkManager GameObject, a different scene than this prefab, so it cannot be Inspector-dragged.")]
        [SerializeField] private NetworkMenuController _networkMenuController;

        private readonly List<ScoreboardRowUI> _rows = new List<ScoreboardRowUI>();
        private bool _boundToManager;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_stateController == null)
                _stateController = GetComponentInParent<PlayerInputStateController>();

            if (_networkMenuController == null)
                _networkMenuController = FindFirstObjectByType<NetworkMenuController>();

            if (_returnToLobbyButton != null)
                _returnToLobbyButton.onClick.AddListener(OnReturnToLobbyClicked);
            if (_leaveToMainMenuButton != null)
                _leaveToMainMenuButton.onClick.AddListener(OnLeaveToMainMenuClicked);

            if (_panel != null)
                _panel.SetActive(false);
        }

        private void OnEnable()
        {
            MatchManager.InstanceReady += TryBindToManager;
            TryBindToManager();
        }

        private void OnDisable()
        {
            MatchManager.InstanceReady -= TryBindToManager;

            if (_boundToManager && MatchManager.Instance != null)
                MatchManager.Instance.OnMatchEnded -= HandleMatchEnded;
            _boundToManager = false;
        }

        private void OnDestroy()
        {
            if (_returnToLobbyButton != null)
                _returnToLobbyButton.onClick.RemoveListener(OnReturnToLobbyClicked);
            if (_leaveToMainMenuButton != null)
                _leaveToMainMenuButton.onClick.RemoveListener(OnLeaveToMainMenuClicked);
        }

        private void TryBindToManager()
        {
            if (_boundToManager || MatchManager.Instance == null) return;

            _boundToManager = true;
            MatchManager.Instance.OnMatchEnded += HandleMatchEnded;
        }

        // ------------------------------------------------------------------
        // Match end
        // ------------------------------------------------------------------

        private void HandleMatchEnded(int winnerClientId)
        {
            if (_stateController != null)
                _stateController.EnterMenuState();

            if (_resultText != null)
            {
                bool won = MatchManager.Instance != null && MatchManager.Instance.LocalPlayerWon;
                _resultText.text = won ? "Victory" : "Defeat";
            }

            if (_panel != null)
                _panel.SetActive(true);

            BuildFinalTally();

            if (_returnToLobbyButton != null)
                _returnToLobbyButton.interactable = MatchManager.Instance != null && MatchManager.Instance.LocalIsHost;
        }

        private void BuildFinalTally()
        {
            ClearRows();

            if (_rowPrefab == null || _rowContainer == null) return;

            KillCount[] players = FindObjectsByType<KillCount>(FindObjectsSortMode.None);
            System.Array.Sort(players, (a, b) => b.Kills.CompareTo(a.Kills));

            foreach (KillCount kc in players)
            {
                int clientId = kc.Owner != null ? kc.Owner.ClientId : -1;
                ScoreboardRowUI row = Instantiate(_rowPrefab, _rowContainer);
                row.SetRow(LobbyPlayerList.LabelFor(clientId), kc.Kills);
                _rows.Add(row);
            }
        }

        private void ClearRows()
        {
            foreach (ScoreboardRowUI row in _rows)
            {
                if (row != null)
                    Destroy(row.gameObject);
            }
            _rows.Clear();
        }

        // ------------------------------------------------------------------
        // Button handlers (also wired here in code; either works from the
        // Inspector's OnClick too, but AddListener means it always fires)
        // ------------------------------------------------------------------

        private void OnReturnToLobbyClicked() => GameFlowController.Instance?.RequestReturnToLobby();

        private void OnLeaveToMainMenuClicked()
        {
            // Available to every player, not just the host - Return to Lobby
            // above is host-gated, so without this a non-host would have no
            // way to leave at all (EnterMenuState() means Esc/Pause does
            // nothing, per PauseMenuUI.HandlePauseToggle's own gameplay-state
            // check).
            if (_networkMenuController == null)
                _networkMenuController = FindFirstObjectByType<NetworkMenuController>();

            if (_networkMenuController != null)
                _networkMenuController.LeaveSession();
            else
                Debug.LogWarning($"[{nameof(MatchEndUI)}] No {nameof(NetworkMenuController)} found; cannot leave the session.", this);
        }
    }
}
