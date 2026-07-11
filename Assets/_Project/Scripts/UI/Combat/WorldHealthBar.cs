// =============================================================================
// WorldHealthBar — world-space UI that mirrors a Health component.
//
// Purely cosmetic. The SyncVar in Health does the actual multiplayer sync; this
// component just subscribes to the local OnHealthChanged event and updates a
// fill slider + numeric label. Billboards toward Camera.main so the bar always
// faces the local player.
//
// If the Health reference is left empty in the prefab, Start attempts to find
// one on a parent GameObject — convenient for the shared HealthBar prefab that
// is dropped as a child of both the Player and the Dummy.
// =============================================================================

using OffAngle.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Combat
{
    public class WorldHealthBar : MonoBehaviour
    {
        [SerializeField] private Health _health;
        [SerializeField] private Slider _fill;
        [SerializeField] private TMP_Text _label;

        private Transform _cameraTransform;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Start()
        {
            if (_health == null)
                _health = GetComponentInParent<Health>();

            if (_health == null)
            {
                Debug.LogWarning($"[{nameof(WorldHealthBar)}] No Health assigned or found in parents for '{name}'.", this);
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

        private void LateUpdate()
        {
            if (_cameraTransform == null)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                _cameraTransform = cam.transform;
            }

            Vector3 toCam = transform.position - _cameraTransform.position;
            if (toCam.sqrMagnitude < 0.0001f) return;

            transform.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
        }

        // ------------------------------------------------------------------
        // Rendering
        // ------------------------------------------------------------------

        private void HandleHealthChanged(float current, float max)
        {
            if (_fill != null)
                _fill.value = max <= 0f ? 0f : Mathf.Clamp01(current / max);

            if (_label != null)
                _label.text = $"{Mathf.CeilToInt(current)} / {Mathf.CeilToInt(max)}";
        }
    }
}
