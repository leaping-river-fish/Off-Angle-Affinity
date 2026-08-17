// =============================================================================
// Gun — plain MonoBehaviour that pairs a GunData asset with a firePoint.
//
// Zero networking. PlayerWeaponController owns the fire pipeline; Gun just
// exposes the data reference and provides a local cooldown gate used to avoid
// mashing the ServerRpc more often than the fire rate allows. The server
// re-validates that same rate authoritatively.
//
// A gun prefab holds the visual mesh + a FirePoint child transform + this
// component. The prefab is attached (as a child) to WeaponHolder on the player.
// =============================================================================

using System;
using UnityEngine;

namespace OffAngle.Weapons
{
    public class Gun : MonoBehaviour
    {
        [SerializeField] private GunData _data;
        [SerializeField] private Transform _firePoint;
        [SerializeField] private GameObject _particleSystem;
        [Tooltip("Optional. Marks the gun's physical iron sight/reticle point. Its local +Z axis (blue arrow) must point along the sighting line (rear sight -> front sight -> target). When assigned, WeaponAdsController solves the ADS pose so this point/direction lines up with the camera instead of using AdsLocalPosition/AdsLocalRotation below. Leave unassigned to keep the hand-authored offset.")]
        [SerializeField] private Transform _sightVector;

        [Tooltip("Optional. Drives fire/reload animations via the \"" + FireAnimTrigger + "\"/\"" + ReloadAnimTrigger + "\" trigger parameters. Leave unassigned to skip animation entirely - a missing Animator, controller, or trigger parameter is treated as \"no animation\" rather than an error.")]
        [SerializeField] private Animator _animator;

        public GunData Data => _data;
        public Transform FirePoint => _firePoint;
        public GameObject ParticleSystem => _particleSystem;
        public Transform SightVector => _sightVector;
        public int  MagazineAmmo => _magazineAmmo;
        public int  ReserveAmmo => _reserveAmmo;
        public bool IsReloading => _isReloading;

        /// <summary>
        /// Raised once per shot that should actually be fired, after the
        /// current FireMode has decided it's time. Only raised for Instant
        /// shot behaviors. PlayerWeaponController subscribes to this and
        /// sends CmdFire for each invocation.
        /// </summary>
        public event Action RequestFire;

        /// <summary>
        /// Raised instead of RequestFire for Continuous/Charged shot
        /// behaviors, which ignore FireMode and define their own
        /// hold-to-sustain / hold-to-charge semantics. PlayerWeaponController
        /// uses these to drive beam/charge networking.
        /// </summary>
        public event Action HoldStarted;
        public event Action HoldStopped;

        /// <summary>
        /// How the currently assigned ShotBehavior wants trigger input wired.
        /// Defaults to Instant (preserving today's behavior) when no
        /// ShotBehavior is assigned - see ShotBehavior.cs / ShotDeliveryKind.cs.
        /// </summary>
        private ShotDeliveryKind CurrentKind =>
            _data != null && _data.ShotBehavior != null ? _data.ShotBehavior.Kind : ShotDeliveryKind.Instant;

        private float _localCooldownUntil;
        private bool  _isTriggerHeld;
        private int   _burstShotsRemaining;

        private int  _magazineAmmo;
        private int  _reserveAmmo;
        private bool _isReloading;

        // Set by PlayerWeaponController.SetFireLocked, itself driven by
        // PlayerLifecycleController on death/respawn. This is the seam this
        // class already asks callers to use rather than adding IsDead checks
        // elsewhere.
        private bool _locked;

        /// <summary>
        /// The single source of truth for "would firing succeed right now?" -
        /// ammo, reload state, and fire-rate cooldown. Future systems (ADS
        /// lock, stun, affinity effects) should add checks here rather than
        /// scattering them through PlayerWeaponController or elsewhere.
        /// </summary>
        public bool CanFire()
        {
            if (_locked) { LogCantFire("locked"); return false; }
            if (_data == null) { LogCantFire("no data"); return false; }
            if (_isReloading) { LogCantFire("reloading"); return false; }
            if (_magazineAmmo <= 0) { LogCantFire("empty magazine"); return false; }
            if (Time.time < _localCooldownUntil) { LogCantFire("local cooldown"); return false; }
            _lastLoggedCantFireReason = null;
            return true;
        }

        // Temporary diagnostic for the "shot silently dropped" bug (see
        // PlayerWeaponController.LogFireRejected for the server-side
        // counterpart). CanFire() is polled every Update() for
        // Automatic/Burst fire modes, so this only logs when the failure
        // reason actually changes to avoid flooding the console while a held
        // trigger keeps failing for the same reason. Safe to remove once the
        // root cause behind the "totally can't fire" reports is confirmed.
        private string _lastLoggedCantFireReason;
        private void LogCantFire(string reason)
        {
            if (_lastLoggedCantFireReason == reason) return;
            _lastLoggedCantFireReason = reason;
            Debug.Log($"[{nameof(Gun)}] {name} CanFire() failed: {reason}");
        }

        public bool CanReload()
        {
            if (_locked) return false;
            if (_data == null) return false;
            if (_isReloading) return false;
            if (_magazineAmmo >= _data.MagazineSize) return false;
            if (_reserveAmmo <= 0) return false;
            return true;
        }

