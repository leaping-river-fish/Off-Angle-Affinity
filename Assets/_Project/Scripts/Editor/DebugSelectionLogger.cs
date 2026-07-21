#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// #region agent log
// Temporary debug-session instrumentation - logs the active Hierarchy
// selection at every Play Mode transition, and whenever a GameObject is
// destroyed while it is the active selection. Helps correlate stale-Inspector
// exceptions (GameObjectInspector/CanvasScalerEditor) with what was selected.
[InitializeOnLoad]
internal static class DebugSelectionLogger
{
    static DebugSelectionLogger()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        try
        {
            GameObject sel = Selection.activeGameObject;
            string selName = sel != null ? sel.name : "null";

            ActiveEditorTracker tracker = ActiveEditorTracker.sharedTracker;
            bool locked = tracker.isLocked;
            Editor[] editors = tracker.activeEditors;
            string editorTargets = string.Join(";", Array.ConvertAll(editors, ed =>
            {
                UnityEngine.Object t = ed.target;
                string editorType = ed.GetType().Name;
                string targetInfo = t == null ? "NULL_TARGET" : (t.GetType().Name + ":" + t.ToString());
                return editorType + "->" + targetInfo;
            }));

            string json = "{\"sessionId\":\"e98bf3\",\"runId\":\"editor-selection\",\"hypothesisId\":\"H5-H6\",\"location\":\"DebugSelectionLogger.cs\",\"message\":\"playModeStateChanged\",\"data\":{\"state\":\"" + state + "\",\"selectedObject\":\"" + selName + "\",\"trackerLocked\":" + locked.ToString().ToLowerInvariant() + ",\"activeEditorCount\":" + editors.Length + ",\"editorTargets\":\"" + editorTargets.Replace("\"", "'") + "\"},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
            File.AppendAllText("debug-e98bf3.log", json + "\n");
        }
        catch (Exception e)
        {
            try { File.AppendAllText("debug-e98bf3.log", "{\"sessionId\":\"e98bf3\",\"runId\":\"editor-selection\",\"hypothesisId\":\"H5-H6\",\"location\":\"DebugSelectionLogger.cs\",\"message\":\"logger threw\",\"data\":{\"error\":\"" + e.Message.Replace("\"", "'") + "\"},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n"); } catch { }
        }
    }
}
// #endregion
#endif
