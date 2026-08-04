// =============================================================================
// AdsCrosshairController — fades or hides the crosshair during ADS.
//
// ARCHITECTURE:
//   Lives on the HUD Canvas alongside CrosshairRenderer. Polls
//   WeaponAdsController.AdsBlend each frame and calls CrosshairRenderer.Apply()
//   with modified opacity. Completely optional - if this component is not
//   present, the crosshair will remain visible during ADS (useful for testing
//   placeholder weapons that don't have real sights yet).
//
// FADE MODES:
//   - None: Crosshair always visible (no-op, same as not having this component).
//   - BlendBased: Opacity fades smoothly based on AdsBlend (0-1).
//   - HideWhenFullyAimed: Opacity drops to 0 only when IsFullyAimed is true.
//
// MULTIPLAYER NOTE:
//   Owner-only component (HUD Canvas is only active for the owning client).
//   No networking - crosshair is purely local presentation.
// =============================================================================

using UnityEngine;
using OffAngle.UI.Crosshair;

namespace OffAngle.Weapons
{
    public class AdsCrosshairController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Leave null to auto-resolve via GetComponent.")]
        [SerializeField] private CrosshairRenderer _crosshairRenderer;

        [Tooltip("The ADS controller on the player's Camera Pivot. Required.")]
        [SerializeField] private WeaponAdsController _adsController;

        [Header("Fade Mode")]
        [Tooltip("How the crosshair should react to ADS state.")]
        [SerializeField] private FadeMode _fadeMode = FadeMode.BlendBased;

        [Tooltip("Opacity when ADS is inactive (hip-fire). Used as the base value for fade calculations.")]
        [SerializeField, Range(0f, 1f)] private float _hipOpacity = 1f;

        [Tooltip("Opacity when fully aimed. Only used in BlendBased mode. Set to 0 to fully hide.")]
        [SerializeField, Range(0f, 1f)] private float _adsOpacity = 0f;

        private CrosshairSettings _defaultSettings;
        private bool _hasInitialized;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_crosshairRenderer == null)
                _crosshairRenderer = GetComponent<CrosshairRenderer>();
        }

        private void Start()
        {
            // Cache the default crosshair settings from the renderer.
            // We'll modify opacity each frame but keep everything else unchanged.
            if (_crosshairRenderer != null)
            {
                _defaultSettings = new CrosshairSettings
                {
                    Thickness = 2f,
                    Length = 8f,
                    Gap = 4f,
                    Opacity = _hipOpacity,
                    Color = Color.white
                };
                _hasInitialized = true;
            }
        }

        private void LateUpdate()
        {
            if (!_hasInitialized || _crosshairRenderer == null || _adsController == null)
                return;

            if (_fadeMode == FadeMode.None)
                return; // No-op: crosshair stays at default opacity.

            // Calculate target opacity based on fade mode.
            float targetOpacity = _hipOpacity;

            switch (_fadeMode)
            {
                case FadeMode.BlendBased:
                    // Smoothly interpolate opacity based on ADS blend.
                    targetOpacity = Mathf.Lerp(_hipOpacity, _adsOpacity, _adsController.AdsBlend);
                    break;

                case FadeMode.HideWhenFullyAimed:
                    // Binary: hide only when fully aimed, else show at hip opacity.
                    targetOpacity = _adsController.IsFullyAimed ? 0f : _hipOpacity;
                    break;
            }

            // Apply updated settings to the renderer.
            CrosshairSettings settings = _defaultSettings;
            settings.Opacity = targetOpacity;
            _crosshairRenderer.Apply(settings);
        }
    }

    // ------------------------------------------------------------------
    // Enum: FadeMode
    // ------------------------------------------------------------------

    public enum FadeMode
    {
        /// <summary>Crosshair always visible at default opacity (no ADS reaction).</summary>
        None,

        /// <summary>Crosshair opacity fades smoothly from hip to ADS based on AdsBlend (0-1).</summary>
        BlendBased,

        /// <summary>Crosshair is hidden only when IsFullyAimed is true (binary on/off).</summary>
        HideWhenFullyAimed
    }
}
