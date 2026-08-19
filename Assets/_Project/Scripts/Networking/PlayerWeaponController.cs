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
//   4. Server re-validates fire rate (as a leaky bucket, so tick quantization
//      and network jitter cannot eat rounds out of a burst - see CmdFire),
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
using System.Collections.Generic;
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

        [Tooltip("Optional. Leave null to auto-resolve. Used to gate input by state (only fire during Gameplay state).")]
        [SerializeField] private PlayerInputStateController _stateController;

        [Header("Server validation")]
        [Tooltip("How many shots' worth of fire-rate credit a client may bank while not firing. This is the jitter tolerance for the server's rate check. FishNet flushes and reads RPCs on tick boundaries (33ms at the default 30 tick rate), so an honest client's shots routinely reach the server closer together than 1/FireRate - a burst weapon cannot land its full burst without this. 3 covers a standard 3-round burst. Sustained fire rate is capped at FireRate no matter what this is set to.")]
        [SerializeField, Range(1f, 8f)] private float _serverFireRateBurstAllowance = 3f;

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

        // Leaky-bucket accumulator behind the server-side fire-rate check. A
        // single field shared across every equipped weapon, hence the reset in
        // ServerSwapAmmo.
        //
        // Semantics: "the earliest Time.time at which the next shot may fire."
        // Every shot that actually happens pushes it forward by exactly one
        // fire interval, so sustained rate is still hard-capped at
        // GunData.FireRate. While not firing it is pulled back toward `now` by
        // up to _serverFireRateBurstAllowance intervals - that banked credit is
        // what absorbs tick quantization and network jitter.
        private float _serverNextAllowedFireTime;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<int>  _magazineAmmo = new SyncVar<int>();
        private readonly SyncVar<int>  _reserveAmmo  = new SyncVar<int>();
        private readonly SyncVar<bool> _isReloading  = new SyncVar<bool>();

        // Server-only: magazine/reserve remembered per equipped Gun instance so
        // category switches restore the weapon you left, instead of reseeding
        // from GunData. Cleared on respawn (ServerResetAmmo). Entries for a
        // destroyed Gun are dropped via ForgetSavedAmmo when the equipper
        // replaces a loadout slot.
        private readonly Dictionary<Gun, AmmoSnapshot> _ammoByGun = new();

        private Coroutine _reloadRoutine;

        private struct AmmoSnapshot
        {
            public int Magazine;
            public int Reserve;
        }

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

        // ------------------------------------------------------------------
        // Solar Ascension fireball override - set by SolarAscensionEffect
        // (same GameObject) while that ultimate is active. Bypasses the
        // equipped Gun entirely: never touches _gun/ammo/the equipped
        // weapon's shared ShotBehavior asset, so nothing leaks into other
        // players using the same gun. _ownerAscensionFireActive gates
        // HandleFireStarted (owner-local); _serverAscensionFireActive gates
        // CmdFireAscension (server-only) - the same owner/server split every
        // other gate in this class already uses.
        // ------------------------------------------------------------------
        private bool _ownerAscensionFireActive;
        private bool _serverAscensionFireActive;
        private GunData _ascensionFireData;
        private ProjectileShotBehavior _ascensionFireBehavior;
        private float _ascensionNextAllowedFireTime;

        public int  MagazineAmmo => _magazineAmmo.Value;
        public int  ReserveAmmo  => _reserveAmmo.Value;
        public bool IsReloading  => _isReloading.Value;
        /// <summary>Raised on every peer whenever any ammo SyncVar changes, including the initial seed. HUD subscribes here.</summary>
        public event Action<int, int, bool> OnAmmoChanged;

        /// <summary>
        /// The Gun currently equipped through this controller, or null if none.
        /// Guns are instantiated/destroyed at runtime by PlayerWeaponEquipper
        /// (see SetGun), so there's no single fixed weapon Transform a prefab
        /// field could point to ahead of time - purely cosmetic systems that
        /// need to track wherever the current weapon visually points/sits
        /// (e.g. BeamRenderer's muzzle origin) should read this every frame
        /// instead of caching a Transform reference.
        /// </summary>
        public Gun CurrentGun => _gun;

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

            if (_stateController != null)
                _stateController.OnStateChanged += HandleInputStateChanged;

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

            // Runs synchronously as part of FishNet's despawn broadcast - contain
            // any exception here rather than letting it escape into the network
            // transport.
            try
            {
                _inputReader.FireStarted -= HandleFireStarted;
                _inputReader.FireCanceled -= HandleFireCanceled;
                _inputReader.ReloadStarted -= HandleReloadStarted;

                if (_stateController != null)
                    _stateController.OnStateChanged -= HandleInputStateChanged;

                UnsubscribeFromGun();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
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
        /// changes (initial spawn equip, loadout change, or a category
        /// switch). Re-homes the RequestFire/HoldStarted/HoldStopped
        /// subscriptions on the owner, stops any beam that was active against
        /// the OLD weapon, and on the server saves/restores per-Gun ammo so
        /// switching away and back keeps magazine and reserves. Full refill
        /// from GunData is only done by ServerResetAmmo (respawn) or the
        /// first time a given Gun instance is drawn.
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

            Gun previous = _gun;
            _gun = gun;
            SubscribeToGun();

            if (IsServerInitialized)
                ServerSwapAmmo(previous, gun);
        }

        /// <summary>
        /// Server-only. Drops any saved ammo for a Gun that is about to be
        /// destroyed (loadout slot replaced). Without this, a destroyed
        /// instance would leak a dictionary entry and a later Instantiate of
        /// the same prefab would be a different key anyway.
        /// </summary>
        public void ForgetSavedAmmo(Gun gun)
        {
            if (gun == null) return;
            _ammoByGun.Remove(gun);
        }

        /// <summary>
        /// Cancels an in-progress reload, stops an active beam, then either
        /// restores the next Gun's saved magazine/reserve or seeds from its
        /// GunData the first time it is drawn. The previous Gun's current
        /// SyncVar ammo is written into _ammoByGun first.
        /// </summary>
        private void ServerSwapAmmo(Gun previous, Gun next)
        {
            CancelReloadAndBeam();

            // The fire-rate cooldown is a single field shared across every
            // equipped weapon (see _serverNextAllowedFireTime's declaration).
            // Without this reset, switching from a slow weapon to a fast one
            // leaves the new weapon silently gated by the old weapon's
            // cooldown window until it naturally expires - CmdFire just
            // returns early with no feedback. A freshly-equipped weapon
            // should start exactly like a freshly-spawned player does (0f).
            _serverNextAllowedFireTime = 0f;

            if (previous != null)
            {
                _ammoByGun[previous] = new AmmoSnapshot
                {
                    Magazine = _magazineAmmo.Value,
                    Reserve  = _reserveAmmo.Value
                };
            }

            if (next == null)
            {
                _magazineAmmo.Value = 0;
                _reserveAmmo.Value  = 0;
                _isReloading.Value  = false;
                return;
            }

            if (_ammoByGun.TryGetValue(next, out AmmoSnapshot saved))
            {
                _magazineAmmo.Value = saved.Magazine;
                _reserveAmmo.Value  = saved.Reserve;
                _isReloading.Value  = false;
            }
            else
            {
                SeedAmmoFromData(next);
            }
        }

        private void CancelReloadAndBeam()
        {
            if (_reloadRoutine != null)
            {
                StopCoroutine(_reloadRoutine);
                _reloadRoutine = null;
            }
            if (_serverBeamActive) ServerStopBeam();
            _isReloading.Value = false;
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
            if (_stateController == null)
                _stateController = GetComponentInParent<PlayerInputStateController>();

            _magazineAmmo.OnChange += HandleAmmoIntChanged;
            _reserveAmmo.OnChange  += HandleAmmoIntChanged;
            _isReloading.OnChange  += HandleReloadingChanged;
        }
        private void OnDestroy()
        {
            try
            {
                if (_stateController != null)
                    _stateController.OnStateChanged -= HandleInputStateChanged;

                _magazineAmmo.OnChange -= HandleAmmoIntChanged;
                _reserveAmmo.OnChange  -= HandleAmmoIntChanged;
                _isReloading.OnChange  -= HandleReloadingChanged;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            SeedAmmoFromData();
        }

        private void SeedAmmoFromData(Gun gun = null)
        {
            gun ??= _gun;
            if (gun == null || gun.Data == null) return;
            _magazineAmmo.Value = gun.Data.MagazineSize;
            _reserveAmmo.Value  = gun.Data.StartingReserveAmmo;
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

        /// <summary>
        /// Server-only. Cancels any in-progress reload, clears every per-Gun
        /// ammo snapshot, and refills the currently drawn weapon to its
        /// GunData starting values. Called by Respawner on respawn so a later
        /// weapon switch cannot restore pre-death magazine/reserve counts.
        /// </summary>
        public void ServerResetAmmo()
        {
            if (!IsServerInitialized) return;
            CancelReloadAndBeam();
            _ammoByGun.Clear();
            SeedAmmoFromData();
        }

        // ------------------------------------------------------------------
        // Owner-side path — Instant shot behaviors
        // ------------------------------------------------------------------

        private void HandleFireStarted()
        {
            // Gate input by state: only allow firing during Gameplay
            if (_stateController != null && _stateController.CurrentState != PlayerInputState.Gameplay)
                return;

            if (_ownerAscensionFireActive)
            {
                GetAimRay(out Vector3 origin, out Vector3 direction);
                CmdFireAscension(origin, direction);
                return;
            }

            if (_gun == null || _gun.Data == null) return;
            _gun.StartFire();
        }

        /// <summary>
        /// Server-only. Called directly by SolarAscensionEffect (same
        /// GameObject) to enable/disable the ascension fireball bypass in
        /// CmdFire's place. Passing active=false clears the cached data/
        /// behavior too, so a stale reference can never be fired after the
        /// ultimate ends.
        /// </summary>
        public void ServerSetAscensionFireOverride(GunData data, ProjectileShotBehavior behavior, bool active)
        {
            if (!IsServerInitialized) return;
            _serverAscensionFireActive = active;
            _ascensionFireData = active ? data : null;
            _ascensionFireBehavior = active ? behavior : null;
            _ascensionNextAllowedFireTime = 0f;
        }

        /// <summary>
        /// Owner-local. Called by SolarAscensionEffect's TargetRpc to switch
        /// HandleFireStarted from the equipped gun to the ascension fireball
        /// bypass.
        /// </summary>
        public void SetOwnerAscensionFireActive(bool active) => _ownerAscensionFireActive = active;

        [ServerRpc]
        private void CmdFireAscension(Vector3 origin, Vector3 direction)
        {
            if (!_serverAscensionFireActive || _ascensionFireData == null || _ascensionFireBehavior == null) return;
            if (_lifecycle != null && _lifecycle.IsDead) return;
            if (direction.sqrMagnitude < 0.0001f) return;

            float now = Time.time;
            float interval = 1f / Mathf.Max(0.01f, _ascensionFireData.FireRate);
            if (now < _ascensionNextAllowedFireTime) return;
            _ascensionNextAllowedFireTime = now + interval;

            direction.Normalize();

            ShotContext ctx = new ShotContext(origin, direction, _ascensionFireData, base.NetworkObject, transform.root, this);
            _ascensionFireBehavior.Fire(ctx);
        }

        private void HandleFireCanceled()
        {
            if (_gun == null) return;
            _gun.StopFire();
        }
        private void HandleReloadStarted()
        {
            // Gate input by state: only allow reload during Gameplay
            if (_stateController != null && _stateController.CurrentState != PlayerInputState.Gameplay)
                return;

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
            if (_gun == null || _gun.Data == null)
            {
                LogFireRejected("gun/data null");
                return;
            }

            // Defense in depth: the owner-side Gun lock should already stop this
            // RPC from ever being sent while dead, but the server never trusts
            // the client - re-check authoritative life state here too.
            if (_lifecycle != null && _lifecycle.IsDead)
            {
                LogFireRejected("dead");
                return;
            }

            GunData data = _gun.Data;

            // Rate validation, as a leaky bucket rather than a hard "no shot
            // before time T". A flat cutoff cannot work here: FishNet flushes
            // and reads RPCs on tick boundaries (33ms at the default 30 tick
            // rate), so an honest client firing at 12/s - shots 83ms apart -
            // has them land at the server either 67ms or 100ms apart depending
            // on tick phase. The old 5% grace only tolerated 4ms of that, so
            // the 67ms case was rejected and a 3-round burst reliably lost a
            // round. Banking credit while not firing absorbs the jitter while
            // still capping sustained rate at exactly FireRate.
            float now = Time.time;
            float interval = 1f / Mathf.Max(0.01f, data.FireRate);

            _serverNextAllowedFireTime = Mathf.Max(
                _serverNextAllowedFireTime,
                now - interval * _serverFireRateBurstAllowance);

            if (_serverNextAllowedFireTime > now)
            {
                LogFireRejected($"fire-rate cooldown ({_serverNextAllowedFireTime - now:F2}s remaining)");
                return;
            }

            if (_isReloading.Value)
            {
                LogFireRejected("reloading");
                return;
            }
            if (_magazineAmmo.Value <= 0)
            {
                LogFireRejected("empty magazine");
                return;
            }

            // Only a shot that actually happens spends rate budget. Charging it
            // above (as this used to) meant a request rejected for ammo or
            // reload still pushed the window out, gating the first real shot
            // after a reload.
            _serverNextAllowedFireTime += interval;

            _magazineAmmo.Value--;

            if (_magazineAmmo.Value <= 0 && data.AutoReloadOnEmpty && _reserveAmmo.Value > 0)
            {
                TryServerBeginReload();
            }

            ShotDeliveryKind kind = data.ShotBehavior != null ? data.ShotBehavior.Kind : ShotDeliveryKind.Instant;
            if (kind != ShotDeliveryKind.Instant) return; // Continuous/Charged behaviors fire through the hold-based path instead.

            if (direction.sqrMagnitude < 0.0001f)
            {
                LogFireRejected("zero-length direction");
                return;
            }
            direction.Normalize();

            InstantShotBehavior behavior = data.ShotBehavior as InstantShotBehavior ?? DefaultHitscanBehavior;
            ShotContext ctx = new ShotContext(origin, direction, data, base.NetworkObject, transform.root, this);
            behavior.Fire(ctx);
            if (_gun.ParticleSystem != null)
            {
                Instantiate(_gun.ParticleSystem,_gun.FirePoint);
            }
        }

        /// <summary>
        /// Temporary diagnostic for the "shot silently dropped" bug - CmdFire
        /// returns early on every rejection with no client-visible feedback,
        /// so there was previously no way to tell after the fact which gate
        /// fired. Tagged with the owning connection's ClientId and the
        /// equipped gun so a log captured during a repro pins down the exact
        /// cause instead of guessing. Safe to remove once the root cause behind
        /// the "totally can't fire" reports is confirmed and fixed.
        /// </summary>
        private void LogFireRejected(string reason)
        {
            int clientId = base.Owner != null ? base.Owner.ClientId : -1;
            string gunName = _gun != null ? _gun.name : "<none>";
            Debug.Log($"[{nameof(PlayerWeaponController)}] CmdFire rejected for client {clientId} ({gunName}): {reason}");
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

            // Hit detection stays tied to the trusted camera-based origin/
            // direction (see AUTHORITY NOTE above), but the visual beam
            // should appear to come from the gun, not the player's eyes -
            // same split HitscanShotBehavior already uses for tracers
            // (ctx.Origin for the raycast, ctx.Host.MuzzlePosition for the
            // drawn line's start point).
            Vector3 muzzlePosition = ((IShotBehaviorHost)this).MuzzlePosition;
            RpcBeamVisualUpdate(muzzlePosition, result.EndPoint, result.DidHit);
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
            // Always raise - remote observers have no CurrentGun (_gun is
            // owner/server-only via SetGun), same pattern as RpcBeamVisualUpdate.
            ShotEvents.RaiseBeamStarted(base.NetworkObject, _gun != null ? _gun.Data : null);
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
            // (ammo empty, reload, death, weapon switch). Always raise the
            // cosmetic stop event even when _gun is null so remote BeamRenderer
            // copies clear their line (they never receive SetGun).
            _ownerBeamHeld = false;
            ShotEvents.RaiseBeamStopped(base.NetworkObject, _gun != null ? _gun.Data : null);
        }

        // ------------------------------------------------------------------
        // Input state integration
        // ------------------------------------------------------------------

        /// <summary>
        /// React to PlayerInputStateController state changes. Stop any ongoing
        /// fire (automatic or beam) when entering Menu or Dead state to prevent
        /// the player from continuing to shoot while the menu is open or after death.
        /// </summary>
        private void HandleInputStateChanged(PlayerInputState oldState, PlayerInputState newState)
        {
            // Only the owner handles input state changes (remote players don't fire locally)
            if (!base.IsOwner) return;

            // Stop ongoing fire when leaving Gameplay state
            if (oldState == PlayerInputState.Gameplay && newState != PlayerInputState.Gameplay)
            {
                // Stop automatic fire or held fire button
                if (_gun != null)
                    _gun.StopFire();

                // Stop beam fire if active
                if (_ownerBeamHeld)
                {
                    _ownerBeamHeld = false;
                    CmdBeamStop();
                }
            }
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