        /// <summary>
        /// Locks/unlocks CanFire()/CanReload() wholesale. Called by
        /// PlayerWeaponController.SetFireLocked, which PlayerLifecycleController
        /// drives on death (locked) and respawn (unlocked). Locking also stops
        /// an in-progress Automatic/Burst hold from continuing to fire, and
        /// raises HoldStopped if a Continuous/Charged behavior was mid-hold so
        /// PlayerWeaponController can tell the server to stop it too.
        /// </summary>
        public void SetLocked(bool locked)
        {
            bool wasHeld = _isTriggerHeld;
            _locked = locked;
            if (_locked)
            {
                if (wasHeld && CurrentKind != ShotDeliveryKind.Instant)
                    HoldStopped?.Invoke();

                _isTriggerHeld = false;
                _burstShotsRemaining = 0;
            }
        }

        /// <summary>
        /// Client-side UX cooldown gate. Returns true and stamps the cooldown
        /// if CanFire() currently allows it. The server is still authoritative
        /// over both ammo and rate - this only prevents unnecessary ServerRpc spam.
        /// </summary>
        public bool TryFire()
        {
            if (!CanFire()) return false;
            _localCooldownUntil = Time.time + (1f / Mathf.Max(0.01f, _data.FireRate));
            return true;
        }

        /// <summary>
        /// Called on trigger press. For Instant shot behaviors: Semi-auto
        /// fires immediately and does nothing further until the next press.
        /// Automatic fires immediately and keeps firing in Update() while
        /// held. Burst starts a fixed-length burst that runs to completion in
        /// Update() regardless of hold state - re-pressing while a burst is in
        /// progress does not start another one.
        ///
        /// For Continuous/Charged shot behaviors, FireMode is ignored entirely
        /// - this just raises HoldStarted once (gated by CanFire()) and lets
        /// PlayerWeaponController drive the rest.
        /// </summary>
        public void StartFire()
        {
            _isTriggerHeld = true;
            if (_data == null) return;

            if (CurrentKind != ShotDeliveryKind.Instant)
            {
                if (!CanFire()) return;
                HoldStarted?.Invoke();
                return;
            }

            if (_data.FireMode == FireMode.Burst)
            {
                if (_burstShotsRemaining > 0) return;
                if (!CanFire()) return;
                _burstShotsRemaining = Mathf.Max(1, _data.BurstCount);
            }
            AttemptFire();
        }

        /// <summary>Called on trigger release. Stops Automatic and any Continuous/Charged hold; has no effect on a Burst already in progress.</summary>
        public void StopFire()
        {
            bool wasHeld = _isTriggerHeld;
            _isTriggerHeld = false;

            if (wasHeld && CurrentKind != ShotDeliveryKind.Instant)
                HoldStopped?.Invoke();
        }

        private void Update()
        {
            if (_data == null) return;
            if (CurrentKind != ShotDeliveryKind.Instant) return; // Continuous/Charged behaviors don't use the discrete FireMode loop.

            switch (_data.FireMode)
            {
                case FireMode.Automatic:
                    if (_isTriggerHeld) AttemptFire();
                    break;
                case FireMode.Burst:
                    if (_burstShotsRemaining <= 0) break;
                    if (_magazineAmmo <= 0) { _burstShotsRemaining = 0; break; }
                    AttemptFire();
                    break;
            }
        }

        private void AttemptFire()
        {
            if (!TryFire()) return;
            if (_data.FireMode == FireMode.Burst)
                _burstShotsRemaining--;
            RequestFire?.Invoke();
            PlayFireAnimation();
        }

        // ------------------------------------------------------------------
        // Animation - both triggers are best-effort. A gun with no Animator,
        // no controller, or a controller that hasn't been given the matching
        // trigger parameter yet just silently plays no animation instead of
        // logging Unity's "parameter does not exist" warning or breaking fire/
        // reload logic, which never waits on animation state.
        // ------------------------------------------------------------------

        private const string FireAnimTrigger = "Fire";
        private const string ReloadAnimTrigger = "Reload";

        private void PlayFireAnimation() => TryPlayAnimationTrigger(FireAnimTrigger);
        private void PlayReloadAnimation() => TryPlayAnimationTrigger(ReloadAnimTrigger);

        private void TryPlayAnimationTrigger(string trigger)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return;
            if (!HasTriggerParameter(trigger)) return;
            _animator.SetTrigger(trigger);
        }

        private bool HasTriggerParameter(string parameterName)
        {
            foreach (AnimatorControllerParameter parameter in _animator.parameters)
            {
                if (parameter.type == AnimatorControllerParameterType.Trigger && parameter.name == parameterName)
                    return true;
            }
            return false;
        }

        public void ResetCooldown()
        {
            _localCooldownUntil = 0f;
        }

        /// <summary>
        /// Called by PlayerWeaponController whenever its networked ammo state
        /// changes (including the initial seed on OnStartClient). Gun never
        /// mutates ammo itself - it only mirrors what the server says, so
        /// CanFire()/CanReload() can answer instantly without an RPC round-trip.
        /// </summary>
        public void SetAmmoState(int magazineAmmo, int reserveAmmo, bool isReloading)
        {
            bool reloadJustStarted = isReloading && !_isReloading;

            _magazineAmmo = magazineAmmo;
            _reserveAmmo = reserveAmmo;
            _isReloading = isReloading;

            if (reloadJustStarted) PlayReloadAnimation();
        }

        // Editor-only visual aid for placing _sightVector - draws its local
        // +Z (the sighting line WeaponAdsController aligns to the camera) as
        // an arrow so it can be oriented correctly by eye.
        private void OnDrawGizmosSelected()
        {
            if (_sightVector == null) return;

            Gizmos.color = Color.cyan;
            Vector3 origin = _sightVector.position;
            Vector3 tip = origin + _sightVector.forward * 0.3f;
            Gizmos.DrawLine(origin, tip);
            Gizmos.DrawSphere(tip, 0.01f);
        }
    }
}
