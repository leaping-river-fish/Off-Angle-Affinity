// =============================================================================
// FloatingDamageNumber — one-shot local damage popup.
//
// Not networked. Spawned by DamageNumberSpawner on the attacking player's
// client in response to Health.DamageFeedback. Rises, holds, fades, and
// self-destroys after Lifetime. World scale tracks camera distance so on-screen
// size stays roughly constant as you move in or out.
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

        [Header("Motion")]
        [SerializeField, Min(0.1f)] private float _lifetime = 2.5f;
        [SerializeField] private float _riseSpeed = 1.5f;
        [SerializeField, Min(0f)] private float _fadeDuration = 0.6f;

        [Header("Size (tune on the Damage Number prefab)")]
        [Tooltip("Primary knob. 1 = designed size at Reference Distance. Raise to make every number larger.")]
        [SerializeField, Min(0.01f)] private float _size = 1f;
        [Tooltip("World metres from camera at which Size 1 looks like the designed scale.")]
        [SerializeField, Min(0.01f)] private float _referenceDistance = 10f;
        [Tooltip("Floor so numbers never vanish at point-blank.")]
        [SerializeField, Min(0.01f)] private float _minScale = 0.4f;
        [Tooltip("Cap so numbers never fill the screen at long range.")]
        [SerializeField, Min(0.01f)] private float _maxScale = 3f;

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

            float age = Time.time - _spawnTime;
            if (_text != null)
            {
                Color c = _baseColor;
                c.a = AlphaAt(age);
                _text.color = c;
            }

            if (age >= _lifetime)
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
            float sqr = toCam.sqrMagnitude;
            if (sqr < 0.0001f) return;

            transform.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);

            float distance = Mathf.Sqrt(sqr);
            float scale = distance * (_size / _referenceDistance);
            scale = Mathf.Clamp(scale, _minScale, _maxScale);
            transform.localScale = Vector3.one * scale;
        }

        private float AlphaAt(float age)
        {
            float fade = Mathf.Min(_fadeDuration, _lifetime);
            float fadeStart = _lifetime - fade;
            if (age <= fadeStart)
                return 1f;
            if (fade <= 0f)
                return 0f;
            return Mathf.Clamp01(1f - (age - fadeStart) / fade);
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
