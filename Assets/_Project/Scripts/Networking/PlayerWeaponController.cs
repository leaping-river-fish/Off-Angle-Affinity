// =============================================================================
// PlayerWeaponController — bridges owner input to server-authoritative fire.
//
// This is the SECOND (and last) FishNet script on the player prefab besides
// NetworkPlayerController. It follows the same isolation pattern: weapon logic
// (Gun/GunData/ShotBehavior) does not import FishNet; this class is the only
// place where input meets RPCs. It also implements IShotBehaviorHost, the seam
// ShotBehavior assets use to reach networking (see IShotBehaviorHost.cs).
//
// FLOW (Instant shot behaviors - Hitscan/Shotgun/Projectile):
//   1. Owner client sees PlayerInputReader.FireStarted.
//   2. Local Gun.TryFire() gates the ServerRpc rate to avoid spam.
//   3. CmdFire(origin, direction) is sent to the server.
//   4. Server re-validates fire rate (with a small grace for network jitter),
//      ammo, reload, and death, then dispatches to data.ShotBehavior.Fire()
//      (or a shared default Hitscan instance if none is assigned).
//   5. The behavior resolves damage via HitResolution (reusing the existing
//      Hitbox/HitZone/DamageInfo pipeline) and/or spawns a projectile, and
//      plays cosmetic tracers through this class's IShotBehaviorHost methods.
//
// FLOW (Continuous shot behaviors - Beam):
//   Gun raises HoldStarted/HoldStopped instead of RequestFire (see Gun.cs).
//   This class sends CmdBeamStart/CmdBeamStop once each, then paces
//   CmdBeamTick(origin, direction) at the behavior's own TickRate (never every
//   rendered frame). The server re-validates on every tick and is the only
//   place beam damage is ever applied.
//
// AUTHORITY NOTE:
//   Origin/direction are trusted from the client because the server does not
//   simulate the client's camera. Fire rate, range, and (for beams) ammo/tick
//   pacing are enforced server-side. Aim-through-walls or teleport-to-target
//   style cheats would need extra validation (line-of-sight, position sanity)
//   - out of scope for prototype.
// =============================================================================

