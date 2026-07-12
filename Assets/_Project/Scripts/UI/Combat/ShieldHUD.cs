using OffAngle.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Combat
{
    public class ShieldHUD : MonoBehaviour
    {
        [SerializeField] private Shield _shield;
        [SerializeField] private Image _fill;
        [SerializeField] private TMP_Text _label;

        private void Start()
        {
            if (_shield == null)
                _shield = GetComponentInParent<Shield>();

            if (_shield == null)
            {
                Debug.LogWarning($"[{nameof(ShieldHUD)}] No Shield assigned or found in parents for '{name}'.", this);
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

        private void HandleShieldChanged(float currentShield, float maxShield)
        {
            if (_fill != null)
                _fill.fillAmount = maxShield <= 0f ? 0f : Mathf.Clamp01(currentShield / maxShield);
            
            if (_label != null)
                _label.text = $"SHD {Mathf.CeilToInt(currentShield)}/{Mathf.CeilToInt(maxShield)}";
        }
    }
}