// =============================================================================
// PlayerUltimate — the ultimate charge meter and activation gate.
//
// CHARGE, NOT COOLDOWN:
//   There is no cooldown anywhere in this system. Charge starts at 0 and fills
//   from damage dealt, damage taken, and a slow passive trickle. An ultimate
//   becomes usable at its own RequiredCharge and resets to 0 when spent, so the
//   meter is self-limiting - no separate rate guard is needed.
//
//   RequiredCharge lives per-ultimate (a stronger one costs more); the GAIN
//   RATES live here, globally. That split means retuning the economy is this
//   one Inspector rather than every ultimate asset in the project.
//
// AUTHORITY:
//   Charge is server-owned, because the only honest source of "damage dealt"
//   and "damage taken" is the server. Clients read the SyncVar for their HUD.
//   The client's own IsReady check is a UX convenience to avoid pointless RPCs;
//   the server re-checks charge and alive state and never trusts it.
//
//   The movement-availability check is deliberately OWNER-ONLY. On a dedicated
//   server a remote player's MovementStateMachine is disabled and its state is
//   meaningless, so testing it there would either always pass or wrongly block.
//   The gates that actually matter for integrity - charge and alive - are both
//   server-side. Same split PlayerGrapple uses.
//
// INPUT:
//   Wired to the Ultimate action in Player Inputs.inputactions via
//   PlayerInputReader.UltimateStarted, the only file allowed to import the
//   Input System. Subscription is owner-gated in OnStartClient/OnStopClient,
//   the same shape PlayerGrapple uses for GrappleStarted.
//
// MANUAL SETUP:
//   Add to the Player prefab root, alongside PlayerAffinity. Assign
//   _inputReader in the Inspector - it is not auto-resolved, matching
//   PlayerGrapple (PlayerInputReader lives on a different GameObject, so
//   GetComponent would not find it).
// =============================================================================

using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Affinities;
using OffAngle.Combat;
using OffAngle.Core;
using OffAngle.Movement;
using UnityEngine;

namespace OffAngle.Networking
{
    public class PlayerUltimate : NetworkBehaviour
    {
        [Header("References (leave null to auto-resolve on this GameObject)")]
        [SerializeField] private PlayerAffinity _affinity;
        [SerializeField] private Health _health;
        [SerializeField] private PlayerLifecycleController _lifecycle;
        [SerializeField] private MovementStateMachine _movement;

        [Tooltip("Optional. Leave null to auto-resolve on this GameObject. Source of the trusted aim ray sent alongside activation - see TryActivate/GetTrustedAimRay.")]
        [SerializeField] private PlayerWeaponController _weapons;

        [Tooltip("Optional. When assigned, charge gain (passive trickle and damage-based) pauses for as long as this reports IsActive - e.g. while Solar Ascension's active window is running, so an ultimate cannot fully recharge inside its own duration. Left null, charge is never paused this way.")]
        [SerializeField] private UltimateDurationEffect _durationEffect;

        [Tooltip("Not auto-resolved - assign in the Inspector. PlayerInputReader lives on a different GameObject, same as PlayerGrapple._inputReader.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [Header("Charge gain (global — per-ultimate cost lives on UltimateDefinition)")]
        [Tooltip("Charge granted per 1 point of damage dealt to another entity. Self-damage never counts.")]
        [SerializeField, Min(0f)] private float _chargePerDamageDealt = 1f;

        [Tooltip("Charge granted per 1 point of damage taken.")]
        [SerializeField, Min(0f)] private float _chargePerDamageTaken = 1f;

        [Tooltip("The slow trickle, in charge per second. Does not accrue while dead.")]
        [SerializeField, Min(0f)] private float _passiveChargePerSecond = 0.5f;

        [Tooltip("Fraction of current charge kept when killed. 1 keeps it all, 0 wipes it on every death.")]
        [SerializeField, Range(0f, 1f)] private float _chargeRetainedOnDeath = 1f;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<float> _charge = new SyncVar<float>();

        // ------------------------------------------------------------------
        // Public read state (HUD)
        // ------------------------------------------------------------------

        public float Charge => _charge.Value;

        /// <summary>Charge needed for the selected ultimate. 0 when nothing is selected.</summary>
        public float RequiredCharge
        {
            get
            {
                UltimateDefinition ultimate = _affinity != null ? _affinity.SelectedUltimate : null;
                return ultimate != null ? ultimate.RequiredCharge : 0f;
            }
        }

        /// <summary>Charge as 0..1 for a meter fill. 0 when no ultimate is selected.</summary>
        public float ChargeNormalized
        {
            get
            {
                float required = RequiredCharge;
                return required <= 0f ? 0f : Mathf.Clamp01(_charge.Value / required);
            }
        }

        /// <summary>True when the ultimate can be used right now, ignoring movement state.</summary>
        public bool IsReady
        {
            get
            {
                UltimateDefinition ultimate = _affinity != null ? _affinity.SelectedUltimate : null;
                if (ultimate == null || ultimate.Behavior == null) return false;
                return _charge.Value >= ultimate.RequiredCharge;
            }
        }

        /// <summary>Fires on every peer when charge or the requirement changes. Args: (charge, required).</summary>
        public event Action<float, float> OnChargeChanged;

        /// <summary>Fires on every peer when this player's ultimate activates.</summary>
        public event Action OnActivated;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_affinity == null) _affinity = GetComponent<PlayerAffinity>();
            if (_health == null) _health = GetComponent<Health>();
            if (_lifecycle == null) _lifecycle = GetComponent<PlayerLifecycleController>();
            if (_movement == null) _movement = GetComponent<MovementStateMachine>();
            if (_weapons == null) _weapons = GetComponent<PlayerWeaponController>();
            if (_durationEffect == null) _durationEffect = GetComponent<UltimateDurationEffect>();

            _charge.OnChange += HandleChargeChanged;
        }