using FishNet;
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
    public class PlayerWeaponController : NetworkBehaviour, IShotBehaviorHost
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

        [Tooltip("Distance the aim ray's origin is pushed forward along the camera's forward direction before being sent to the server. Must clear the player's own CharacterController/hitbox colliders (radius ~0.5) so shots can never self-block while moving. Applied on the trusted client side, same as origin/direction themselves.")]
        [SerializeField, Min(0f)] private float _muzzleClearanceDistance = 0.6f;

        [Header("Feedback")]
        [Tooltip("Pure-visual tracer spawned locally on every peer for each shot (hit or miss). Not networked itself — only the start/end points travel over the tracer RPCs.")]
        [SerializeField] private BulletTracer _tracerPrefab;

        // Shared, stateless fallback so a GunData with no ShotBehavior assigned
        // keeps behaving exactly like the old hardcoded hitscan path. Created
        // once and reused by every PlayerWeaponController - ShotBehavior
        // instances never hold per-shot state (see ShotBehavior.cs).
        private static HitscanShotBehavior _defaultHitscanBehavior;
        private static HitscanShotBehavior DefaultHitscanBehavior =>
            _defaultHitscanBehavior ??= ScriptableObject.CreateInstance<HitscanShotBehavior>();

        private float _serverNextAllowedFireTime;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<int>  _magazineAmmo = new SyncVar<int>();
        private readonly SyncVar<int>  _reserveAmmo  = new SyncVar<int>();
        private readonly SyncVar<bool> _isReloading  = new SyncVar<bool>();

        private Coroutine _reloadRoutine;

        // ------------------------------------------------------------------
        // Continuous (beam) state.
        //   _ownerBeamHeld   - client-local, only meaningful on the owner:
        //                      "should my Update() keep sending CmdBeamTick?"
        //   _serverBeamActive - server-only: "is a beam currently authorized?"
        // Kept separate rather than one shared flag so a value mirrored to
        // non-owner peers (via RpcBeamStopped) can never be mistaken for
        // server state on this same instance. See PlayerWeaponEquipper's
        // SetGun for why a weapon switch must also stop an active beam.
        // ------------------------------------------------------------------
        private bool  _ownerBeamHeld;
        private float _beamTickAccumulator;
        private bool  _serverBeamActive;
        private float _beamAmmoAccumulator;
        private float _serverBeamStartTime;

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
            if (_inputReader == null) return;

            _inputReader.FireStarted += HandleFireStarted;
            _inputReader.FireCanceled += HandleFireCanceled;
            _inputReader.ReloadStarted += HandleReloadStarted;

            // _gun may still be unassigned here if PlayerWeaponEquipper hasn't
            // equipped a weapon yet - SetGun() picks up these subscriptions
            // once it does.
            SubscribeToGun();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!base.IsOwner) return;
            if (_inputReader == null) return;

            _inputReader.FireStarted -= HandleFireStarted;
            _inputReader.FireCanceled -= HandleFireCanceled;
            _inputReader.ReloadStarted -= HandleReloadStarted;

            UnsubscribeFromGun();
        }

        private void Update()
        {
            // Owner-only: pace beam ticks at the behavior's TickRate rather
            // than sending a ServerRpc every rendered frame.
            if (!base.IsOwner || !_ownerBeamHeld) return;
            if (_gun == null || _gun.Data == null) return;
            if (_gun.Data.ShotBehavior is not IContinuousShotBehavior beam) return;

            _beamTickAccumulator += Time.deltaTime;
            float interval = 1f / Mathf.Max(0.01f, beam.TickRate);
            if (_beamTickAccumulator < interval) return;
            _beamTickAccumulator -= interval;

            GetAimRay(out Vector3 origin, out Vector3 direction);
            CmdBeamTick(origin, direction);
        }

        /// <summary>
        /// Swaps the Gun this controller fires against and validates ammo
        /// for. Called by PlayerWeaponEquipper whenever the equipped weapon
        /// changes (initial spawn equip, respawn, or a menu/category switch).
        /// Re-homes the RequestFire/HoldStarted/HoldStopped subscriptions on
        /// the owner, stops any beam that was active against the OLD weapon
        /// (switching weapons must not leave a beam running server-side
        /// against a weapon we no longer have equipped), and reseeds ammo on
        /// the server for the new weapon's GunData - no duplicated seeding
        /// logic, this just calls the same path Respawner already uses.
        /// </summary>
        public void SetGun(Gun gun)
        {
            if (_gun == gun) return;

            UnsubscribeFromGun();

            if (_ownerBeamHeld)
            {
                _ownerBeamHeld = false;
                if (base.IsOwner) CmdBeamStop();
            }

            _gun = gun;
            SubscribeToGun();

            ServerResetAmmo();
        }

        private void SubscribeToGun()
        {
            if (!base.IsOwner || _gun == null) return;
            _gun.RequestFire += HandleRequestFire;
            _gun.HoldStarted += HandleHoldStarted;
            _gun.HoldStopped += HandleHoldStopped;
        }

        private void UnsubscribeFromGun()
        {
            if (!base.IsOwner || _gun == null) return;
            _gun.RequestFire -= HandleRequestFire;
            _gun.HoldStarted -= HandleHoldStarted;
            _gun.HoldStopped -= HandleHoldStopped;
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
        /// no separate IsDead check needed here or in Gun's callers. Locking
        /// while a beam is held raises Gun.HoldStopped (see Gun.SetLocked),
        /// which HandleHoldStopped below turns into CmdBeamStop.
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
            if (_serverBeamActive) ServerStopBeam();
            SeedAmmoFromData();
        }

        // ------------------------------------------------------------------
        // Owner-side path — Instant shot behaviors
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
            GetAimRay(out Vector3 origin, out Vector3 direction);
            CmdFire(origin, direction);
        }

        /// <summary>
        /// Builds the trusted aim ray sent to the server, pushing the origin
        /// forward from the camera by _muzzleClearanceDistance first. The
        /// camera sits inside the player's own CharacterController/hitbox
        /// colliders (by design, at head height) - without this offset a
        /// shot's raycast origin can start inside those colliders, which is
        /// harmless on its own (Physics.Raycast never hits a Collider it
        /// starts inside) but leaves no margin against edge cases (capsule
        /// resizing on crouch, floating-point boundary overlap, etc.) that
        /// could otherwise cause a shot to clip the shooter's own body
        /// immediately after leaving it. Shared by every shot path (Instant
        /// via HandleRequestFire, Continuous/Beam via Update()) so Hitscan,
        /// Shotgun, Beam, and Projectile's aim-correction ray all get this
        /// for free through the same ShotContext.Origin.
        /// </summary>
        private void GetAimRay(out Vector3 origin, out Vector3 direction)
        {
            origin = _cameraTransform != null ? _cameraTransform.position : transform.position;
            direction = _cameraTransform != null ? _cameraTransform.forward : transform.forward;
            origin += direction * _muzzleClearanceDistance;
        }

        // ------------------------------------------------------------------
        // Owner-side path — Continuous (beam) shot behaviors
        // ------------------------------------------------------------------

        private void HandleHoldStarted()
        {
            _ownerBeamHeld = true;
            _beamTickAccumulator = 0f;
            CmdBeamStart();
        }

        private void HandleHoldStopped()
        {
            if (!_ownerBeamHeld) return;
            _ownerBeamHeld = false;
            CmdBeamStop();
        }

        // ------------------------------------------------------------------
        // Server-side path — Instant shot behaviors
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

            ShotDeliveryKind kind = data.ShotBehavior != null ? data.ShotBehavior.Kind : ShotDeliveryKind.Instant;
            if (kind != ShotDeliveryKind.Instant) return; // Continuous/Charged behaviors fire through the hold-based path instead.

            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            InstantShotBehavior behavior = data.ShotBehavior as InstantShotBehavior ?? DefaultHitscanBehavior;
            ShotContext ctx = new ShotContext(origin, direction, data, base.NetworkObject, transform.root, this);
            behavior.Fire(ctx);
        }

        [ServerRpc]
        private void CmdReload()
        {
            if (_lifecycle != null && _lifecycle.IsDead) return;
            TryServerBeginReload();
        }
        /// <summary>
        /// Starts a reload if the current state allows it. Used by both the
        /// manual CmdReload path and the auto-reload trigger inside CmdFire/
        /// ConsumeBeamAmmo - one server-side entry point, no duplicated
        /// validation. Also stops an active beam, satisfying "beam must stop
        /// during reload."
        /// </summary>
        private bool TryServerBeginReload()
        {
            if (_gun == null || _gun.Data == null) return false;
            if (_isReloading.Value) return false;
            if (_magazineAmmo.Value >= _gun.Data.MagazineSize) return false;
            if (_reserveAmmo.Value <= 0) return false;

            if (_serverBeamActive) ServerStopBeam();

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
        // Server-side path — Continuous (beam) shot behaviors
        // ------------------------------------------------------------------

        [ServerRpc]
        private void CmdBeamStart()
        {
            if (_gun == null || _gun.Data == null) return;
            if (_gun.Data.ShotBehavior is not IContinuousShotBehavior) return;
            if (_lifecycle != null && _lifecycle.IsDead) return;
            if (_isReloading.Value || _magazineAmmo.Value <= 0) return;

            _serverBeamActive = true;
            _beamAmmoAccumulator = 0f;
            _serverBeamStartTime = Time.time;
            RpcBeamStarted();
        }

        [ServerRpc]
        private void CmdBeamTick(Vector3 origin, Vector3 direction)
        {
            if (!_serverBeamActive) return;
            if (_gun == null || _gun.Data == null || _gun.Data.ShotBehavior is not IContinuousShotBehavior beam)
            {
                ServerStopBeam();
                return;
            }
            if (_lifecycle != null && _lifecycle.IsDead) { ServerStopBeam(); return; }
            if (_isReloading.Value) { ServerStopBeam(); return; }
            if (_magazineAmmo.Value <= 0) { ServerStopBeam(); return; }
            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            float heldDuration = Time.time - _serverBeamStartTime;
            ShotContext ctx = new ShotContext(origin, direction, _gun.Data, base.NetworkObject, transform.root, this, heldDuration);
            BeamTickResult result = beam.Tick(ctx);

            RpcBeamVisualUpdate(origin, result.EndPoint, result.DidHit);
            ConsumeBeamAmmo(beam.AmmoPerTick);
        }

        [ServerRpc]
        private void CmdBeamStop()
        {
            ServerStopBeam();
        }

        /// <summary>
        /// Server-only fractional ammo accumulator so AmmoPerTick values below
        /// 1 (e.g. "one round every two ticks") still decrement whole
        /// magazine rounds. Stops the beam (and auto-reloads, same as CmdFire)
        /// once ammo reaches zero.
        /// </summary>
        private void ConsumeBeamAmmo(float amountPerTick)
        {
            _beamAmmoAccumulator += amountPerTick;
            int wholeRounds = Mathf.FloorToInt(_beamAmmoAccumulator);
            if (wholeRounds <= 0) return;

            _beamAmmoAccumulator -= wholeRounds;
            _magazineAmmo.Value = Mathf.Max(0, _magazineAmmo.Value - wholeRounds);

            if (_magazineAmmo.Value <= 0)
            {
                ServerStopBeam();
                if (_gun.Data.AutoReloadOnEmpty && _reserveAmmo.Value > 0)
                    TryServerBeginReload();
            }
        }

        private void ServerStopBeam()
        {
            if (!_serverBeamActive) return;
            _serverBeamActive = false;
            RpcBeamStopped();
        }

        [ObserversRpc]
        private void RpcBeamStarted()
        {
            if (_gun != null && _gun.Data != null)
                ShotEvents.RaiseBeamStarted(base.NetworkObject, _gun.Data);
        }

        [ObserversRpc]
        private void RpcBeamVisualUpdate(Vector3 origin, Vector3 endPoint, bool didHit)
        {
            GunData weapon = _gun != null ? _gun.Data : null;
            ShotEvents.RaiseBeamUpdated(base.NetworkObject, weapon, origin, endPoint, didHit);
            if (didHit)
                ShotEvents.RaiseBeamHit(base.NetworkObject, weapon, endPoint);
        }

        [ObserversRpc]
        private void RpcBeamStopped()
        {
            // Mirrors server intent to every peer, including the owner - this
            // is what stops the owner's Update() loop from sending further
            // CmdBeamTick calls once the server ends the beam for any reason
            // (ammo empty, reload, death, weapon switch).
            _ownerBeamHeld = false;
            if (_gun != null && _gun.Data != null)
                ShotEvents.RaiseBeamStopped(base.NetworkObject, _gun.Data);
        }

        // ------------------------------------------------------------------
        // IShotBehaviorHost — the seam ShotBehavior assets use to reach
        // networking. See IShotBehaviorHost.cs.
        // ------------------------------------------------------------------

        Vector3 IShotBehaviorHost.MuzzlePosition =>
            _gun != null && _gun.FirePoint != null ? _gun.FirePoint.position : transform.position;

        void IShotBehaviorHost.PlayTracer(Vector3 start, Vector3 end) => RpcPlayTracer(start, end);

        void IShotBehaviorHost.PlayTracers(Vector3 start, Vector3[] ends) => RpcPlayTracers(start, ends);

        NetworkObject IShotBehaviorHost.SpawnProjectile(NetworkObject prefab, Vector3 position, Quaternion rotation)
        {
            if (!IsServerInitialized || prefab == null) return null;

            NetworkObject instance = Instantiate(prefab, position, rotation);
            InstanceFinder.ServerManager.Spawn(instance, base.Owner);
            return instance;
        }

        // ------------------------------------------------------------------
        // Tracer feedback (pure UX — never mutates game state)
        // ------------------------------------------------------------------

        [ObserversRpc]
        private void RpcPlayTracer(Vector3 start, Vector3 end)
        {
            if (_tracerPrefab != null)
            {
                BulletTracer tracer = Instantiate(_tracerPrefab, start, Quaternion.identity);
                tracer.Play(start, end);
            }
            ShotEvents.RaiseShotFired(base.NetworkObject, _gun != null ? _gun.Data : null, start, end);
        }

        [ObserversRpc]
        private void RpcPlayTracers(Vector3 start, Vector3[] ends)
        {
            GunData weapon = _gun != null ? _gun.Data : null;
            if (ends == null) return;

            foreach (Vector3 end in ends)
            {
                if (_tracerPrefab != null)
                {
                    BulletTracer tracer = Instantiate(_tracerPrefab, start, Quaternion.identity);
                    tracer.Play(start, end);
                }
                ShotEvents.RaisePelletFired(base.NetworkObject, weapon, start, end);
            }
        }
    }
}
