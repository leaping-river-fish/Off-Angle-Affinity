using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

// =============================================================================
// Shield — reusable, network-synchronized regenerating shield pool.
//
// AUTHORITY:
//   The server owns CurrentShield. Clients only read the SyncVar.
//
// PLUMBING:
//   - AbsorbDamage is the single write path for incoming damage, called by
//     Health.ApplyDamage before health is touched. Shield has no concept of
//     IDamageable itself — Health decides whether a shield exists and asks it
//     to absorb first; this keeps Health the single damage entry point used
//     by both the Player and the Dummy.
//   - OnShieldChanged fires on every peer (server + clients) whenever the
//     SyncVar changes; UI subscribes here, same pattern as Health.
//   - Regeneration runs in Update(), gated by IsServerInitialized so only the
//     server ever advances CurrentShield; the SyncVar replicates the result.
// =============================================================================

namespace OffAngle.Combat
{
    public class Shield : NetworkBehaviour
    {
        [Header("Config")]
        [SerializeField, Min(1f)] private float _maxShield = 100f;
        
        [Tooltip("If true, the server initializes CurrentShield to MaxShield when this object spawns.")]
        [SerializeField] private bool _initializeToMaxOnStart = true;

        [Header("Regeneration")]
        [Tooltip("Seconds of no damage before shield starts regenerating.")]
        [SerializeField, Min(0f)] private float _regenDelay = 3f;

        [Tooltip("Shield points restored per second once regeneration begins.")]
        [SerializeField, Min(0f)] private float _regenRate = 10f;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<float> _current = new SyncVar<float>();

        private float _lastDamageTime = float.NegativeInfinity;

        public float MaxShield => _maxShield;
        public float CurrentShield => _current.Value;
        public float Normalized => _maxShield <= 0f ? 0f : Mathf.Clamp01(_current.Value / _maxShield);
        
        /// <summary>Fires on every peer when CurrentShield changes. Args: (current, max).</summary>
        public event Action<float, float> OnShieldChanged;

        private void Awake()
        {
            _current.OnChange += HandleCurrentChanged;
        }

        private void OnDestroy()
        {
            _current.OnChange -= HandleCurrentChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (_initializeToMaxOnStart)
                _current.Value = _maxShield;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Seed subscribers with the current value; SyncVar.OnChange only fires on future writes.
            OnShieldChanged?.Invoke(_current.Value, _maxShield);
        }

        private void HandleCurrentChanged(float prev, float next, bool asServer)
        {
            OnShieldChanged?.Invoke(next, _maxShield);
        }

        /// <summary>
        /// Server-only. Reduces the shield by up to <paramref name="amount"/> and
        /// returns whatever portion could not be absorbed (0 if the shield fully
        /// absorbed the hit). Never lets shield go negative.
        /// </summary>
        public float AbsorbDamage(float amount)
        {
            if (!IsServerInitialized) return amount;
            if (amount <= 0f) return 0f;

            float absorbed = Mathf.Min(_current.Value, amount);
            _current.Value -= absorbed;

            if (absorbed > 0f)
                _lastDamageTime = Time.time;

            return amount - absorbed;
        }

        /// <summary>Server-only. Restores shield to MaxShield. Mirrors Health.ResetHealth().</summary>
        public void ResetShield()
        {
            if (!IsServerInitialized) return;
            _current.Value = _maxShield;
        }

        private void Update()
        {
            if (!IsServerInitialized) return;
            if (_current.Value >= _maxShield) return;
            if (Time.time < _lastDamageTime + _regenDelay) return;
            _current.Value = Mathf.Min(_maxShield, _current.Value + _regenRate * Time.deltaTime);
        }
    }
}