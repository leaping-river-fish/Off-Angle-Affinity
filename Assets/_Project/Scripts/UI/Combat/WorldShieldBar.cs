using OffAngle.Combat;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Combat
{
    public class WorldShieldBar : MonoBehaviour
    {
        [SerializeField] private Shield _shield;
        [SerializeField] private Slider _fill;

        private void Start()
        {
            if (_shield == null)
                _shield = GetComponentInParent<Shield>();

            if (_shield == null)
            {
                Debug.LogWarning($"[{nameof(WorldShieldBar)}] No Shield assigned or found in parents for '{name}'.", this);
                return;
            }

            _shield.OnShieldChanged += HandleShieldChanged;
            HandleShieldChanged(_shield.CurrentShield, _shield.MaxShield);
        }

        private void OnDestroy()
        {
            if (_shield != null)
                _shield.OnShieldChanged -= HandleShieldChanged;
        }
        
        private void HandleShieldChanged(float current, float max)
        {
            if (_fill != null)
                _fill.value = max <= 0f ? 0f : Mathf.Clamp01(current / max);
        }
    }
}