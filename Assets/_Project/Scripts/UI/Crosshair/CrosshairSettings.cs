// =============================================================================
// CrosshairSettings — plain serializable data for a single crosshair look.
//
// Deliberately just a bag of values with no behaviour. Keeping it a standalone
// struct (rather than fields scattered across CrosshairRenderer) means it can
// later be dropped into a ScriptableObject preset, a JSON export string, or a
// per-weapon/per-ADS override without any of those future systems needing to
// know about UI, MonoBehaviours, or the Inspector.
// =============================================================================

using UnityEngine;

namespace OffAngle.UI.Crosshair
{
    [System.Serializable]
    public struct CrosshairSettings
    {
        [Tooltip("Width of each crosshair bar, in reference-resolution pixels.")]
        public float Thickness;

        [Tooltip("Length of each crosshair bar, in reference-resolution pixels.")]
        public float Length;

        [Tooltip("Empty space between the center point and the start of each bar, in reference-resolution pixels.")]
        public float Gap;

        [Tooltip("Overall opacity, applied on top of Color's alpha.")]
        [Range(0f, 1f)] public float Opacity;

        public Color Color;

        public static CrosshairSettings Default => new CrosshairSettings
        {
            Thickness = 2f,
            Length = 8f,
            Gap = 4f,
            Opacity = 1f,
            Color = Color.white
        };
    }
}
