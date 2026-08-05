// =============================================================================
// PlayerInputState — defines the high-level input/UI modes the player can be in.
//
// Used by PlayerInputStateController to gate input, control cursor visibility,
// and manage HUD/menu UI. Designed to be extended with additional states
// (Paused, SettingsMenu, LoadoutEditor, etc.) without rewriting core systems.
// =============================================================================

namespace OffAngle.Core
{
    public enum PlayerInputState
    {
        /// <summary>Normal gameplay: movement, combat, abilities all active. Cursor locked.</summary>
        Gameplay,

        /// <summary>Menu open: gameplay input disabled, UI input enabled. Cursor unlocked.</summary>
        Menu,

        /// <summary>Player dead: all input disabled except respawn UI. Cursor unlocked.</summary>
        Dead,

        // Future states can be added here without breaking existing code:
        // Paused,
        // SettingsMenu,
        // LoadoutEditor,
        // Spectating,
        // Cutscene,
    }
}
