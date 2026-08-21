// =============================================================================
// ScoreboardRowUI — one row in the Tab-held scoreboard or the match-end final
// tally. Attach to the "Scoreboard Row" prefab; ScoreboardUI/MatchEndUI each
// instantiate one per player and call SetRow.
//
// Standalone sibling of PlayerRowUI (the Lobby's connection-list row), not a
// subclass or edit of it - PlayerRowUI stays exactly as LobbyMenuUI already
// depends on it; this one carries a second column (kills) that row has no
// use for.
// =============================================================================

using TMPro;
using UnityEngine;

namespace OffAngle.UI.Match
{
    public class ScoreboardRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text _nameText;
        [SerializeField] private TMP_Text _killsText;

        public void SetRow(string label, int kills)
        {
            if (_nameText != null)
                _nameText.text = label;
            if (_killsText != null)
                _killsText.text = kills.ToString();
        }
    }
}
