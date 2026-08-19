// =============================================================================
// UltimateHUD — bottom-center Overwatch-style ultimate charge circle.
//
// Drives a radial Image.fillAmount from PlayerUltimate.OnChargeChanged and
// swaps to a "ready" visual state via an Animator bool once IsReady flips,
// mirroring how HealthHUD/ShieldHUD bind a fill Image to their source event.
// The icon sprite comes from PlayerAffinity.SelectedUltimate.Icon, re-read
// whenever the loadout (re)applies since selection can change between lives.
// =============================================================================

using OffAngle.Affinities;
using OffAngle.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OffAngle.UI.Combat
{
    public class UltimateHUD : MonoBehaviour
    {
        [SerializeField] private PlayerUltimate _ultimate;
        [SerializeField] private PlayerAffinity _affinity;

        [Header("Visuals")]
        [Tooltip("Radial fill Image. Set Image Type = Filled, Fill Method = Radial 360, Fill Origin = Top, Clockwise = true.")]
        [SerializeField] private Image _fill;
        [SerializeField] private Image _icon;
        [SerializeField] private TMP_Text _percentLabel;

        [Tooltip("Optional. Bool parameter set to true while IsReady, for a pulse/glow animation authored in the Animator.")]
        [SerializeField] private Animator _animator;
        [SerializeField] private string _readyBoolParam = "IsReady";

        [Header("Colors")]
        [SerializeField] private Color _chargingColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        [SerializeField] private Color _readyColor = Color.white;

        private int _readyBoolHash;
        private bool _wasReady;

        private void Start()
        {
            if (_ultimate == null)
                _ultimate = GetComponentInParent<PlayerUltimate>();
            if (_affinity == null)
                _affinity = GetComponentInParent<PlayerAffinity>();

            if (_ultimate == null)
            {
                Debug.LogWarning($"[{nameof(UltimateHUD)}] No PlayerUltimate assigned or found in parents for '{name}'.", this);
                return;
            }

            _readyBoolHash = Animator.StringToHash(_readyBoolParam);

            _ultimate.OnChargeChanged += HandleChargeChanged;
            _ultimate.OnActivated += HandleActivated;
            AffinityEvents.LoadoutApplied += HandleLoadoutApplied;

            RefreshIcon();
            HandleChargeChanged(_ultimate.Charge, _ultimate.RequiredCharge);
        }

        private void OnDestroy()
        {
            if (_ultimate != null)
            {
                _ultimate.OnChargeChanged -= HandleChargeChanged;
                _ultimate.OnActivated -= HandleActivated;
            }

            AffinityEvents.LoadoutApplied -= HandleLoadoutApplied;
        }

        private void HandleChargeChanged(float charge, float required)
        {
            Debug.Log($"[{nameof(UltimateHUD)}] charge={charge:F2} required={required:F2}"); // TEMP DEBUG - remove after diagnosing
            float normalized = required <= 0f ? 0f : Mathf.Clamp01(charge / required);
            bool isReady = required > 0f && charge >= required;

            if (_fill != null)
                _fill.fillAmount = normalized;

            if (_percentLabel != null)
                _percentLabel.text = isReady ? string.Empty : $"{Mathf.FloorToInt(normalized * 100f)}%";

            Color color = isReady ? _readyColor : _chargingColor;
            if (_fill != null) _fill.color = color;
            if (_icon != null) _icon.color = color;

            if (isReady != _wasReady)
            {
                _wasReady = isReady;
                if (_animator != null)
                    _animator.SetBool(_readyBoolHash, isReady);
            }
        }

        private void HandleActivated()
        {
            // Charge resets to 0 server-side and replicates via OnChargeChanged;
            // nothing additional to do here beyond a hook for a future one-shot
            // "fired" effect (screen flash, sound) if wanted later.
        }

        private void HandleLoadoutApplied(FishNet.Object.NetworkObject owner, AffinityLoadout loadout)
        {
            if (_ultimate == null || owner != _ultimate.NetworkObject) return;
            RefreshIcon();
        }

        private void RefreshIcon()
        {
            if (_icon == null || _affinity == null) return;

            UltimateDefinition ultimate = _affinity.SelectedUltimate;
            _icon.sprite = ultimate != null ? ultimate.Icon : null;
            _icon.enabled = _icon.sprite != null;
        }
    }
}
