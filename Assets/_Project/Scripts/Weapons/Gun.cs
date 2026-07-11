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

        public GunData Data => _data;
        public Transform FirePoint => _firePoint;
        public int  MagazineAmmo => _magazineAmmo;
        public int  ReserveAmmo => _reserveAmmo;
        public bool IsReloading => _isReloading;

        /// <summary>
        /// Raised once per shot that should actually be fired, after the
        /// current FireMode has decided it's time. PlayerWeaponController
        /// subscribes to this and sends CmdFire for each invocation.
        /// </summary>
        public event Action RequestFire;

        private float _localCooldownUntil;
        private bool  _isTriggerHeld;
        private int   _burstShotsRemaining;

        private int  _magazineAmmo;
        private int  _reserveAmmo;
        private bool _isReloading;

        /// <summary>
        /// The single source of truth for "would firing succeed right now?" -
        /// ammo, reload state, and fire-rate cooldown. Future systems (ADS
        /// lock, stun, affinity effects) should add checks here rather than
        /// scattering them through PlayerWeaponController or elsewhere.
        /// </summary>
        public bool CanFire()
        {
            if (_data == null) return false;
            if (_isReloading) return false;
            if (_magazineAmmo <= 0) return false;
            if (Time.time < _localCooldownUntil) return false;
            return true;
        }

        public bool CanReload()
        {
            if (_data == null) return false;
            if (_isReloading) return false;
            if (_magazineAmmo >= _data.MagazineSize) return false;
            if (_reserveAmmo <= 0) return false;
            return true;
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
        /// Called on trigger press. Semi-auto fires immediately and does
        /// nothing further until the next press. Automatic fires immediately
        /// and keeps firing in Update() while held. Burst starts a fixed-length
        /// burst that runs to completion in Update() regardless of hold state -
        /// re-pressing while a burst is in progress does not start another one.
        /// </summary>
        public void StartFire()
        {
            _isTriggerHeld = true;
            if (_data == null) return;

            if (_data.FireMode == FireMode.Burst)
            {
                if (_burstShotsRemaining > 0) return;
                if (!CanFire()) return;
                _burstShotsRemaining = Mathf.Max(1, _data.BurstCount);
            }
            AttemptFire();
        }

        /// <summary>Called on trigger release. Stops Automatic; has no effect on a Burst already in progress.</summary>
        public void StopFire()
        {
            _isTriggerHeld = false;
        }

        private void Update()
        {
            if (_data == null) return;
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
            _magazineAmmo = magazineAmmo;
            _reserveAmmo = reserveAmmo;
            _isReloading = isReloading;
        }
    }
}
