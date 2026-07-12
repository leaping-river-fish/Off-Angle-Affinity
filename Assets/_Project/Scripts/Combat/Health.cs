// =============================================================================
// Health — reusable, network-synchronized HP component.
//
// AUTHORITY:
//   The server owns CurrentHealth. Clients only read the SyncVar and receive
//   the RpcOnDamaged observer message for local UX (floating damage numbers).
//
// PLUMBING:
//   - IDamageable.ApplyDamage is the single write path. Anything that wants
//     to hurt this entity calls it (server-side only). This includes weapons,
//     hazards, future melee, etc.
//   - OnHealthChanged fires on every peer (server + clients) whenever the
//     SyncVar changes; UI subscribes here.
//   - OnServerDied fires only on the server the first frame CurrentHealth hits
//     zero. Respawner subscribes here.
//   - DamageFeedback is a static event carrying (position, amount, affinity)
//     for pure-UX feedback (damage numbers). Keeps gameplay -> UI dependency
//     inverted: UI subscribes to gameplay, never the reverse.
// =============================================================================

using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace OffAngle.Combat
{
    public class Health : NetworkBehaviour, IDamageable
    {
        [Header("Config")]
        [SerializeField, Min(1f)] private float _maxHealth = 100f;

        [Tooltip("If true, the server initializes CurrentHealth to MaxHealth when this object spawns.")]
        [SerializeField] private bool _initializeToMaxOnStart = true;

        [Header("Shield (optional)")]
        [Tooltip("If assigned, incoming damage is absorbed by the shield first; only the leftover reaches health.")]
        [SerializeField] private Shield _shield;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<float> _current = new SyncVar<float>();

        // ------------------------------------------------------------------
        // Public read state
        // ------------------------------------------------------------------

        public float MaxHealth => _maxHealth;
        public float CurrentHealth => _current.Value;
        public bool IsDead => _current.Value <= 0f;
        public float Normalized => _maxHealth <= 0f ? 0f : Mathf.Clamp01(_current.Value / _maxHealth);

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------

        /// <summary>Fires on every peer when CurrentHealth changes. Args: (current, max).</summary>
        public event Action<float, float> OnHealthChanged;

        /// <summary>Server-only. Fires when health first crosses to zero.</summary>
        public event Action<DamageInfo> OnServerDied;

        /// <summary>
        /// Global damage-feedback broadcast (all Health instances funnel through here).
        /// Args: (worldHitPoint, amount, affinity). UI subscribes; gameplay does not.
        /// </summary>
        public static event Action<Vector3, float, AffinityType, DamageCategory> DamageFeedback;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

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
                _current.Value = _maxHealth;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Seed subscribers with the current value; SyncVar.OnChange only fires on future writes.
            OnHealthChanged?.Invoke(_current.Value, _maxHealth);
        }

        // ------------------------------------------------------------------
        // IDamageable — write path (server only)
        // ------------------------------------------------------------------

        public void ApplyDamage(DamageInfo info)
        {
            if (!IsServerInitialized) return;
            if (IsDead) return;
            if (info.Amount <= 0f) return;

            float remaining = _shield != null ? _shield.AbsorbDamage(info.Amount) : info.Amount;
            
            if (remaining <= 0f)
            {
                // Fully absorbed by the shield — health untouched, but still give the
                // client a damage-number popup so the hit feels acknowledged.
                RpcOnDamaged(info.HitPoint, info.Amount, info.Affinity, DamageCategory.Shield);
                return;
            }

            float next = Mathf.Max(0f, _current.Value - remaining);
            _current.Value = next;

            RpcOnDamaged(info.HitPoint, remaining, info.Affinity, info.Category);

            if (next <= 0f)
                OnServerDied?.Invoke(info);
        }

        // ------------------------------------------------------------------
        // Server helpers
        // ------------------------------------------------------------------

        /// <summary>Server-only. Restores health to MaxHealth.</summary>
        public void ResetHealth()
        {
            if (!IsServerInitialized) return;
            _current.Value = _maxHealth;
        }

        /// <summary>Server-only. Applies healing without exceeding MaxHealth.</summary>
        public void Heal(float amount)
        {
            if (!IsServerInitialized) return;
            if (amount <= 0f) return;
            _current.Value = Mathf.Min(_maxHealth, _current.Value + amount);
        }

        /// <summary>Server-only. Updates MaxHealth and clamps CurrentHealth.</summary>
        public void SetMaxHealth(float value)
        {
            if (!IsServerInitialized) return;
            _maxHealth = Mathf.Max(1f, value);
            if (_current.Value > _maxHealth)
                _current.Value = _maxHealth;
            else
                OnHealthChanged?.Invoke(_current.Value, _maxHealth);
        }

        // ------------------------------------------------------------------
        // Client-visible damage RPC (UX only — never mutates game state)
        // ------------------------------------------------------------------

        [ObserversRpc]
        private void RpcOnDamaged(Vector3 hitPoint, float amount, AffinityType affinity, DamageCategory category)
        {
            DamageFeedback?.Invoke(hitPoint, amount, affinity, category);
        }

        // ------------------------------------------------------------------
        // SyncVar change handler — routes to the local OnHealthChanged event
        // ------------------------------------------------------------------

        private void HandleCurrentChanged(float prev, float next, bool asServer)
        {
            OnHealthChanged?.Invoke(next, _maxHealth);
        }
    }
}
