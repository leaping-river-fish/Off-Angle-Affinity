// =============================================================================
// FloatingDamageNumber — one-shot local damage popup.
//
// Not networked. Spawned by DamageNumberSpawner on every peer in response to
// Health.DamageFeedback. Rises, fades, and self-destroys after Lifetime.
//
// Affinity tinting is placeholder cosmetic — no gameplay effect.
// =============================================================================

using OffAngle.Combat;
using TMPro;
using UnityEngine;

namespace OffAngle.UI.Combat
{
    public class FloatingDamageNumber : MonoBehaviour
    {
        [SerializeField] private TMP_Text _text;
        [SerializeField, Min(0.1f)] private float _lifetime = 1f;
        [SerializeField] private float _riseSpeed = 1.5f;

        private float _spawnTime;
        private Color _baseColor = Color.white;
        private Transform _cameraTransform;

        // ------------------------------------------------------------------
        // Public — called immediately after Instantiate
        // ------------------------------------------------------------------

        public void Initialize(float amount, AffinityType affinity, DamageCategory category)
        {
            if (_text != null)
            {
                _text.text = Mathf.CeilToInt(amount).ToString();
                _baseColor = ColorForCategory(category, affinity);
                _text.color = _baseColor;
            }
            _spawnTime = Time.time;
        }

        // ------------------------------------------------------------------
        // Animation
        // ------------------------------------------------------------------

        private void Update()
        {
            transform.position += Vector3.up * (_riseSpeed * Time.deltaTime);

            float t = (Time.time - _spawnTime) / _lifetime;
            if (_text != null)
            {
                Color c = _baseColor;
                c.a = Mathf.Clamp01(1f - t);
                _text.color = c;
            }

            if (t >= 1f)
                Destroy(gameObject);
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
        // Category color mapping — category wins over affinity; Normal falls
        // back to the existing elemental tint so affinity flavor still shows.
        // ------------------------------------------------------------------
        private static Color ColorForCategory(DamageCategory category, AffinityType affinity)
        {
            switch (category)
            {
                case DamageCategory.Critical: return new Color(1.00f, 0.85f, 0.10f); // gold
                case DamageCategory.Shield:   return new Color(0.40f, 0.80f, 1.00f); // cyan
                case DamageCategory.Heal:     return new Color(0.40f, 1.00f, 0.40f); // green
                default:                      return ColorForAffinity(affinity);
            }
        }

        // ------------------------------------------------------------------
        // Placeholder color mapping — pure UX, no gameplay effect
        // ------------------------------------------------------------------

        private static Color ColorForAffinity(AffinityType a)
        {
            switch (a)
            {
                case AffinityType.Frost:   return new Color(0.60f, 0.85f, 1.00f);
                case AffinityType.Cinder:  return new Color(1.00f, 0.55f, 0.25f);
                case AffinityType.Tide:    return new Color(0.35f, 0.75f, 1.00f);
                case AffinityType.Tempest: return new Color(0.90f, 0.95f, 0.55f);
                case AffinityType.Thorn:   return new Color(0.55f, 1.00f, 0.55f);
                case AffinityType.Void:    return new Color(0.80f, 0.50f, 1.00f);
                default:                   return Color.white;
            }
        }
    }
}