        private void OnDestroy()
        {
            try
            {
                _charge.OnChange -= HandleChargeChanged;
                Health.ServerDamageApplied -= HandleServerDamageApplied;
                AffinityEvents.LoadoutApplied -= HandleLoadoutApplied;

                if (_health != null)
                    _health.OnServerDied -= HandleServerDied;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            _charge.Value = 0f;

            Health.ServerDamageApplied += HandleServerDamageApplied;

            if (_health != null)
                _health.OnServerDied += HandleServerDied;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            // Runs inside FishNet's despawn broadcast - contain any exception
            // rather than letting it escape into the transport.
            try
            {
                Health.ServerDamageApplied -= HandleServerDamageApplied;

                if (_health != null)
                    _health.OnServerDied -= HandleServerDied;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // The requirement half of the meter only becomes known once the
            // loadout applies, which can be after this runs.
            AffinityEvents.LoadoutApplied += HandleLoadoutApplied;

            // Seed subscribers directly; a SyncVar's initial replicated value never
            // raises OnChange (same caveat Health and Shield document).
            OnChargeChanged?.Invoke(_charge.Value, RequiredCharge);

            if (IsOwner && _inputReader != null)
                _inputReader.UltimateStarted += TryActivate;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            try
            {
                AffinityEvents.LoadoutApplied -= HandleLoadoutApplied;

                if (IsOwner && _inputReader != null)
                    _inputReader.UltimateStarted -= TryActivate;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        private void Update()
        {
            if (!IsServerInitialized) return;
            if (_passiveChargePerSecond <= 0f) return;
            if (_lifecycle != null && _lifecycle.IsDead) return;

            AddCharge(_passiveChargePerSecond * Time.deltaTime);
        }

        // ------------------------------------------------------------------
        // Charge accumulation (server)
        // ------------------------------------------------------------------

        private void HandleServerDamageApplied(NetworkObject victim, DamageInfo info, float amountApplied)
        {
            if (!IsServerInitialized) return;
            if (amountApplied <= 0f) return;

            NetworkObject self = base.NetworkObject;
            if (self == null) return;

            if (victim == self)
                AddCharge(amountApplied * _chargePerDamageTaken);

            // Excluded when self-inflicted, so a player cannot charge twice off one
            // hit - or farm charge off their own future self-damage sources.
            if (info.Attacker == self && victim != self)
                AddCharge(amountApplied * _chargePerDamageDealt);
        }

        private void HandleServerDied(DamageInfo info)
        {
            if (!IsServerInitialized) return;
            if (_chargeRetainedOnDeath >= 1f) return;

            _charge.Value *= _chargeRetainedOnDeath;
        }

        private void AddCharge(float amount)
        {
            if (amount <= 0f) return;

            // Paused for as long as the ultimate's own active window is
            // running (e.g. Solar Ascension's 8s duration) - otherwise
            // damage dealt/taken and the passive trickle during the ultimate
            // could fully recharge it again before it even ends.
            if (_durationEffect != null && _durationEffect.IsActive) return;

            // Nothing selected yet means nothing to charge toward. Skipping rather
            // than banking prevents charge running away for a player whose loadout
            // never arrives, and the loadout applies at spawn so nothing real is
            // lost.
            float required = RequiredCharge;
            if (required <= 0f) return;

            if (_charge.Value >= required) return;

            _charge.Value = Mathf.Min(required, _charge.Value + amount);
        }

        // ------------------------------------------------------------------
        // Activation
        // ------------------------------------------------------------------

        /// <summary>
        /// Owner-side entry point. Wire a keybind, HUD button or debug key to this.
        /// Silently does nothing when the ultimate is not usable - the server
        /// re-checks everything that matters regardless.
        /// </summary>
        public void TryActivate()
        {
            if (!IsOwner) { Debug.Log($"[{nameof(PlayerUltimate)}] TryActivate blocked: not owner"); return; } // TEMP DEBUG
            if (!IsReady) { Debug.Log($"[{nameof(PlayerUltimate)}] TryActivate blocked: not ready (charge={_charge.Value:F2}, required={RequiredCharge:F2})"); return; } // TEMP DEBUG
            if (_lifecycle != null && _lifecycle.IsDead) { Debug.Log($"[{nameof(PlayerUltimate)}] TryActivate blocked: dead"); return; } // TEMP DEBUG

            // Owner-only gate: meaningless on a server that is not running this
            // player's movement.
            if (_movement != null && !_movement.CanStartMovementAction()) { Debug.Log($"[{nameof(PlayerUltimate)}] TryActivate blocked: CanStartMovementAction() false"); return; } // TEMP DEBUG

            Vector3 aimOrigin = transform.position;
            Vector3 aimDirection = transform.forward;
            if (_weapons != null) _weapons.GetTrustedAimRay(out aimOrigin, out aimDirection);

            Debug.Log($"[{nameof(PlayerUltimate)}] TryActivate passed all gates, sending CmdActivate"); // TEMP DEBUG
            CmdActivate(aimOrigin, aimDirection);
        }

        [ServerRpc]
        private void CmdActivate(Vector3 aimOrigin, Vector3 aimDirection)
        {
            if (!IsServerInitialized) { Debug.Log($"[{nameof(PlayerUltimate)}] CmdActivate blocked: server not initialized"); return; } // TEMP DEBUG

            UltimateDefinition ultimate = _affinity != null ? _affinity.SelectedUltimate : null;
            if (ultimate == null || ultimate.Behavior == null) { Debug.Log($"[{nameof(PlayerUltimate)}] CmdActivate blocked: ultimate={ultimate}, behavior={(ultimate != null ? ultimate.Behavior : null)}"); return; } // TEMP DEBUG

            if (_charge.Value < ultimate.RequiredCharge) { Debug.Log($"[{nameof(PlayerUltimate)}] CmdActivate blocked: server charge={_charge.Value:F2} < required={ultimate.RequiredCharge:F2}"); return; } // TEMP DEBUG
            if (_lifecycle != null && _lifecycle.IsDead) { Debug.Log($"[{nameof(PlayerUltimate)}] CmdActivate blocked: dead (server)"); return; } // TEMP DEBUG

            AffinityRuntimeContext context = _affinity.Context;
            if (context == null) { Debug.Log($"[{nameof(PlayerUltimate)}] CmdActivate blocked: null context"); return; } // TEMP DEBUG

            Debug.Log($"[{nameof(PlayerUltimate)}] CmdActivate activating '{ultimate.DisplayName}'"); // TEMP DEBUG

            ultimate.Behavior.ServerActivate(context, aimOrigin, aimDirection);

            _charge.Value = 0f;
            RpcActivated();
        }

        [ObserversRpc]
        private void RpcActivated()
        {
            // The ultimate itself is already synced via PlayerAffinity, so there is
            // nothing to send here - every peer resolves it locally.
            UltimateDefinition ultimate = _affinity != null ? _affinity.SelectedUltimate : null;

            AffinityEvents.RaiseUltimateActivated(base.NetworkObject, ultimate);
            OnActivated?.Invoke();
        }

        // ------------------------------------------------------------------
        // Change routing
        // ------------------------------------------------------------------

        private void HandleChargeChanged(float prev, float next, bool asServer)
            => OnChargeChanged?.Invoke(next, RequiredCharge);

        private void HandleLoadoutApplied(NetworkObject owner, AffinityLoadout loadout)
        {
            if (owner != base.NetworkObject) return;

            // Charge is unchanged but the denominator just became known, so a meter
            // bound to this event redraws with the right requirement.
            OnChargeChanged?.Invoke(_charge.Value, RequiredCharge);
        }
    }
}
