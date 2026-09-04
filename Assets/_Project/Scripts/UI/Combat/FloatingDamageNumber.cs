// =============================================================================
// FloatingDamageNumber — one-shot local damage popup.
//
// Not networked. Spawned by DamageNumberSpawner on every peer in response to
// Health.DamageFeedback. Rises, fades, and self-destroys after Lifetime.
//
// Color is the last pool the hit reached (cyan if fully absorbed by shield,
// affinity tint if any damage reached health). Font weight is the hit zone
// (bold for Critical/headshot). Affinity tinting is placeholder cosmetic.
// =============================================================================

using OffAngle.Combat;
using TMPro;
using UnityEngine;

namespace OffAngle.UI.Combat
{
    public class FloatingDamageNumber : MonoBehaviour
    {
        private static readonly Color ShieldColor = new Color(0.40f, 0.80f, 1.00f);
        private static readonly Color HealColor = new Color(0.40f, 1.00f, 0.40f);

        [SerializeField] private TMP_Text _text;
        [SerializeField, Min(0.1f)] private float _lifetime = 1f;
        [SerializeField] private float _riseSpeed = 1.5f;

        private float _spawnTime;
        private Color _baseColor = Color.white;
        private Transform _cameraTransform;

        // ------------------------------------------------------------------
        // Public — called immediately after Instantiate
        // ------------------------------------------------------------------

        public void Initialize(float shieldAmount, float healthAmount, AffinityType affinity, DamageCategory category)
        {
            if (_text != null)
            {
                float total = shieldAmount + healthAmount;
                _text.text = Mathf.CeilToInt(total).ToString();
                _text.fontStyle = category == DamageCategory.Critical
                    ? FontStyles.Bold
                    : FontStyles.Normal;
                _baseColor = ColorForPopup(healthAmount, affinity, category);
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
        // Color: last pool (health vs shield). Heal keeps its own tint.
        // Headshots are the same color, distinguished by bold weight.
        // ------------------------------------------------------------------

        private static Color ColorForPopup(float healthAmount, AffinityType affinity, DamageCategory category)
        {
            if (category == DamageCategory.Heal)
                return HealColor;

            if (healthAmount > 0f)
                return ColorForAffinity(affinity);

            return ShieldColor;
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
