// =============================================================================
// WeaponChoiceUI — one selectable weapon entry. Reused for every weapon by
// assigning a different WeaponDefinition per prefab instance in the
// Inspector; no per-weapon script or code change required.
//
// PLACEMENT:
//   Lives on the "Weapon Choice" prefab. Place one instance per weapon inside
//   the Weapon Selection Menu prefab (under whichever category section it
//   belongs to) and assign its _definition in the Inspector.
//
// This script does not know about WeaponSelectionMenu - it only raises
// Chosen when clicked. WeaponSelectionMenu finds every WeaponChoiceUI under
// it and subscribes, keeping selection logic out of this reusable prefab.
// =============================================================================

using System;
using OffAngle.Weapons;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Weapons
{
    public class WeaponChoiceUI : MonoBehaviour
    {
        [Header("Data")]
        [Tooltip("The weapon this choice represents. Assign a different WeaponDefinition per instance - this is the only per-weapon setup required.")]
        [SerializeField] private WeaponDefinition _definition;

        [Header("Widgets")]
        [SerializeField] private TMP_Text _nameLabel;
        [SerializeField] private Button _button;

        [Header("Optional")]
        [Tooltip("Shown while this choice is the currently-selected weapon for its category. Leave null to skip the highlight.")]
        [SerializeField] private GameObject _selectedIndicator;

        public WeaponDefinition Definition => _definition;

        /// <summary>Raised when the button is clicked. WeaponSelectionMenu subscribes to every child instance.</summary>
        public event Action<WeaponDefinition> Chosen;

        private void Awake()
        {
            if (_nameLabel != null && _definition != null)
                _nameLabel.text = _definition.DisplayName;

            if (_button != null)
                _button.onClick.AddListener(HandleClicked);

            SetSelectedVisual(false);
        }

        private void OnDestroy()
        {
            if (_button != null)
                _button.onClick.RemoveListener(HandleClicked);
        }

        private void HandleClicked()
        {
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
    }
}
