// =============================================================================
// ScoreboardUI — Tab-held live scoreboard overlay.
//
// ARCHITECTURE:
//   Same CanvasGroup show/hide shape as PauseMenuUI, but driven by a
//   hold-to-view input (ScoreboardHoldStarted/Canceled) rather than a toggle,
//   and deliberately does NOT touch PlayerInputStateController - holding Tab
//   should not pause gameplay, lock the cursor, or hide the combat HUD, so
//   this stays a pure overlay on top of normal play.
//
//   Must live under the Player's live hierarchy (e.g. under HUD Canvas, as a
//   SIBLING of the combat HUD root, never a child of it - see MatchEndUI's
//   header for why child placement is a trap) so GetComponentInParent finds
//   the correct live instance, not the Player prefab asset.
//
// ROW LIST:
//   Same instantiate/track/clear idiom LobbyMenuUI.RebuildRows()/ClearRows()
//   already uses, just sourced from every currently spawned KillCount
//   (FindObjectsByType) instead of a SyncList of connection ids - there is no
//   equivalent networked "list of players" for an in-progress match, and for
//   this game's small player counts a scene-wide find on each Show() is
//   cheap enough not to need one. Subscribing to every found KillCount's
//   OnKillsChanged and reacting by rebuilding wholesale (rather than patching
//   one row) mirrors LobbyMenuUI's own "any change -> rebuild everything"
//   convention; unsubscribing happens inside ClearRows() before the fresh
//   subscriptions are made, which is safe even when called from inside one
//   of those same events (a multicast delegate invocation snapshots its
//   subscriber list before invoking, so this can't self-corrupt).
//
// AUTO-HIDE:
//   No manual PlayerInputState polling needed: Unity's Input System fires
//   `canceled` when an in-progress action is disabled out from under it, so
//   PlayerInputReader's own DisablePlayerMap/DisableAllMaps (entering Menu/
//   Paused/Dead state) already fires ScoreboardHoldCanceled and hides this
//   for free. DisableAllMaps deliberately keeps Scoreboard enabled during the
//   Dead state specifically, so Tab still works while waiting to respawn.
// =============================================================================

using System.Collections.Generic;
using OffAngle.Combat;
using OffAngle.Core;
using OffAngle.Networking;
using UnityEngine;

namespace OffAngle.UI.Match
{
    public class ScoreboardUI : MonoBehaviour
    {
        [Tooltip("CanvasGroup on this GameObject or a parent. Will be auto-added if missing.")]
        [SerializeField] private CanvasGroup _canvasGroup;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent. ScoreboardHoldStarted/Canceled (Tab) show/hide this overlay.\n\nDo NOT hand-assign this by dragging the Player prefab asset's PlayerInputReader in - that points at the static prefab asset, not the live instantiated player, and will silently never fire. This must live under the Player's hierarchy (e.g. under HUD Canvas) so the auto-resolve can find the correct live instance.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [SerializeField] private RectTransform _rowContainer;
        [SerializeField] private ScoreboardRowUI _rowPrefab;

        private readonly List<ScoreboardRowUI> _rows = new List<ScoreboardRowUI>();
        private readonly List<KillCount> _liveSubscriptions = new List<KillCount>();

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_inputReader == null)
                _inputReader = GetComponentInParent<PlayerInputReader>();

            if (_canvasGroup == null)
            {
                _canvasGroup = GetComponent<CanvasGroup>();
                if (_canvasGroup == null)
                    _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            SetVisible(false);
        }

        private void OnEnable()
        {
            if (_inputReader != null)
            {
                _inputReader.ScoreboardHoldStarted += Show;
                _inputReader.ScoreboardHoldCanceled += Hide;
            }
        }

        private void OnDisable()
        {
            if (_inputReader != null)
            {
                _inputReader.ScoreboardHoldStarted -= Show;
                _inputReader.ScoreboardHoldCanceled -= Hide;
            }

            Hide();
        }

        // ------------------------------------------------------------------
        // Show / hide
        // ------------------------------------------------------------------

        private void Show()
        {
            EnsureScales();
            RebuildRows();
            SetVisible(true);
        }

        private void Hide()
        {
            SetVisible(false);
            ClearRows();
        }

        private void SetVisible(bool visible)
        {
            if (_canvasGroup == null) return;

            _canvasGroup.alpha = visible ? 1f : 0f;
            _canvasGroup.interactable = visible;
            _canvasGroup.blocksRaycasts = visible;
        }

        // ------------------------------------------------------------------
        // Row list
        // ------------------------------------------------------------------

        private void RebuildRows()
        {
            ClearRows();

            if (_rowPrefab == null || _rowContainer == null) return;

            KillCount[] players = FindObjectsByType<KillCount>(FindObjectsSortMode.None);
            System.Array.Sort(players, (a, b) => b.Kills.CompareTo(a.Kills));

            foreach (KillCount kc in players)
            {
                ScoreboardRowUI row = Instantiate(_rowPrefab, _rowContainer);
                row.SetRow(LabelFor(kc), kc.Kills);
                _rows.Add(row);

                kc.OnKillsChanged += HandleAnyKillsChanged;
                _liveSubscriptions.Add(kc);
            }
        }

        private void ClearRows()
        {
            foreach (KillCount kc in _liveSubscriptions)
            {
                if (kc != null)
                    kc.OnKillsChanged -= HandleAnyKillsChanged;
            }
            _liveSubscriptions.Clear();

            foreach (ScoreboardRowUI row in _rows)
            {
                if (row != null)
                    Destroy(row.gameObject);
            }
            _rows.Clear();
        }

        // A kill anywhere invalidates the sort order, not just one row's text -
        // rebuilding wholesale is simpler and, at this game's player counts,
        // cheap enough not to need incremental patching.
        private void HandleAnyKillsChanged(int _) => RebuildRows();

        // Same fix PlayerInputStateController.EnsureCombatHudScales() applies
        // to the combat HUD root - a RectTransform nested under an
        // initially-hidden ancestor can get its localScale baked to zero by
        // Unity. This panel has no nested Canvas of its own (unlike
        // MatchEndUI's), so it's less exposed to that specific trigger, but
        // the check is cheap enough to run defensively on every Show() too.
        private void EnsureScales()
        {
            RectTransform[] rects = GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rt = rects[i];
                if (rt.localScale == Vector3.zero)
                    rt.localScale = Vector3.one;
            }
        }

        private static string LabelFor(KillCount kc)
        {
            int clientId = kc.Owner != null ? kc.Owner.ClientId : -1;
            return LobbyPlayerList.LabelFor(clientId);
        }
    }
}
