// =============================================================================
// AffinityRegistryEditor — custom inspector for AffinityRegistry that adds a
// "Refresh Affinity List" button to auto-populate AllAffinities by scanning for
// every AffinityDefinition asset in the project.
//
// Direct counterpart of WeaponRegistryEditor. It only SCANS for assets that
// already exist - it never creates any.
//
// It also surfaces the id problems that are otherwise invisible until a match
// goes wrong: a blank id cannot be selected or synced at all, and a duplicate
// id makes two different perks resolve to the same asset on every peer.
// =============================================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using OffAngle.Affinities;
using UnityEditor;
using UnityEngine;

namespace OffAngle.Editor
{
    [CustomEditor(typeof(AffinityRegistry))]
    public class AffinityRegistryEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            AffinityRegistry registry = (AffinityRegistry)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Click 'Refresh Affinity List' to scan the project for all AffinityDefinition assets and populate the list automatically.",
                MessageType.Info
            );

            if (GUILayout.Button("Refresh Affinity List", GUILayout.Height(30)))
            {
                RefreshAffinityList(registry);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Affinity Count", registry.AllAffinities.Count.ToString());

            DrawIdValidation(registry);
        }

        private void RefreshAffinityList(AffinityRegistry registry)
        {
            string[] guids = AssetDatabase.FindAssets("t:AffinityDefinition");

            List<AffinityDefinition> existingOrder = registry.AllAffinities
                .Where(a => a != null)
                .ToList();

            var found = new List<AffinityDefinition>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AffinityDefinition affinity = AssetDatabase.LoadAssetAtPath<AffinityDefinition>(path);

                if (affinity != null)
                {
                    found.Add(affinity);
                }
            }

            // Preserve the existing (hand-arranged) order for affinities that are
            // still present, then append any newly discovered ones alphabetically.
            registry.AllAffinities = existingOrder
                .Where(a => found.Contains(a))
                .Concat(found.Except(existingOrder).OrderBy(a => a.DisplayName))
                .ToList();

            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------
        // Id validation — every id must be present and unique across the WHOLE
        // registry, because ids are the only thing that crosses the network.
        // ------------------------------------------------------------------

        private static void DrawIdValidation(AffinityRegistry registry)
        {
            var seen = new HashSet<string>();
            var duplicates = new List<string>();
            int blanks = 0;

            void Check(string id)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    blanks++;
                    return;
                }

                if (!seen.Add(id))
                    duplicates.Add(id);
            }

            foreach (AffinityDefinition affinity in registry.AllAffinities)
            {
                if (affinity == null) continue;

                Check(affinity.Id);

                if (affinity.Ultimates != null)
                {
                    foreach (UltimateDefinition ultimate in affinity.Ultimates)
                    {
                        if (ultimate != null) Check(ultimate.Id);
                    }
                }

                if (affinity.PerkRows == null) continue;

                foreach (AffinityDefinition.PerkRow row in affinity.PerkRows)
                {
                    if (row?.Perks == null) continue;

                    foreach (PerkDefinition perk in row.Perks)
                    {
                        if (perk != null) Check(perk.Id);
                    }
                }
            }

            if (blanks > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{blanks} asset(s) have a blank Id. They cannot be selected or synced until every Id is filled in.",
                    MessageType.Error);
            }

            if (duplicates.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Duplicate Id(s): {string.Join(", ", duplicates.Distinct())}. Ids must be unique across every affinity, ultimate and perk - a duplicate makes two different assets resolve to the same one on every peer.",
                    MessageType.Error);
            }

            if (registry.DefaultAffinity == null)
            {
                EditorGUILayout.HelpBox(
                    "No DefaultAffinity assigned. Players who never picked (late joiners, countdown expiry) will spawn with no affinity at all.",
                    MessageType.Warning);
            }
        }
    }
}
#endif
