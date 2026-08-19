// =============================================================================
// SolarAscensionEffect — per-player coordinator for the Solar Ascension
// ultimate's whole active-duration lifecycle: bonus shield, hidden gun +
// fireball fire override, visual scale-up, the movement lift/hold, and the
// generic UltimateDurationEffect countdown. Always present on the Player
// prefab, completely inert unless ServerBeginAscension is called - same
// "always present, usually idle" shape as PlayerGrapple.
//
// SINGLE TEARDOWN PATH:
//   Natural timeout, death (Health.OnServerDied), and disconnect (OnStopServer)
//   all funnel into one idempotent ServerEndAscension() so every system this
//   ultimate touches is guaranteed to revert exactly once, regardless of how
//   the activation ends. The player is NOT invulnerable during the ultimate -
//   death simply ends it early via the same path as a natural timeout.
//
// AUTHORITY:
//   ServerBeginAscension/ServerEndAscension run server-only. The movement
//   lift and the owner-local fire bypass are reached via TargetRpc, exactly
//   like PlayerGrapple reaches its owner - see IAbilityMovementDriver.cs and
//   MovementStateMachine.BeginAbilityMovement. The visual scale-up is an
//   ObserversRpc since every peer needs to see it, not just the owner.
//
// MANUAL SETUP:
//   Add to the Player prefab root, alongside PlayerUltimate/PlayerAffinity/
//   UltimateDurationEffect. Every reference auto-resolves via GetComponent on
//   this same GameObject except _thirdPersonBodyRoot, which auto-resolves by
//   name ("Third Person Body") since it's a child transform, not a sibling
//   component - assign explicitly in the Inspector only if that name ever
//   changes.
// =============================================================================

using System;
using System.Collections;
using FishNet.Connection;
using FishNet.Object;
using OffAngle.Movement;
using OffAngle.Movement.Abilities;
using OffAngle.Networking;
using OffAngle.Weapons;
using UnityEngine;

namespace OffAngle.Combat
{
    public class SolarAscensionEffect : NetworkBehaviour
    {
        [Header("References (leave null to auto-resolve on this GameObject)")]
        [SerializeField] private MovementStateMachine _movement;
        [SerializeField] private PlayerWeaponController _weaponController;
        [SerializeField] private PlayerWeaponEquipper _weaponEquipper;
        [SerializeField] private Shield _shield;
        [SerializeField] private UltimateDurationEffect _durationEffect;
        [SerializeField] private Health _health;

        [Tooltip("The player's visual model root to scale up while ascended - NOT the CharacterController root. Leave null to auto-resolve the child named 'Third Person Body'. Scaling this also scales the body/head hit colliders parented under it - an accepted tradeoff (a bigger, easier target while airborne/immobile/gun-hidden), see the implementation plan.")]
        [SerializeField] private Transform _thirdPersonBodyRoot;

        private bool _active;
        private float _activeBonusShield;
        private Coroutine _countdownRoutine;

        private void Awake()
        {
            if (_movement == null) _movement = GetComponent<MovementStateMachine>();
            if (_weaponController == null) _weaponController = GetComponent<PlayerWeaponController>();
            if (_weaponEquipper == null) _weaponEquipper = GetComponent<PlayerWeaponEquipper>();
            if (_shield == null) _shield = GetComponent<Shield>();
            if (_durationEffect == null) _durationEffect = GetComponent<UltimateDurationEffect>();
            if (_health == null) _health = GetComponent<Health>();

            if (_thirdPersonBodyRoot == null)
            {
                Transform found = transform.Find("Third Person Body");
                if (found != null) _thirdPersonBodyRoot = found;
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (_health != null)
                _health.OnServerDied += HandleServerDied;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            // Runs synchronously as part of FishNet's despawn broadcast - contain
            // any exception here rather than letting it escape into the network
            // transport.
            try
            {
                if (_health != null)
                    _health.OnServerDied -= HandleServerDied;

                if (_active)
                    ServerEndAscension();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        private void OnDestroy()
        {
            try
            {
                if (_health != null)
                    _health.OnServerDied -= HandleServerDied;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        /// <summary>
        /// Server-only. Called by SolarAscensionUltimateBehavior.ServerActivate.
        /// No-op if an activation is already in progress.
        /// </summary>
        public void ServerBeginAscension(
            float duration, float riseHeight, float riseTime, float scaleMultiplier,
            float bonusShield, GunData fireballData, ProjectileShotBehavior fireballBehavior)
        {
            if (!IsServerInitialized || _active) return;

            _active = true;
            _activeBonusShield = Mathf.Max(0f, bonusShield);

            if (_activeBonusShield > 0f)
                _shield?.AddBonusMaxShield(_activeBonusShield);

            _weaponController?.ServerSetAscensionFireOverride(fireballData, fireballBehavior, true);
            _weaponEquipper?.ServerSetWeaponHiddenOverride(true);
            _durationEffect?.ServerBegin(duration);

            RpcSetAscendedVisual(true, scaleMultiplier);
            TargetRpcBeginAscensionMovement(base.Owner, riseHeight, riseTime, duration);

            if (_countdownRoutine != null) StopCoroutine(_countdownRoutine);
            _countdownRoutine = StartCoroutine(ServerCountdown(duration));
        }

        private IEnumerator ServerCountdown(float duration)
        {
            yield return new WaitForSeconds(duration);
            ServerEndAscension();
        }

        private void HandleServerDied(DamageInfo info) => ServerEndAscension();

        /// <summary>Server-only, idempotent. Safe to call from the countdown, from death, or from OnStopServer.</summary>
        private void ServerEndAscension()
        {
            if (!_active) return;
            _active = false;

            if (_countdownRoutine != null)
            {
                StopCoroutine(_countdownRoutine);
                _countdownRoutine = null;
            }

            if (_activeBonusShield > 0f)
                _shield?.RemoveBonusMaxShield(_activeBonusShield);
            _activeBonusShield = 0f;

            _weaponController?.ServerSetAscensionFireOverride(null, null, false);
            _weaponEquipper?.ServerSetWeaponHiddenOverride(false);
            _durationEffect?.ServerEnd();

            RpcSetAscendedVisual(false, 1f);
            TargetRpcEndAscensionMovement(base.Owner);
        }

        [ObserversRpc]
        private void RpcSetAscendedVisual(bool ascended, float scaleMultiplier)
        {
            if (_thirdPersonBodyRoot == null) return;
            _thirdPersonBodyRoot.localScale = Vector3.one * (ascended ? Mathf.Max(0.01f, scaleMultiplier) : 1f);
        }

        [TargetRpc]
        private void TargetRpcBeginAscensionMovement(NetworkConnection conn, float riseHeight, float riseTime, float duration)
        {
            _movement?.BeginAbilityMovement(new SolarAscensionDriver(riseHeight, riseTime, duration));
            _weaponController?.SetOwnerAscensionFireActive(true);
        }

        [TargetRpc]
        private void TargetRpcEndAscensionMovement(NetworkConnection conn)
        {
            _weaponController?.SetOwnerAscensionFireActive(false);

            // No-op if the driver already self-completed; forces an end if the
            // server's independent timer fires slightly before the owner's own -
            // see SolarAscensionDriver's SELF-TIMED note. Safe to call
            // unconditionally: while ascending, no other ability can be active
            // (TryActivate already required CanStartMovementAction() before
            // this began, and IsInAbilityMovement blocks new abilities from
            // starting for as long as this driver holds it).
            if (_movement != null && _movement.IsInAbilityMovement)
                _movement.InterruptCurrentAction();
        }
    }
}
