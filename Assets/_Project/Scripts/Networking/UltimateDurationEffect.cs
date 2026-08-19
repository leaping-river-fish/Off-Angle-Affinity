// =============================================================================
// UltimateDurationEffect — generic, reusable "active duration" countdown for
// any ultimate that stays active for a while after activation (as opposed to
// PlayerUltimate's charge-up meter, which tracks time BEFORE activation).
// Solar Ascension is the first user; any future ultimate with a visible
// active-duration bar calls ServerBegin/ServerEnd on this same component -
// see UltimateDurationBar.cs for the matching generic UI half.
//
// Always present on the Player prefab, inert until something calls
// ServerBegin. Deliberately knows nothing about any specific ultimate.
//
// AUTHORITY:
//   Same SyncVar + OnChange + seed-on-OnStartClient idiom Health/Shield
//   already use. The server ticks _remaining down in Update() and ends the
//   countdown itself when it reaches zero, so a caller only ever needs to
//   call ServerBegin (and, if the effect can end early - e.g. the player
//   dies mid-ultimate - ServerEnd).
// =============================================================================

using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace OffAngle.Networking
{
    public class UltimateDurationEffect : NetworkBehaviour
    {
        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<float> _remaining = new SyncVar<float>();
        private readonly SyncVar<float> _total = new SyncVar<float>();
        private readonly SyncVar<bool> _active = new SyncVar<bool>();

        public bool IsActive => _active.Value;
        public float Remaining => _remaining.Value;
        public float Total => _total.Value;

        /// <summary>Fires on every peer when remaining/total/active changes, including the initial seed. Args: (remaining, total, active).</summary>
        public event Action<float, float, bool> OnDurationChanged;

        private void Awake()
        {
            _remaining.OnChange += HandleFloatChanged;
            _total.OnChange += HandleFloatChanged;
            _active.OnChange += HandleActiveChanged;
        }

        private void OnDestroy()
        {
            // Runs synchronously as part of FishNet's despawn broadcast on every
            // remaining peer - contain any exception here rather than letting it
            // escape into the network transport.
            try
            {
                _remaining.OnChange -= HandleFloatChanged;
                _total.OnChange -= HandleFloatChanged;
                _active.OnChange -= HandleActiveChanged;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Seed subscribers with the current value; a SyncVar's initial replicated value never raises OnChange.
            OnDurationChanged?.Invoke(_remaining.Value, _total.Value, _active.Value);
        }

        private void Update()
        {
            if (!IsServerInitialized || !_active.Value) return;

            _remaining.Value = Mathf.Max(0f, _remaining.Value - Time.deltaTime);
            if (_remaining.Value <= 0f)
                ServerEnd();
        }

        /// <summary>Server-only. Starts (or restarts) a duration countdown.</summary>
        public void ServerBegin(float duration)
        {
            if (!IsServerInitialized) return;
            _total.Value = Mathf.Max(0.01f, duration);
            _remaining.Value = _total.Value;
            _active.Value = true;
        }

        /// <summary>Server-only. Ends the countdown immediately, whatever remains - e.g. the source ultimate ended early (death, disconnect).</summary>
        public void ServerEnd()
        {
            if (!IsServerInitialized) return;
            _remaining.Value = 0f;
            _active.Value = false;
        }

        private void HandleFloatChanged(float prev, float next, bool asServer) => RaiseChanged();
        private void HandleActiveChanged(bool prev, bool next, bool asServer) => RaiseChanged();

        private void RaiseChanged() => OnDurationChanged?.Invoke(_remaining.Value, _total.Value, _active.Value);
    }
}
