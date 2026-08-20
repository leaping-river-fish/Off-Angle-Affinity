// =============================================================================
// UltimateDurationBar — generic UI bar for any ultimate's active-duration
// countdown (as opposed to UltimateHUD's pre-activation charge meter). Drives
// an Image.fillAmount from UltimateDurationEffect.OnDurationChanged and shows/
// hides its root based on whether a duration is currently active, mirroring
// how HealthHUD/ShieldHUD/UltimateHUD bind a fill Image to their source event.
//
// Deliberately knows nothing about Solar Ascension or any other specific
// ultimate - any future ultimate that starts/ends a duration on the same
// UltimateDurationEffect drives this bar for free.
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Combat
{
    public class UltimateDurationBar : MonoBehaviour
    {
        [SerializeField] private OffAngle.Networking.UltimateDurationEffect _durationEffect;

        [Header("Visuals")]
        [Tooltip("Shown only while a duration is active. Leave null to always keep this object visible and just drive _fill.")]
        [SerializeField] private GameObject _root;
        [Tooltip("Set Image Type = Filled, Fill Method = Horizontal.")]
        [SerializeField] private Image _fill;

        private void Start()
        {
            if (_durationEffect == null)
                _durationEffect = GetComponentInParent<OffAngle.Networking.UltimateDurationEffect>();

            if (_durationEffect == null)
            {
                Debug.LogWarning($"[{nameof(UltimateDurationBar)}] No UltimateDurationEffect assigned or found in parents for '{name}'.", this);
                return;
            }

            _durationEffect.OnDurationChanged += HandleDurationChanged;
            HandleDurationChanged(_durationEffect.Remaining, _durationEffect.Total, _durationEffect.IsActive);
        }

        private void OnDestroy()
        {
            if (_durationEffect != null)
                _durationEffect.OnDurationChanged -= HandleDurationChanged;
        }

        private void HandleDurationChanged(float remaining, float total, bool active)
        {
            Debug.Log($"[{nameof(UltimateDurationBar)}] remaining={remaining:F2} total={total:F2} active={active}"); // TEMP DEBUG - remove after diagnosing

            if (_root != null)
                _root.SetActive(active);

            if (_fill != null)
                _fill.fillAmount = total <= 0f ? 0f : Mathf.Clamp01(remaining / total);
        }
    }
}
