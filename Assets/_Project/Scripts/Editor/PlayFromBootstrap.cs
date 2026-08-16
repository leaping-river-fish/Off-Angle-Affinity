// =============================================================================
// PlayFromBootstrap — forces Play to always start from the Bootstrap scene.
//
// WHY:
//   The NetworkManager (plus NetworkMenuController / PlayerSpawner /
//   GameFlowController and the persistent EventSystem) lives in Bootstrap, not
//   MainMenu. Pressing Play while MainMenu, Lobby or Game is the open scene
//   therefore runs with no NetworkManager at all: Host/Join log
//   "NetworkManager not ready", and UI clicks do nothing because the
//   EventSystem is missing too.
//
//   Rather than rely on remembering to open Bootstrap first, this points
//   EditorSceneManager.playModeStartScene at it. You keep whatever scene you
//   are editing open; Play still enters through Bootstrap and lands on
//   MainMenu, matching a real build exactly.
//
// TOGGLE:
//   Tools > Off-Angle > Always Play From Bootstrap (on by default).
//   Turn it off when you deliberately want to Play the open scene in isolation.
// =============================================================================

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OffAngle.Editor
{
    [InitializeOnLoad]
    public static class PlayFromBootstrap
    {
        private const string BootstrapScenePath = "Assets/_Project/Scenes/Bootstrap.unity";
        private const string MenuPath = "Tools/Off-Angle/Always Play From Bootstrap";

        // EditorPrefs, not a static field: this must survive domain reloads and
        // editor restarts, and it is a per-developer preference, not project data.
        private const string PrefKey = "OffAngle.AlwaysPlayFromBootstrap";

        private static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        static PlayFromBootstrap() => EditorApplication.delayCall += Apply;

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            Enabled = !Enabled;
            Apply();
        }

        [MenuItem(MenuPath, isValidateFunction: true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        // delayCall'd: AssetDatabase lookups are unreliable from a static
        // constructor during domain reload.
        private static void Apply()
        {
            if (!Enabled)
            {
                EditorSceneManager.playModeStartScene = null;
                return;
            }

            SceneAsset bootstrap = AssetDatabase.LoadAssetAtPath<SceneAsset>(BootstrapScenePath);

            if (bootstrap == null)
            {
                // Expected only before Bootstrap.unity has been created. Silent
                // no-op rather than an error, so the project still opens cleanly.
                EditorSceneManager.playModeStartScene = null;
                return;
            }

            EditorSceneManager.playModeStartScene = bootstrap;
        }
    }
}
