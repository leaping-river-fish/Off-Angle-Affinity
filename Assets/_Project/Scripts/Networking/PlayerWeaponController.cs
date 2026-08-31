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
using FishNet.Connection;
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

        [Tooltip("Maximum age of the owner's FireTick versus TimeManager.Tick. Older or future ticks are rejected. This is not rewind; it is the window lag compensation will sit behind.")]
        [SerializeField, Min(0f)] private float _maxFireTickAgeSeconds = 0.25f;

        [Tooltip("Distance the aim ray's origin is pushed forward along the camera's forward direction before being sent to the server. Must clear the player's own CharacterController/hitbox colliders (radius ~0.5) so shots can never self-block while moving. Applied on the trusted client side, same as origin/direction themselves.")]
        [SerializeField, Min(0f)] private float _muzzleClearanceDistance = 0.6f;

        [Header("Feedback")]
        [Tooltip("Pure-visual tracer spawned locally on every peer for each shot (hit or miss). Not networked itself — only the start/end points travel over the tracer RPCs.")]
        [SerializeField] private BulletTracer _tracerPrefab;

        [Header("Owner projectile handoff")]
        [Tooltip("If predicted and authoritative are within this distance, skip blending and show the server projectile immediately.")]
        [SerializeField, Min(0f)] private float _ownerHandoffSnapDistance = 1f;

        [Tooltip("If visual error is larger than this, skip blending (likely clipped geometry). Hard-cut to the server projectile.")]
        [SerializeField, Min(0f)] private float _ownerHandoffMaxBlendDistance = 12f;

        [Tooltip("Seconds to lerp the predicted visual toward the authoritative transform.")]
        [SerializeField, Min(0.01f)] private float _ownerHandoffBlendDuration = 0.1f;

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

        // ------------------------------------------------------------------
        // Client-side prediction - shot ID tracking for reconciliation.
        // Owner-only: tracks predicted shots (muzzle flash, tracers, 
        // projectiles) so the authoritative server response can be 
        // associated and duplicates avoided.
        // ------------------------------------------------------------------
        private ushort _ownerNextShotId = 1; // Wraps at ushort.MaxValue, 0 = invalid
        private readonly Dictionary<ushort, PredictedShot> _predictedShots = new();

        private struct PredictedShot
        {
            public GameObject PredictedProjectile; // Visual-only GameObject for predicted projectile (not networked)
            public float PredictedAt; // Time.time when prediction was made, for timeout cleanup
        }
        
        private struct OwnerProjectileHandoff
        {
            public GameObject Predicted;
            public NetworkObject Authoritative;
            public Vector3 StartPosition;
            public Quaternion StartRotation;
            public float StartTime;
            public float Duration;
        }

        private readonly List<OwnerProjectileHandoff> _ownerHandoffs = new();

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
            if (!base.IsOwner) return;

            // Cleanup stale predicted shots periodically
            CleanupStalePredictions();

            // Owner-only: pace beam ticks at the behavior's TickRate rather
            // than sending a ServerRpc every rendered frame.
            OwnerUpdateProjectileHandoffs();
            if (!_ownerBeamHeld) return;
            if (_gun == null || _gun.Data == null) return;
            if (_gun.Data.ShotBehavior is not IContinuousShotBehavior beam) return;

            _beamTickAccumulator += Time.deltaTime;
            float interval = 1f / Mathf.Max(0.01f, beam.TickRate);
            if (_beamTickAccumulator < interval) return;
            _beamTickAccumulator -= interval;

            GetAimRay(out Vector3 origin, out Vector3 direction);
            CmdBeamTick(origin, direction);
        }

        private void OwnerUpdateProjectileHandoffs()
        {
            for (int i = _ownerHandoffs.Count - 1; i >= 0; i--) 
            {
                OwnerProjectileHandoff handoff = _ownerHandoffs[i];

                if (handoff.Predicted == null || handoff.Authoritative == null)
                {
                    OwnerFinishProjectileHandoff(handoff);
                    _ownerHandoffs.RemoveAt(i);
                    continue;
                }

                float t = Mathf.Clamp01((Time.time - handoff.StartTime) / Mathf.Max(0.01f, handoff.Duration));
                Transform auth = handoff.Authoritative.transform;
                handoff.Predicted.transform.SetPositionAndRotation(
                    Vector3.Lerp(handoff.StartPosition, auth.position, t),
                    Quaternion.Slerp(handoff.StartRotation, auth.rotation, t));

                if (t >= 1f)
                {
                    OwnerFinishProjectileHandoff(handoff);
                    _ownerHandoffs.RemoveAt(i);
                }    
            }
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
            
            // Generate unique shot ID for prediction/reconciliation
            ushort shotId = _ownerNextShotId++;
            if (_ownerNextShotId == 0) _ownerNextShotId = 1; // Skip 0 (invalid)
            
            // Predict fire effects immediately on owner
            OwnerPredictFire(origin, direction, shotId);
            
            // Send to server for authoritative validation
            CmdFire(origin, direction, shotId, (uint)TimeManager.Tick);
        }

        /// <summary>
        /// Owner-only. Predicts firing effects immediately without waiting for
        /// server round-trip. Plays muzzle flash, tracers, and spawns predicted
        /// projectiles. The server's authoritative response will later be
        /// reconciled via shot ID to avoid duplicate effects.
        /// </summary>
        private void OwnerPredictFire(Vector3 origin, Vector3 direction, ushort shotId)
        {
            if (!base.IsOwner) return;
            if (_gun == null || _gun.Data == null) return;

            // Muzzle flash - already handled by Gun.AttemptFire() → PlayFireAnimation()
            // which was called before this via Gun.RequestFire event chain
            
            // Predict hitscan tracer immediately
            if (_gun.Data.ShotBehavior is HitscanShotBehavior or null) // null = default hitscan
            {
                bool didHit = HitResolution.TryRaycastIgnoreSelf(origin, direction, _gun.Data.Range, _gun.Data.HitMask, transform.root, out RaycastHit hit);
                Vector3 tracerEnd = didHit ? hit.point : origin + direction * _gun.Data.Range;
                
                if (_tracerPrefab != null)
                {
                    BulletTracer tracer = Instantiate(_tracerPrefab, 
                        ((IShotBehaviorHost)this).MuzzlePosition, Quaternion.identity);
                    tracer.Play(((IShotBehaviorHost)this).MuzzlePosition, tracerEnd);
                }
                
                // Store prediction for cleanup
                _predictedShots[shotId] = new PredictedShot 
                { 
                    PredictedProjectile = null, 
                    PredictedAt = Time.time 
                };
            }
            // Predict projectile weapons immediately (visual-only, non-networked)
            else if (_gun.Data.ShotBehavior is ProjectileShotBehavior projBehavior && 
                projBehavior.ProjectilePrefab != null)
            {
                Vector3 muzzle = ((IShotBehaviorHost)this).MuzzlePosition;
                Vector3 correctedDir = ComputeAimCorrectedDirection(origin, direction, muzzle);
                
                // Instantiate as regular GameObject (not networked) for immediate visual feedback
                GameObject predicted = Instantiate(projBehavior.ProjectilePrefab.gameObject, 
                    muzzle, 
                    Quaternion.LookRotation(correctedDir, Vector3.up));
                
                if (predicted != null && predicted.TryGetComponent<Projectile>(out var proj))
                {
                    // Initialize predicted projectile with same parameters server will use
                    proj.ClientInitializePredicted(base.NetworkObject, transform.root, 
                        _gun.Data, projBehavior, correctedDir * projBehavior.Speed);
                    proj.OwnerSetPredictedShotId(shotId);
                    
                    _predictedShots[shotId] = new PredictedShot 
                    { 
                        PredictedProjectile = predicted,
                        PredictedAt = Time.time 
                    };
                }
            }
        }

        /// <summary>
        /// Computes aim-corrected direction for projectiles, matching the logic
        /// in ProjectileShotBehavior.ComputeAimCorrectedDirection. Ensures the
        /// predicted projectile aims at what the player's crosshair is actually
        /// pointing at, not just the muzzle's forward direction.
        /// </summary>
        private Vector3 ComputeAimCorrectedDirection(Vector3 origin, Vector3 direction, Vector3 muzzle)
        {
            float referenceDistance = _gun.Data.Range > 0f ? _gun.Data.Range : 500f;
            
            Vector3 aimPoint = HitResolution.TryRaycastIgnoreSelf(origin, direction, referenceDistance, _gun.Data.HitMask, transform.root, out RaycastHit hit)
                ? hit.point
                : origin + direction * referenceDistance;
            
            Vector3 toAimPoint = aimPoint - muzzle;
            return toAimPoint.sqrMagnitude > 0.0001f ? toAimPoint.normalized : direction;
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

        /// <summary>
        /// Owner-only. Public wrapper around GetAimRay for callers outside
        /// this class that need the same trusted, muzzle-clearance-adjusted
        /// aim ray weapon fire uses - e.g. PlayerUltimate, so an
        /// aim-dependent ultimate (Hadal Zone) can send it alongside
        /// CmdActivate the same way every shot already does.
        /// </summary>
        public void GetTrustedAimRay(out Vector3 origin, out Vector3 direction) => GetAimRay(out origin, out direction);

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
        private void CmdFire(Vector3 origin, Vector3 direction, ushort shotId, uint clientTick)
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

            // Timing validation - foundation for future lag compensation
            uint serverTick = (uint)TimeManager.Tick;
            if (clientTick > serverTick)
            {
                ServerRejectShot(shotId, "FireTick in the future");
                return;
            }

            double fireTickAge = TimeManager.TimePassed(clientTick, allowNegative: false);
            if (fireTickAge > _maxFireTickAgeSeconds)
            {
                ServerRejectShot(shotId, "FireTick too old");
                return;
            }
            
            // For now, just trust origin/direction as-is (existing behavior)
            // Future: add sanity checks (tickDelta < reasonable threshold, 
            // origin near player position, etc.)

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
            if (kind != ShotDeliveryKind.Instant)
            {
                ServerRejectShot(shotId, "Not instant");
                return;
            } // Continuous/Charged behaviors fire through the hold-based path instead.

            if (direction.sqrMagnitude < 0.0001f)
            {
                ServerRejectShot(shotId, "Zero-length direction");
                return;
            }
            direction.Normalize();

            InstantShotBehavior behavior = data.ShotBehavior as InstantShotBehavior ?? DefaultHitscanBehavior;
            ShotContext ctx = new ShotContext(origin, direction, data, base.NetworkObject, transform.root, this, 0f, shotId, clientTick);
            // One rewind for the whole Fire(): hitscan ray or all shotgun pellets.
            bool rewindHitboxes = behavior is HitscanShotBehavior or ShotgunShotBehavior;
            if (rewindHitboxes)
                HitboxHistoryRegistry.Rewind(clientTick);
            try
            {
                behavior.Fire(ctx);
            }
            finally
            {
                if (rewindHitboxes)
                    HitboxHistoryRegistry.Restore();
            }

            if (behavior is not ProjectileShotBehavior)
            {
                ServerAcceptPredictedInstant(shotId);
            }
            if (_gun.ParticleSystem != null)
            {
                Instantiate(_gun.ParticleSystem, _gun.FirePoint);
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

        private void ServerRejectShot(ushort shotId, string reason)
        {
            LogFireRejected(reason);
            if(base.Owner == null) return;
            TargetRpcShotRejected(base.Owner, shotId);
        }

        private void ServerAcceptPredictedInstant(ushort shotId)
        {
            if(base.Owner == null) return;
            TargetRpcShotAccepted(base.Owner, shotId);
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

        NetworkObject IShotBehaviorHost.SpawnPredictedProjectile(NetworkObject prefab, Vector3 position, Quaternion rotation, ushort shotId)
        {
            // No longer used - prediction now uses visual-only GameObjects instead of networked spawning
            // Kept for interface compatibility, but returns null
            return null;
        }

        // ------------------------------------------------------------------
        // Prediction reconciliation and cleanup
        // ------------------------------------------------------------------

        /// <summary>
        /// Called by Projectile.OnStartClient when the authoritative server
        /// projectile spawns. Destroys the visual-only predicted version to avoid duplicates.
        /// </summary>
        public void ReconcilePredictedProjectile(ushort shotId, NetworkObject authoritative)
        {
            if (!base.IsOwner) return;

            if(!_predictedShots.TryGetValue(shotId, out PredictedShot predicted)) return;

            _predictedShots.Remove(shotId);

            GameObject predictedGo = predicted.PredictedProjectile;
            if (predictedGo == null) return;

            if (authoritative == null)
            {
                Destroy(predictedGo);
                return;
            }

            OwnerSetProjectileRenderers(authoritative.gameObject, false);

            float error = Vector3.Distance(predictedGo.transform.position, authoritative.transform.position);
            bool snap = error <= _ownerHandoffSnapDistance || error > _ownerHandoffMaxBlendDistance;


            if (predictedGo.TryGetComponent<Rigidbody>(out Rigidbody predictedBody))
            {
                predictedBody.linearVelocity = Vector3.zero;
                predictedBody.isKinematic = true;
            }

            if (snap)
            {
                OwnerFinishProjectileHandoff(new OwnerProjectileHandoff{
                    Predicted = predictedGo,
                    Authoritative = authoritative,
                });
                return;
            }
        
            _ownerHandoffs.Add(new OwnerProjectileHandoff
            {
                Predicted = predictedGo,
                Authoritative = authoritative,
                StartPosition = predictedGo.transform.position,
                StartRotation = predictedGo.transform.rotation,
                StartTime = Time.time,
                Duration = _ownerHandoffBlendDuration
            });
        }

        private void OwnerFinishProjectileHandoff(OwnerProjectileHandoff handoff)
        {
            if (handoff.Predicted != null)
            {
                Destroy(handoff.Predicted);
            }

            if (handoff.Authoritative != null)
            {
                OwnerSetProjectileRenderers(handoff.Authoritative.gameObject, true);
            }
        }

        private void OwnerResolvePredictedShot(ushort shotId)
        {
            if (_predictedShots.TryGetValue(shotId, out PredictedShot predicted))
            {
                if (predicted.PredictedProjectile != null)
                {
                    Destroy(predicted.PredictedProjectile);
                }
                _predictedShots.Remove(shotId);
            }
        }

        private static void OwnerSetProjectileRenderers(GameObject root, bool enabled)
        {
            if (root == null) return;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            for(int i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = enabled;
            }
        }

        [TargetRpc]
        private void TargetRpcShotRejected(NetworkConnection conn, ushort shotId)
        {
            OwnerResolvePredictedShot(shotId);
        }

        [TargetRpc]
        private void TargetRpcShotAccepted(NetworkConnection conn, ushort shotId)
        {
            OwnerResolvePredictedShot(shotId);
        }

        /// <summary>
        /// Cleans up stale predicted shots that never reconciled (e.g., server
        /// rejected the shot due to ammo/death, or packet loss prevented the
        /// authoritative projectile from arriving). Called periodically from Update().
        /// </summary>
        private void CleanupStalePredictions()
        {
            if (!base.IsOwner) return;
            if (_predictedShots.Count == 0) return;
            
            float now = Time.time;
            List<ushort> stale = new List<ushort>();
            
            foreach (var kvp in _predictedShots)
            {
                if (now - kvp.Value.PredictedAt > 2f) // Timeout after 2 seconds
                {
                    if (kvp.Value.PredictedProjectile != null)
                    {
                        // Destroy the visual-only predicted projectile (regular GameObject)
                        Destroy(kvp.Value.PredictedProjectile);
                    }
                    stale.Add(kvp.Key);
                }
            }
            
            foreach (ushort id in stale)
                _predictedShots.Remove(id);
        }

        // ------------------------------------------------------------------
        // Tracer feedback (pure UX — never mutates game state)
        // Owner already predicted tracers in OwnerPredictFire(), so exclude
        // owner from these RPCs to prevent duplicates.
        // ------------------------------------------------------------------

        [ObserversRpc(ExcludeOwner = true, RunLocally = false)]
        private void RpcPlayTracer(Vector3 start, Vector3 end)
        {
            if (_tracerPrefab != null)
            {
                BulletTracer tracer = Instantiate(_tracerPrefab, start, Quaternion.identity);
                tracer.Play(start, end);
            }
            ShotEvents.RaiseShotFired(base.NetworkObject, _gun != null ? _gun.Data : null, start, end);
        }

        [ObserversRpc(ExcludeOwner = false, RunLocally = false)]
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
