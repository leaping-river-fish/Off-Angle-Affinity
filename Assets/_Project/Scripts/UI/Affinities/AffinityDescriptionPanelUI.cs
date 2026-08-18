// =============================================================================
// AffinityDescriptionPanelUI — the right-hand 25% panel: enlarged icon, name,
// then description. Pure display sink with no click handling of its own -
// AffinitySelectMenuUI calls Inspect() whenever any tree node is clicked.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Affinities
{
    public class AffinityDescriptionPanelUI : MonoBehaviour
    {
        [SerializeField] private Image _iconImage;
        [SerializeField] private TMP_Text _nameText;
        [SerializeField] private TMP_Text _descriptionText;

        /// <summary>Shows a node's details. Pass a null sprite to hide the icon.</summary>
        public void Inspect(Sprite icon, string displayName, string description)
        {
            if (_iconImage != null)
            {
                if (icon != null)
                {
                    _iconImage.sprite = icon;
                    _iconImage.gameObject.SetActive(true);
                }
                else
                {
                    _iconImage.gameObject.SetActive(false);
                }
            }

            if (_nameText != null)
                _nameText.text = displayName;

            if (_descriptionText != null)
                _descriptionText.text = description;
        }

        /// <summary>Blanks the panel. Call once before anything's been clicked so it isn't stale.</summary>
        public void Clear()
        {
            if (_iconImage != null)
                _iconImage.gameObject.SetActive(false);
            if (_nameText != null)
                _nameText.text = "";
            if (_descriptionText != null)
                _descriptionText.text = "";
        }
    }
}
