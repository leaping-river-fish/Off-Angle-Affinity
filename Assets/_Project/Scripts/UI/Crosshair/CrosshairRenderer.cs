// =============================================================================
// CrosshairRenderer — draws a simple four-line crosshair from CrosshairSettings.
//
// Entirely client-side, zero networking: this is a plain MonoBehaviour with no
// FishNet references. It lives under Camera Pivot inside HUD Canvas on the
// player prefab, which NetworkPlayerController only activates for the owning
// client (see NetworkPlayerController.cs), so it naturally never renders for
// remote players - same pattern AmmoHUD already relies on.
//
// EXTENSION POINT:
// Apply(CrosshairSettings) is the only entry point this class needs. Future
// systems (saved presets, a customization menu, weapon-specific or ADS-specific
// crosshairs, dynamic behaviour) all just call Apply() with a different
// CrosshairSettings value - nothing in this class has to change to support them.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Crosshair
{
    public class CrosshairRenderer : MonoBehaviour
    {
        [SerializeField] private CrosshairSettings _settings = CrosshairSettings.Default;

        [Header("Bars (Top/Bottom/Left/Right Image children)")]
        [SerializeField] private Image _top;
        [SerializeField] private Image _bottom;
        [SerializeField] private Image _left;
        [SerializeField] private Image _right;

        private void Start()
        {
            Apply(_settings);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            Apply(_settings);
        }
#endif

        /// <summary>
        /// Repositions and re-colors the four bars from the given settings.
        /// Safe to call at any time (e.g. on ADS start/stop, weapon switch,
        /// or preset changes) - it fully re-derives the visual state each call.
        /// </summary>
        public void Apply(CrosshairSettings settings)
        {
            _settings = settings;

            Color color = settings.Color;
            color.a = settings.Opacity;

            float offset = settings.Gap + settings.Length / 2f;
            Vector2 verticalSize = new Vector2(settings.Thickness, settings.Length);
            Vector2 horizontalSize = new Vector2(settings.Length, settings.Thickness);

            SetBar(_top, new Vector2(0f, offset), verticalSize, color);
            SetBar(_bottom, new Vector2(0f, -offset), verticalSize, color);
            SetBar(_left, new Vector2(-offset, 0f), horizontalSize, color);
            SetBar(_right, new Vector2(offset, 0f), horizontalSize, color);
        }

        private static void SetBar(Image image, Vector2 anchoredPosition, Vector2 size, Color color)
        {
            if (image == null) return;

            RectTransform rect = image.rectTransform;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            image.color = color;
        }
    }
}
