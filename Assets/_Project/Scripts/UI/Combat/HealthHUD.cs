using OffAngle.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Combat
{
    public class HealthHUD : MonoBehaviour
    {
        [SerializeField] private Health _health;
        [SerializeField] private Image _fill;
        [SerializeField] private TMP_Text _label;

        private void Start()
        {
            if (_health == null)
                _health = GetComponentInParent<Health>();
            if (_health == null)
            {
                Debug.LogWarning($"[{nameof(HealthHUD)}] No Health assigned or found in parents for '{name}'.", this);
                return;
            }
            _health.OnHealthChanged += HandleHealthChanged;
            HandleHealthChanged(_health.CurrentHealth, _health.MaxHealth);
        }

        private void OnDestroy()
        {
            if (_health != null)
                _health.OnHealthChanged -= HandleHealthChanged;
        }

        private void HandleHealthChanged(float currentHealth, float maxHealth)
        {
            if (_fill != null)
                _fill.fillAmount = maxHealth <= 0f ? 0f : Mathf.Clamp01(currentHealth / maxHealth);

            if (_label != null)
                _label.text = $"HP {Mathf.CeilToInt(currentHealth)}/{Mathf.CeilToInt(maxHealth)}";
        }
    }
}