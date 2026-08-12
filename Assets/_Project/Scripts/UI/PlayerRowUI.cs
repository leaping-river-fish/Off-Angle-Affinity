// =============================================================================
// PlayerRowUI — one row in the lobby's scrollable player list.
// Attach to the "Player Row" prefab; LobbyMenuUI instantiates one per
// connected player and calls SetLabel.
// =============================================================================

using TMPro;
using UnityEngine;

namespace OffAngle.UI
{
    public class PlayerRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text _nameText;

        public void SetLabel(string label)
        {
            if (_nameText != null)
                _nameText.text = label;
        }
    }
}
