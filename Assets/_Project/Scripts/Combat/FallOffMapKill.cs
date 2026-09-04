// =============================================================================
// FallOffMapKill — server-only instant death when this entity drops below
// the scene WorldKillPlane.
//
// Lives on the player (and any other Health entity that should fall-kill).
// Clients do not evaluate this; a missing plane means "no cutoff this map".
//
// Death goes through Health.ApplyDamage with a null attacker so KillCount
// awards nobody and CombatMemory records no relationship. PlayerLifecycle
// / Respawner already subscribe to OnServerDied.
//
// RESPAWN / CLIENT-AUTHORITATIVE TRANSFORM:
//   Respawner warps the OWNER via TargetRpc; NetworkTransform then copies
//   that pose to the server. ResetHealth runs immediately, so for a few
//   frames the server body is still below KillY with a live Health. Killing
//   on that stale pose would loop: die → respawn → die. _armed stays false
//   until this life has been observed at or above the plane (spawn, or any
//   in-bounds position). Falling off then arms the kill as usual.
//
// MANUAL SETUP:
//   Add this component to the Player prefab root (same GameObject as Health).
//   Health/Shield resolve via GetComponent if left unassigned.
// =============================================================================

using FishNet.Object;
using UnityEngine;

namespace OffAngle.Combat
{
    public class FallOffMapKill : NetworkBehaviour
    {
        public const string DeathWeaponLabel = "Fall";

        [Tooltip("Leave null to auto-resolve via GetComponent on this GameObject.")]
        [SerializeField] private Health _health;

        [Tooltip("Optional. Leave null to auto-resolve. Used only to size lethal damage through current shield.")]
        [SerializeField] private Shield _shield;

        // False after death and until the server has seen this body above the
        // kill plane this life. See header "RESPAWN / CLIENT-AUTHORITATIVE TRANSFORM".
        private bool _armed;

        private void Awake()
        {
            if (_health == null) _health = GetComponent<Health>();
            if (_shield == null) _shield = GetComponent<Shield>();
        }

        private void Update()
        {
            if (!IsServerInitialized) return;
            if (_health == null || _health.IsDead)
            {
                _armed = false;
                return;
            }

            WorldKillPlane plane = WorldKillPlane.Instance;
            if (plane == null) return;

            if (transform.position.y >= plane.KillY)
            {
                _armed = true;
                return;
            }

            if (!_armed) return;

            float amount = _health.MaxHealth + 1f;
            if (_shield != null)
                amount += _shield.MaxShield;

            DamageInfo info = new DamageInfo(
                amount,
                attacker: null,
                weapon: null,
                AffinityType.None,
                transform.position,
                Vector3.up,
                DamageCategory.Normal);

            _health.ApplyDamage(info);
        }
    }
}
