// =============================================================================
// WeaponChoiceUI — one selectable weapon entry. Reused for every weapon by
// assigning a different WeaponDefinition per prefab instance in the
// Inspector; no per-weapon script or code change required.
// =============================================================================

using System;
using OffAngle.Weapons;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OffAngle.UI.Weapons
{
    public class WeaponChoiceUI : MonoBehaviour, IPointerClickHandler
    {
        [Header("Data")]
        [Tooltip("The weapon this choice represents. Assign a different WeaponDefinition per instance - this is the only per-weapon setup required.")]
        [SerializeField] private WeaponDefinition _definition;

        [Header("Widgets")]
        [SerializeField] private TMP_Text _nameLabel;
        [SerializeField] private Button _button;

        [Header("Optional")]
        [Tooltip("Optional icon image. Shows the weapon's icon sprite if assigned in the WeaponDefinition. Leave null to skip icon display.")]
        [SerializeField] private Image _iconImage;

        [Tooltip("Shown while this choice is the currently-selected weapon for its category. Leave null to skip the highlight.")]
        [SerializeField] private GameObject _selectedIndicator;

        public WeaponDefinition Definition => _definition;

        /// <summary>Raised when the button is clicked. WeaponSelectionMenu subscribes to every child instance.</summary>
        public event Action<WeaponDefinition> Chosen;

        private bool _clickHandledThisFrame;

        private void Awake()
        {
            if (_button == null)
                _button = GetComponent<Button>();

            EnsureRaycastTarget();

            if (_button != null)
                _button.onClick.AddListener(HandleClicked);

            RefreshDisplay();
            SetSelectedVisual(false);
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
        /// The Weapon Choice prefab previously had Target Graphic = None and
        /// Background.raycastTarget = false, which made the whole choice dead.
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
                // Fallback: add a transparent hit area on the root
                Image hitArea = gameObject.GetComponent<Image>();
                if (hitArea == null)
                    hitArea = gameObject.AddComponent<Image>();
                hitArea.color = new Color(1f, 1f, 1f, 0f);
                hitArea.raycastTarget = true;
                _button.targetGraphic = hitArea;
            }

            if (_nameLabel != null)
                _nameLabel.raycastTarget = false; // let Background own the hit box
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // Backup path if Button.onClick fails to fire for any reason
            HandleClicked();
        }

        private void HandleClicked()
        {
            // Button.onClick and IPointerClickHandler can both fire for one click
            if (_clickHandledThisFrame) return;
            _clickHandledThisFrame = true;

            if (_definition == null)
            {
                Debug.LogWarning($"[{nameof(WeaponChoiceUI)}] '{name}' has no WeaponDefinition assigned.", this);
                return;
            }

            Chosen?.Invoke(_definition);
        }

        /// <summary>Toggles the optional selected-state indicator. Called by WeaponSelectionMenu after any selection change.</summary>
        public void SetSelectedVisual(bool selected)
        {
            if (_selectedIndicator != null)
                _selectedIndicator.SetActive(selected);
        }

        /// <summary>Sets the weapon definition for this choice. Used when spawning choices at runtime.</summary>
        public void SetDefinition(WeaponDefinition definition)
        {
            _definition = definition;
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (_definition == null) return;

            if (_nameLabel != null)
                _nameLabel.text = _definition.DisplayName;

            if (_iconImage != null)
            {
                if (_definition.Icon != null)
                {
                    _iconImage.sprite = _definition.Icon;
                    _iconImage.gameObject.SetActive(true);
                }
                else
                {
                    _iconImage.gameObject.SetActive(false);
                }
            }
        }
    }
}
