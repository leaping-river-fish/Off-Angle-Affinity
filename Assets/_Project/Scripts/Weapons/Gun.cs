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

using UnityEngine;

namespace OffAngle.Weapons
{
    public class Gun : MonoBehaviour
    {
        [SerializeField] private GunData _data;
        [SerializeField] private Transform _firePoint;

        public GunData Data => _data;
        public Transform FirePoint => _firePoint;

        private float _localCooldownUntil;

        /// <summary>
        /// Client-side UX cooldown gate. Returns true and stamps the cooldown
        /// if enough time has passed since the last local fire. The server is
        /// still authoritative — this only prevents unnecessary ServerRpc spam.
        /// </summary>
        public bool TryFire()
        {
            if (_data == null) return false;

            float now = Time.time;
            if (now < _localCooldownUntil) return false;

            _localCooldownUntil = now + (1f / Mathf.Max(0.01f, _data.FireRate));
            return true;
        }

        public void ResetCooldown()
        {
            _localCooldownUntil = 0f;
        }
    }
}
