// =============================================================================
// FontAtlasModeTool — bulk-switches every TMP_FontAsset in the project from
// Dynamic to Static atlas population mode. Dynamic fonts silently append new
// glyphs/kerning pairs to their .asset file whenever unseen character
// combinations are rendered, which causes constant, unintentional diffs and
// merge conflicts on those files. Static mode freezes the atlas as-is.
// =============================================================================

#if UNITY_EDITOR
using TMPro;
using UnityEditor;
using UnityEngine;

namespace OffAngle.Editor
{
    public static class FontAtlasModeTool
    {
        [MenuItem("Tools/Fonts/Set All TMP Font Assets To Static")]
        public static void SetAllToStatic()
        {
            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");

            if (!EditorUtility.DisplayDialog(
                "Set Font Assets To Static",
                $"Found {guids.Length} TMP_FontAsset(s) in the project. " +
                "Any currently set to Dynamic will be switched to Static so they stop " +
                "self-modifying at runtime. Continue?",
                "Continue", "Cancel"))
            {
                return;
            }

            int changed = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TMP_FontAsset fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);

                if (fontAsset == null || fontAsset.atlasPopulationMode == AtlasPopulationMode.Static)
                {
                    continue;
                }

                fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
                EditorUtility.SetDirty(fontAsset);
                changed++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"FontAtlasModeTool: switched {changed} of {guids.Length} font asset(s) to Static.");
        }
    }
}
#endif
