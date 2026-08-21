// =============================================================================
// PlayerNameEntryUI — the small "change name" panel in the corner of the Main
// Menu scene.
//
// ARCHITECTURE:
//   Framework-agnostic like NetworkMenuUI: no FishNet import here, and no
//   NetworkMenuController dependency either. This panel runs before any
//   connection exists, so all it does is read/write PlayerPrefs through
//   PlayerNameRegistry.RequestLocalNameChange - that call also pushes the
//   name over the network immediately if a session somehow is already live,
//   but the normal path is: type a name here, connect later, and
//   PlayerNameRegistry.OnStartClient does the pushing once a session starts.
//
// PLACEMENT:
//   Any small panel GameObject in the Main Menu scene. Does not need to be
//   shown/hidden by any controller event - it's just always there.
// =============================================================================

using TMPro;
using UnityEngine;
using UnityEngine.UI;
using OffAngle.Networking;

namespace OffAngle.UI
{
    public class PlayerNameEntryUI : MonoBehaviour
    {
        [Header("Widgets")]
        [SerializeField] private TMP_InputField _nameField;
        [SerializeField] private Button _saveButton;

        [Tooltip("Optional. Set to confirm the save (or that the name was cleared) right after clicking Save.")]
        [SerializeField] private TMP_Text _statusText;

        [Tooltip("Shown as placeholder text while the field is empty.")]
        [SerializeField] private string _placeholder = "Player Name";

        private void Awake()
        {
            if (_saveButton != null)
                _saveButton.onClick.AddListener(OnSaveClicked);

            if (_nameField != null)
            {
                _nameField.characterLimit = PlayerNameRegistry.MaxNameLength;
                _nameField.text = PlayerPrefs.GetString(PlayerNameRegistry.PlayerNamePrefsKey, "");

                if (_nameField.placeholder is TMP_Text placeholderText)
                    placeholderText.text = _placeholder;
            }

            if (_statusText != null)
                _statusText.text = "";
        }

        private void OnDestroy()
        {
            if (_saveButton != null)
                _saveButton.onClick.RemoveListener(OnSaveClicked);
        }

        private void OnSaveClicked()
        {
            if (_nameField == null)
                return;

            PlayerNameRegistry.RequestLocalNameChange(_nameField.text);

            // Reflect back exactly what got saved (trimmed/clamped/stripped),
            // so the field never shows something other than the real value.
            string saved = PlayerPrefs.GetString(PlayerNameRegistry.PlayerNamePrefsKey, "");
            _nameField.text = saved;

            if (_statusText != null)
            {
                _statusText.text = string.IsNullOrEmpty(saved)
                    ? "Name cleared"
                    : $"Saved as \"{saved}\"";
            }
        }
    }
}
