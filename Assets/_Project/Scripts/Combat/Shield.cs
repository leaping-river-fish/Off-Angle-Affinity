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

        // Server-owned, replicated bonus added on top of _maxShield by timed
        // effects (e.g. Solar Ascension's +500 shield) - see
        // AddBonusMaxShield/RemoveBonusMaxShield. Kept separate from
        // _maxShield (a static design-time value, already identical on every
        // peer via the serialized prefab) specifically so the DYNAMIC portion
        // replicates - _maxShield alone never needed a SyncVar.
        private readonly SyncVar<float> _bonusMaxShield = new SyncVar<float>();

        private float _lastDamageTime = float.NegativeInfinity;

        // Set by PlayerLifecycleController.SetRegenLocked, driven by death/respawn
        // (mirrors the seam Gun.SetLocked already exposes for weapons). Regen
        // should not creep up while the player is dead and waiting to respawn.
        private bool _regenLocked;

        public float MaxShield => _maxShield + _bonusMaxShield.Value;
        public float CurrentShield => _current.Value;
        public float Normalized => MaxShield <= 0f ? 0f : Mathf.Clamp01(_current.Value / MaxShield);

        /// <summary>Fires on every peer when CurrentShield or MaxShield changes. Args: (current, max).</summary>
        public event Action<float, float> OnShieldChanged;

        private void Awake()
        {
            _current.OnChange += HandleCurrentChanged;
            _bonusMaxShield.OnChange += HandleBonusMaxShieldChanged;
        }

        private void OnDestroy()
        {
            // Runs synchronously as part of FishNet's despawn broadcast on every
            // remaining peer - contain any exception here rather than letting it
            // escape into the network transport.
            try
            {
                _current.OnChange -= HandleCurrentChanged;
                _bonusMaxShield.OnChange -= HandleBonusMaxShieldChanged;
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
                _current.Value = _maxShield;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Seed subscribers with the current value; SyncVar.OnChange only fires on future writes.
            // Reads MaxShield (not the bare _maxShield field) so a peer joining
            // mid-effect sees any already-active bonus immediately.
            OnShieldChanged?.Invoke(_current.Value, MaxShield);
        }

        private void HandleCurrentChanged(float prev, float next, bool asServer)
        {
            OnShieldChanged?.Invoke(next, MaxShield);
        }

        private void HandleBonusMaxShieldChanged(float prev, float next, bool asServer)
        {
            OnShieldChanged?.Invoke(_current.Value, MaxShield);
        }

        /// <summary>
        /// Server-only. Grants a temporary bonus to MaxShield, immediately
        /// usable (also raises CurrentShield by the same amount, so the bonus
        /// doesn't sit unusable behind existing damage). Pair with
        /// RemoveBonusMaxShield when the source timed effect ends - e.g.
        /// Solar Ascension's +500 shield for its duration.
        /// </summary>
        public void AddBonusMaxShield(float amount)
        {
            if (!IsServerInitialized || amount <= 0f) return;
            _bonusMaxShield.Value += amount;
            _current.Value += amount;
        }

        /// <summary>
        /// Server-only. Removes a previously granted bonus, clamping
        /// CurrentShield down if it now exceeds the reduced MaxShield (e.g.
        /// the bonus was mostly unused). Pass the exact amount given to the
        /// matching AddBonusMaxShield call.
        /// </summary>
        public void RemoveBonusMaxShield(float amount)
        {
            if (!IsServerInitialized || amount <= 0f) return;
            _bonusMaxShield.Value = Mathf.Max(0f, _bonusMaxShield.Value - amount);
            if (_current.Value > MaxShield)
                _current.Value = MaxShield;
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
            return amount - absorbed;
        }

        /// <summary>
        /// Server-only. Restarts the regen delay. Called by Health for every
        /// hit that lands — shield, health, or bypass — so regen does not
        /// begin while the player is still taking damage after the shield
        /// is already empty.
        /// </summary>
        public void NotifyDamaged()
        {
            if (!IsServerInitialized) return;
            _lastDamageTime = Time.time;
        }

        /// <summary>Server-only. Restores shield to MaxShield. Mirrors Health.ResetHealth().</summary>
        public void ResetShield()
        {
            if (!IsServerInitialized) return;
            _current.Value = MaxShield;
        }

        /// <summary>
        /// Server-only. Pauses/resumes passive regeneration wholesale. Called by
        /// PlayerLifecycleController on death (locked) and respawn (unlocked) so
        /// the shield does not creep up while the player is dead - it does not
        /// affect AbsorbDamage, which is already unreachable once Health.IsDead
        /// is true.
        /// </summary>
        public void SetRegenLocked(bool locked)
        {
            if (!IsServerInitialized) return;
            _regenLocked = locked;
        }

        private void Update()
        {
            if (!IsServerInitialized) return;
            if (_regenLocked) return;
            if (_current.Value >= MaxShield) return;
            if (Time.time < _lastDamageTime + _regenDelay) return;
            _current.Value = Mathf.Min(MaxShield, _current.Value + _regenRate * Time.deltaTime);
        }
    }
}