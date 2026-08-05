// =============================================================================
// WeaponRegistryEditor — custom inspector for WeaponRegistry that adds a
// "Refresh Weapon List" button to auto-populate the AllWeapons list by
// scanning for all WeaponDefinition assets in the project.
//
// This runs in the Editor only and provides a convenient way to keep the
// registry up-to-date without manually dragging assets into the list.
// =============================================================================

#if UNITY_EDITOR
using System.Linq;
using OffAngle.Weapons;
using UnityEditor;
using UnityEngine;

namespace OffAngle.Editor
{
    [CustomEditor(typeof(WeaponRegistry))]
    public class WeaponRegistryEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            WeaponRegistry registry = (WeaponRegistry)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Click 'Refresh Weapon List' to scan the project for all WeaponDefinition assets and populate the list automatically.",
                MessageType.Info
            );

            if (GUILayout.Button("Refresh Weapon List", GUILayout.Height(30)))
            {
                RefreshWeaponList(registry);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Weapon Count", registry.AllWeapons.Count.ToString());
        }

        private void RefreshWeaponList(WeaponRegistry registry)
        {
            string[] guids = AssetDatabase.FindAssets("t:WeaponDefinition");
            
            registry.AllWeapons.Clear();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                WeaponDefinition weapon = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                
                if (weapon != null)
                {
                    registry.AllWeapons.Add(weapon);
                }
            }

            registry.AllWeapons = registry.AllWeapons
                .OrderBy(w => w.Category?.DisplayName ?? "")
                .ThenBy(w => w.DisplayName)
                .ToList();

            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
