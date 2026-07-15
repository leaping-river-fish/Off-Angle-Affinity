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
//   7. Server reads the hit collider's Hitbox (if any) to resolve a HitZone,
//      then resolves IDamageable and calls ApplyDamage(DamageInfo) with the
//      zone-appropriate amount/category (e.g. GunData.HeadshotDamage + Critical
//      for HitZone.Head). Unmarked colliders default to HitZone.Body.
//   8. Health SyncVar propagates + ObserversRpc fires damage-number feedback.
//
// AUTHORITY NOTE:
//   Origin/direction are trusted from the client because the server does not
//   simulate the client's camera. Fire rate and range are enforced server-side.
//   Aim-through-walls or teleport-to-target style cheats would need extra
//   validation (line-of-sight, position sanity) — out of scope for prototype.
// =============================================================================

using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Combat;
using OffAngle.Core;
using OffAngle.Weapons;
using System;
using System.Collections;
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

        [Tooltip("Optional. When assigned, CmdFire/CmdReload reject requests while this reports the player as dead - a server-side backstop in case a modified client bypasses the owner-side Gun lock.")]
        [SerializeField] private PlayerLifecycleController _lifecycle;

        [Header("Server validation")]
        [Tooltip("Fraction of the fire interval the server allows as jitter grace. 0.05 = 5% early accepted.")]
        [SerializeField, Range(0f, 0.5f)] private float _serverFireRateGrace = 0.05f;

        [Header("Feedback")]
        [Tooltip("Pure-visual tracer spawned locally on every peer for each shot (hit or miss). Not networked itself — only the start/end points travel over RpcPlayTracer.")]
        [SerializeField] private BulletTracer _tracerPrefab;

        private float _serverNextAllowedFireTime;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<int>  _magazineAmmo = new SyncVar<int>();
        private readonly SyncVar<int>  _reserveAmmo  = new SyncVar<int>();
        private readonly SyncVar<bool> _isReloading  = new SyncVar<bool>();

        private Coroutine _reloadRoutine;

        public int  MagazineAmmo => _magazineAmmo.Value;
        public int  ReserveAmmo  => _reserveAmmo.Value;
        public bool IsReloading  => _isReloading.Value;
        /// <summary>Raised on every peer whenever any ammo SyncVar changes, including the initial seed. HUD subscribes here.</summary>
        public event Action<int, int, bool> OnAmmoChanged;

        // ------------------------------------------------------------------
        // Lifecycle — subscribe only for the owning client
        // ------------------------------------------------------------------

        public override void OnStartClient()
        {
            base.OnStartClient();

            PushAmmoState();

            if (!base.IsOwner) return;
            if (_inputReader == null || _gun == null) return;

            _inputReader.FireStarted += HandleFireStarted;
            _inputReader.FireCanceled += HandleFireCanceled;
            _inputReader.ReloadStarted += HandleReloadStarted;
            _gun.RequestFire += HandleRequestFire;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!base.IsOwner) return;
            if (_inputReader == null) return;

            _inputReader.FireStarted -= HandleFireStarted;
            _inputReader.FireCanceled -= HandleFireCanceled;
            _inputReader.ReloadStarted -= HandleReloadStarted;
            _gun.RequestFire -= HandleRequestFire;
        }

        private void Awake()
        {
            _magazineAmmo.OnChange += HandleAmmoIntChanged;
            _reserveAmmo.OnChange  += HandleAmmoIntChanged;
            _isReloading.OnChange  += HandleReloadingChanged;
        }
        private void OnDestroy()
        {
            _magazineAmmo.OnChange -= HandleAmmoIntChanged;
            _reserveAmmo.OnChange  -= HandleAmmoIntChanged;
            _isReloading.OnChange  -= HandleReloadingChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            SeedAmmoFromData();
        }

        private void SeedAmmoFromData()
        {
            if (_gun == null || _gun.Data == null) return;
            _magazineAmmo.Value = _gun.Data.MagazineSize;
            _reserveAmmo.Value  = _gun.Data.StartingReserveAmmo;
            _isReloading.Value  = false;
        }
        /// <summary>
        /// Owner-side gameplay lock. Called by PlayerLifecycleController on death
        /// (locked) and respawn (unlocked). Passes straight through to Gun,
        /// which is the single seam CanFire()/CanReload() already gate on -
        /// no separate IsDead check needed here or in Gun's callers.
        /// </summary>
        public void SetFireLocked(bool locked)
        {
            _gun?.SetLocked(locked);
        }

        /// <summary>Server-only. Cancels any in-progress reload and refills ammo to the weapon's starting values. Called by Respawner on respawn.</summary>
        public void ServerResetAmmo()
        {
            if (!IsServerInitialized) return;
            if (_reloadRoutine != null)
            {
                StopCoroutine(_reloadRoutine);
                _reloadRoutine = null;
            }
            SeedAmmoFromData();
        }

        // ------------------------------------------------------------------
        // Owner-side path
        // ------------------------------------------------------------------

        private void HandleFireStarted()
        {
            if (_gun == null || _gun.Data == null) return;
            _gun.StartFire();
        }

        private void HandleFireCanceled()
        {
            if (_gun == null) return;
            _gun.StopFire();
        }
        private void HandleReloadStarted()
        {
            if (_gun == null) return;
            if (!_gun.CanReload()) return;

            CmdReload();
        }

        private void HandleRequestFire()
        {
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

            // Defense in depth: the owner-side Gun lock should already stop this
            // RPC from ever being sent while dead, but the server never trusts
            // the client - re-check authoritative life state here too.
            if (_lifecycle != null && _lifecycle.IsDead) return;

            GunData data = _gun.Data;

            // Rate validation. Grace is small enough that the client can't reliably beat it.
            float now = Time.time;
            if (now < _serverNextAllowedFireTime) return;
            float interval = (1f / Mathf.Max(0.01f, data.FireRate)) * (1f - _serverFireRateGrace);
            _serverNextAllowedFireTime = now + interval;

            if (_isReloading.Value) return;
            if (_magazineAmmo.Value <= 0) return;

            _magazineAmmo.Value--;

            if (_magazineAmmo.Value <= 0 && data.AutoReloadOnEmpty && _reserveAmmo.Value > 0)
            {
                TryServerBeginReload();
            }

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

            Hitbox hitbox = hit.collider.GetComponent<Hitbox>();
            HitZone zone = hitbox != null ? hitbox.Zone : HitZone.Body;

            float amount = zone == HitZone.Head ? data.HeadshotDamage : data.Damage;
            DamageCategory category = zone == HitZone.Head ? DamageCategory.Critical : DamageCategory.Normal;

            DamageInfo info = new DamageInfo(
                amount:   amount,
                attacker: base.NetworkObject,
                weapon:   data,
                affinity: data.Affinity,
                hitPoint: hit.point,
                hitNormal: hit.normal,
                category: category);

            damageable.ApplyDamage(info);
        }

        [ServerRpc]
        private void CmdReload()
        {
            if (_lifecycle != null && _lifecycle.IsDead) return;
            TryServerBeginReload();
        }
        /// <summary>
        /// Starts a reload if the current state allows it. Used by both the
        /// manual CmdReload path and the auto-reload trigger inside CmdFire -
        /// one server-side entry point, no duplicated validation.
        /// </summary>
        private bool TryServerBeginReload()
        {
            if (_gun == null || _gun.Data == null) return false;
            if (_isReloading.Value) return false;
            if (_magazineAmmo.Value >= _gun.Data.MagazineSize) return false;
            if (_reserveAmmo.Value <= 0) return false;

            _isReloading.Value = true;
            _reloadRoutine = StartCoroutine(ServerReloadRoutine(_gun.Data.ReloadTime));
            return true;
        }
        private IEnumerator ServerReloadRoutine(float reloadTime)
        {
            yield return new WaitForSeconds(reloadTime);

            if (_gun != null && _gun.Data != null)
            {
                int needed = _gun.Data.MagazineSize - _magazineAmmo.Value;
                int amountToLoad = Mathf.Min(needed, _reserveAmmo.Value);
                _magazineAmmo.Value += amountToLoad;
                _reserveAmmo.Value -= amountToLoad;
            }
            _isReloading.Value = false;
            _reloadRoutine = null;
        }

        private void HandleAmmoIntChanged(int prev, int next, bool asServer) => PushAmmoState();
        private void HandleReloadingChanged(bool prev, bool next, bool asServer) => PushAmmoState();
        private void PushAmmoState()
        {
            if (_gun != null)
                _gun.SetAmmoState(_magazineAmmo.Value, _reserveAmmo.Value, _isReloading.Value);
            OnAmmoChanged?.Invoke(_magazineAmmo.Value, _reserveAmmo.Value, _isReloading.Value);
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
