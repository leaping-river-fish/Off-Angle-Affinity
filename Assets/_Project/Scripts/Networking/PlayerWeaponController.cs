// =============================================================================
// PlayerWeaponController — bridges owner input to server-authoritative fire.
//
// This is the SECOND (and last) FishNet script on the player prefab besides
// NetworkPlayerController. It follows the same isolation pattern: weapon logic
// (Gun/GunData) does not import FishNet; this class is the only place where
// input meets RPCs.
//
// FLOW:
//   1. Owner client sees PlayerInputReader.FireStarted.
//   2. Local Gun.TryFire() gates the ServerRpc rate to avoid spam.
//   3. CmdFire(origin, direction) is sent to the server.
//   4. Server re-validates fire rate (with a small grace for network jitter).
//   5. Server does Physics.Raycast in the trusted direction.
//   6. RpcPlayTracer broadcasts the shot's start/end points to every peer
//      (hit or miss) so a BulletTracer streak renders locally on each client.
//   7. Server resolves IDamageable and calls ApplyDamage(DamageInfo).
//   8. Health SyncVar propagates + ObserversRpc fires damage-number feedback.
//
// AUTHORITY NOTE:
//   Origin/direction are trusted from the client because the server does not
//   simulate the client's camera. Fire rate and range are enforced server-side.
//   Aim-through-walls or teleport-to-target style cheats would need extra
//   validation (line-of-sight, position sanity) — out of scope for prototype.
// =============================================================================

using FishNet.Object;
using OffAngle.Combat;
using OffAngle.Core;
using OffAngle.Weapons;
using UnityEngine;

namespace OffAngle.Networking
{
    public class PlayerWeaponController : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private PlayerInputReader _inputReader;
        [SerializeField] private Gun _gun;

        [Tooltip("Transform whose position/forward defines the aim ray on the owner client (usually the player camera).")]
        [SerializeField] private Transform _cameraTransform;

        [Header("Server validation")]
        [Tooltip("Fraction of the fire interval the server allows as jitter grace. 0.05 = 5% early accepted.")]
        [SerializeField, Range(0f, 0.5f)] private float _serverFireRateGrace = 0.05f;

        [Header("Feedback")]
        [Tooltip("Pure-visual tracer spawned locally on every peer for each shot (hit or miss). Not networked itself — only the start/end points travel over RpcPlayTracer.")]
        [SerializeField] private BulletTracer _tracerPrefab;

        private float _serverNextAllowedFireTime;

        // ------------------------------------------------------------------
        // Lifecycle — subscribe only for the owning client
        // ------------------------------------------------------------------

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!base.IsOwner) return;
            if (_inputReader == null || _gun == null) return;

            _inputReader.FireStarted += HandleFireStarted;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!base.IsOwner) return;
            if (_inputReader == null) return;

            _inputReader.FireStarted -= HandleFireStarted;
        }

        // ------------------------------------------------------------------
        // Owner-side path
        // ------------------------------------------------------------------

        private void HandleFireStarted()
        {
            if (_gun == null || _gun.Data == null) return;
            if (!_gun.TryFire()) return;

            Vector3 origin = _cameraTransform != null ? _cameraTransform.position : transform.position;
            Vector3 direction = _cameraTransform != null ? _cameraTransform.forward : transform.forward;

            CmdFire(origin, direction);
        }

        // ------------------------------------------------------------------
        // Server-side path
        // ------------------------------------------------------------------

        [ServerRpc]
        private void CmdFire(Vector3 origin, Vector3 direction)
        {
            if (_gun == null || _gun.Data == null) return;

            GunData data = _gun.Data;

            // Rate validation. Grace is small enough that the client can't reliably beat it.
            float now = Time.time;
            if (now < _serverNextAllowedFireTime) return;
            float interval = (1f / Mathf.Max(0.01f, data.FireRate)) * (1f - _serverFireRateGrace);
            _serverNextAllowedFireTime = now + interval;

            if (data.ShotType != ShotType.Hitscan) return; // Projectile not implemented.

            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            bool didHit = Physics.Raycast(origin, direction, out RaycastHit hit, data.Range, data.HitMask, QueryTriggerInteraction.Ignore);

            // Tracer fires regardless of hit/miss — bullets are visible even when they go nowhere.
            Vector3 tracerEnd = didHit ? hit.point : origin + direction * data.Range;
            Vector3 tracerStart = _gun.FirePoint != null ? _gun.FirePoint.position : origin;
            RpcPlayTracer(tracerStart, tracerEnd);

            if (!didHit) return;

            IDamageable damageable = hit.collider.GetComponentInParent<IDamageable>();
            if (damageable == null) return;

            // Ignore self-hits (our own capsule/CharacterController).
            if (damageable is Component damageableComponent
                && damageableComponent.transform.root == transform.root)
            {
                return;
            }

            DamageInfo info = new DamageInfo(
                amount:   data.Damage,
                attacker: base.NetworkObject,
                weapon:   data,
                affinity: data.Affinity,
                hitPoint: hit.point,
                hitNormal: hit.normal);

            damageable.ApplyDamage(info);
        }

        // ------------------------------------------------------------------
        // Tracer feedback (pure UX — never mutates game state)
        // ------------------------------------------------------------------

        [ObserversRpc]
        private void RpcPlayTracer(Vector3 start, Vector3 end)
        {
            if (_tracerPrefab == null) return;

            BulletTracer tracer = Instantiate(_tracerPrefab, start, Quaternion.identity);
            tracer.Play(start, end);
        }
    }
}
