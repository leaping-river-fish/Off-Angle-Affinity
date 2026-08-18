// =============================================================================
// AffinityTreeNodeUI — one selectable slot in the skill tree (the affinity
// node, an ultimate node, or a perk node). Reused for all three: it holds no
// definition reference of its own, just an icon/label/button/highlight. The
// owning AffinitySelectMenuUI knows which definition maps to which node
// instance (fixed position in the tree) and pushes content in via SetContent.
//
// INSPECT-EVEN-WHEN-LOCKED:
//   OnPointerClick fires Clicked unconditionally, the same way
//   WeaponChoiceUI's backup path does, regardless of Button.interactable.
//   This is deliberate: a node the player hasn't unlocked yet (an affinity not
//   yet Selected, or an Ultimate/perk row locked out for Secondary) still
//   needs to be inspectable on the description panel even though it must not
//   register a pick. AffinitySelectMenuUI is what decides whether a click
//   mutates LocalAffinitySelection - never gate that decision by adding an
//   interactable check here.
// =============================================================================

using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OffAngle.UI.Affinities
{
    public class AffinityTreeNodeUI : MonoBehaviour, IPointerClickHandler
    {
        [Header("Widgets")]
        [SerializeField] private Button _button;

        [Tooltip("Optional icon image. Hidden when SetContent is given a null sprite.")]
        [SerializeField] private Image _iconImage;

        [Tooltip("Optional name label. Not every node needs one (e.g. the top affinity node may rely on the carousel's own name text instead).")]
        [SerializeField] private TMP_Text _nameLabel;

        [Tooltip("Shown while this node is the player's current pick. Leave null to skip the highlight.")]
        [SerializeField] private GameObject _selectedIndicator;

        /// <summary>Raised on click, regardless of interactable state. See the class header.</summary>
        public event Action Clicked;

        private bool _clickHandledThisFrame;

        private void Awake()
        {
            if (_button == null)
                _button = GetComponent<Button>();

            EnsureRaycastTarget();

            if (_button != null)
                _button.onClick.AddListener(HandleClicked);

            SetSelected(false);
        }

        private void LateUpdate()
        {
            _clickHandledThisFrame = false;
        }

        private void OnDestroy()
        {
            if (_button != null)
                _button.onClick.RemoveListener(HandleClicked);
        }

        /// <summary>
        /// Buttons need a Graphic with Raycast Target enabled or clicks never hit.
        /// Mirrors WeaponChoiceUI.EnsureRaycastTarget - the same "Target Graphic =
        /// None" bug class applies to every node prefab built from this component.
        /// </summary>
        private void EnsureRaycastTarget()
        {
            Image background = null;
            Transform bg = transform.Find("Background");
            if (bg != null)
                background = bg.GetComponent<Image>();

            if (background != null)
            {
                background.raycastTarget = true;
                if (_button != null && _button.targetGraphic == null)
                    _button.targetGraphic = background;
            }
            else if (_button != null && _button.targetGraphic == null)
            {
                Image hitArea = gameObject.GetComponent<Image>();
                if (hitArea == null)
                    hitArea = gameObject.AddComponent<Image>();
                hitArea.color = new Color(1f, 1f, 1f, 0f);
                hitArea.raycastTarget = true;
                _button.targetGraphic = hitArea;
            }

            if (_nameLabel != null)
                _nameLabel.raycastTarget = false;
            if (_iconImage != null)
                _iconImage.raycastTarget = false;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // Backup path if Button.onClick fails to fire (e.g. Button.interactable
            // is false because the node is locked/dimmed) - see class header.
            HandleClicked();
        }

        private void HandleClicked()
        {
            // Button.onClick and IPointerClickHandler can both fire for one click.
            if (_clickHandledThisFrame) return;
            _clickHandledThisFrame = true;

            Clicked?.Invoke();
        }

        /// <summary>Sets the icon and label. Pass a null sprite to hide the icon image entirely.</summary>
        public void SetContent(Sprite icon, string displayName)
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

            if (_nameLabel != null)
                _nameLabel.text = displayName;
        }

        /// <summary>Toggles the selected-state indicator.</summary>
        public void SetSelected(bool selected)
        {
            if (_selectedIndicator != null)
                _selectedIndicator.SetActive(selected);
        }

        /// <summary>Grays the node out / re-enables it. Does not affect whether Clicked fires - see class header.</summary>
        public void SetInteractable(bool interactable)
        {
            if (_button != null)
                _button.interactable = interactable;
        }
    }
}
