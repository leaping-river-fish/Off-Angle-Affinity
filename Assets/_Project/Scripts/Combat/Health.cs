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
//   - ServerDamageApplied is a SERVER-ONLY static event carrying the finished
//     numbers. Unlike DamageFeedback it never leaves the server and is meant
//     for gameplay, not UX: ultimate charge, CombatMemory, and any future
//     post-mitigation reaction (heal-on-damage-dealt, on-hit procs) all read
//     it. One event, several consumers.
//
// THE TWO DAMAGE CHANNELS:
//   Because this is the ONLY IDamageable implementer, every damage source in
//   the game funnels through ApplyDamage - which makes it the one place worth
//   inserting conditional modification. DamagePipeline runs the attacker's and
//   victim's registered IDamageModifiers here and returns two numbers:
//     - a shieldable amount, absorbed by the shield first as always
//     - a shield-BYPASS amount, routed straight to health
//   The bypass channel is generic (this game has no "true damage"); it exists
//   so a perk can express partial shield penetration. Note the consequence: a
//   hit can now be fully absorbed by the shield AND still reduce health, so
//   there is no early return on "the shield ate it".
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

        /// <summary>
        /// Server-only. Fires after damage has actually landed on any entity.
        /// Args: (victim, info, amountApplied).
        ///
        /// amountApplied is what the hit really cost: shield absorbed plus health
        /// actually lost. It EXCLUDES overkill, so a 500-damage hit on a 10 HP
        /// target reports 10 - otherwise ultimate charge and any heal-on-damage
        /// effect would pay out on damage that never existed.
        ///
        /// Raised AFTER the damage is applied, which means DamagePipeline's
        /// modifiers see CombatMemory as it was BEFORE this hit. Opener perks
        /// ("your first hit on a target...") depend on that ordering.
        /// </summary>
        public static event Action<NetworkObject, DamageInfo, float> ServerDamageApplied;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            _current.OnChange += HandleCurrentChanged;
        }

        private void OnDestroy()
        {
            // Runs synchronously as part of FishNet's despawn broadcast on every
            // remaining peer - contain any exception here rather than letting it
            // escape into the network transport.
            try
            {
                _current.OnChange -= HandleCurrentChanged;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
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

            // Conditional perks adjust the hit here, BEFORE anything is applied and
            // before CombatMemory records it. With no modifiers registered this
            // returns info.Amount unchanged and zero bypass, so the pipeline is
            // invisible until a perk uses it.
            DamagePipeline.Resolve(info, this, _shield, out float shieldable, out float shieldBypass);

            // A modifier is allowed to reduce a hit to nothing.
            if (shieldable <= 0f && shieldBypass <= 0f) return;

            float absorbed = 0f;
            float toHealth = shieldBypass;

            if (shieldable > 0f)
            {
                float leftover = _shield != null ? _shield.AbsorbDamage(shieldable) : shieldable;
                absorbed = shieldable - leftover;
                toHealth += leftover;
            }

            if (toHealth <= 0f)
            {
                // Fully absorbed by the shield — health untouched, but still give the
                // client a damage-number popup so the hit feels acknowledged.
                RpcOnDamaged(info.HitPoint, absorbed, info.Affinity, DamageCategory.Shield);
                ServerDamageApplied?.Invoke(base.NetworkObject, info, absorbed);
                return;
            }

            // Captured before the write so overkill can be excluded from what the
            // hit is reported to have cost.
            float previous = _current.Value;
            float next = Mathf.Max(0f, previous - toHealth);
            _current.Value = next;

            RpcOnDamaged(info.HitPoint, toHealth, info.Affinity, info.Category);

            // Before OnServerDied, so a killing blow still awards its charge and is
            // still recorded in the attacker's combat memory.
            ServerDamageApplied?.Invoke(base.NetworkObject, info, absorbed + (previous - next));

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
