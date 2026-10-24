// =============================================================================
// ThirdPersonLocomotionAnimator — the persistent Animator-state writer.
//
// Each LateUpdate it:
//   1. Builds one PlayerAnimSnapshot (owner and remote produce the same shape).
//   2. Writes the persistent Animator parameters from that snapshot.
//   3. Smooths WeaponFocus and sets the Upper Body Aim / Relaxed Weapon
//      layer weights from that one value.
//
// It does NOT decide gameplay state, network anything, aim, or run IK.
//   - Crouch/airborne/etc. come from PlayerAnimFlags (gameplay-derived,
//     composed on the owner and replicated for remotes) via IPlayerAnimStateSource.
//   - Sprint cadence is LocomotionSpeed from planar speed, not a flag.
//   - Velocity: owner uses MovementStateMachine.Velocity (authoritative);
//     remotes use root position delta (their visually interpolated movement).
//   - WeaponFocus reads that same planar speed, the owner's WeaponAdsController,
//     the owner's fire button, and ShotEvents observers already receive.
//     ADS is not replicated, so remotes cannot see aiming. No focus SyncVar.
// =============================================================================

using FishNet.Object;
using UnityEngine;
using OffAngle.Movement;
using OffAngle.Core;
using OffAngle.Weapons;

namespace OffAngle.Player {
    public class ThirdPersonLocomotionAnimator : MonoBehaviour
    {
        private const string UpperBodyAimLayerName = "Upper Body Aim";
        private const string RelaxedWeaponLayerName = "Relaxed Weapon";

        // Full 0↔1 change. Scaled time, so a pause freezes the blend.
        private const float FocusBlendSeconds = 0.2f;

        // PlanarSpeed at or below this is the locomotion system standing still.
        // Crouch-walk is 2.5 m/s, so this only absorbs interpolation noise.
        private const float StationaryPlanarSpeed = 0.25f;

        // Discrete ShotFired / PelletFired / ProjectileSpawned are pulses.
        // Hold desired focus across the gap between rounds (pistol is 5/s).
        // Longer gaps (shotgun, slug) count as not currently firing.
        private const float RemoteFireHoldSeconds = 0.3f;

        [SerializeField] private Animator _animator;
        [SerializeField] private MovementStateMachine _movementStateMachine;
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("Horizontal speed (m/s) that maps to blend-tree length 1. Matches WalkSpeed (~7); sprint is clamped to the unit circle.")]
        [SerializeField] private float _referenceSpeed = 7f;

        [Tooltip("Animator.SetFloat damp time. Higher = slower blend.")]
        [SerializeField] private float _dampTime = 0.12f;

        [Tooltip("Upper clamp for Locomotion playback speed (planar speed / reference speed, never below 1). Sprint 10/7 is ~1.43.")]
        [SerializeField] private float _maxLocomotionSpeed = 1.5f;

        [Header("Debug (read-only)")]
        [SerializeField] private PlayerAnimSnapshot _snapshot;

        // 1 on spawn so a player who appears standing still does not flash the
        // controller default (Upper Body Aim 0, Relaxed Weapon 1).
        [SerializeField] private float _weaponFocus = 1f;

        /// <summary>The snapshot written to the Animator this frame. Read-only for debugging.</summary>
        public PlayerAnimSnapshot Snapshot => _snapshot;

        /// <summary>0 = relaxed weapon pose, 1 = focused weapon pose.</summary>
        public float WeaponFocus => _weaponFocus;

        private static readonly int MoveXHash = Animator.StringToHash("MoveX");
        private static readonly int MoveYHash = Animator.StringToHash("MoveY");
        private static readonly int LocomotionSpeedHash = Animator.StringToHash("LocomotionSpeed");

        private Vector3 _lastPosition;
        private bool _hasLastPosition;
        private bool _hasLocomotionSpeedParam;
        private IPlayerAnimStateSource _flagSource;

        private WeaponAdsController _adsController;
        private NetworkObject _playerNetworkObject;
        private int _upperBodyAimLayer = -1;
        private int _relaxedWeaponLayer = -1;
        private bool _focusLayersResolved;
        private bool _focusLayersReady;
        private bool _appliedInitialFocus;
        private bool _fireHeld;
        private bool _remoteBeamActive;
        private float _remoteFireUntil;

        private void Awake()
        {
            if (_inputReader == null) _inputReader = GetComponent<PlayerInputReader>();

            if (_movementStateMachine == null) _movementStateMachine = GetComponent<MovementStateMachine>();

            if (_animator == null) _animator = GetComponentInChildren<Animator>(true);

            _flagSource = GetComponent<IPlayerAnimStateSource>();
            _adsController = GetComponentInChildren<WeaponAdsController>(true);
            _playerNetworkObject = GetComponentInParent<NetworkObject>();

            if (_playerNetworkObject == null)
            {
                Debug.LogWarning(
                    $"[{nameof(ThirdPersonLocomotionAnimator)}] No NetworkObject found on '{name}'. Remote shot focus will not run.",
                    this);
            }

            CacheAnimatorParameters();
        }

        private void OnEnable()
        {
            if (_inputReader != null)
            {
                _inputReader.FireStarted += HandleFireStarted;
                _inputReader.FireCanceled += HandleFireCanceled;
            }

            ShotEvents.ShotFired += HandleShotFired;
            ShotEvents.PelletFired += HandlePelletFired;
            ShotEvents.ProjectileSpawned += HandleProjectileSpawned;
            ShotEvents.BeamStarted += HandleBeamStarted;
            ShotEvents.BeamUpdated += HandleBeamUpdated;
            ShotEvents.BeamStopped += HandleBeamStopped;
        }

        private void OnDisable()
        {
            if (_inputReader != null)
            {
                _inputReader.FireStarted -= HandleFireStarted;
                _inputReader.FireCanceled -= HandleFireCanceled;
            }

            ShotEvents.ShotFired -= HandleShotFired;
            ShotEvents.PelletFired -= HandlePelletFired;
            ShotEvents.ProjectileSpawned -= HandleProjectileSpawned;
            ShotEvents.BeamStarted -= HandleBeamStarted;
            ShotEvents.BeamUpdated -= HandleBeamUpdated;
            ShotEvents.BeamStopped -= HandleBeamStopped;

            _fireHeld = false;
            _remoteBeamActive = false;
        }

        private void Start()
        {
            TryApplyInitialFocus();
        }

        // Animator init can land after Start. Retry before the pose evaluates
        // so spawn-still still writes focus 1 ahead of the controller defaults.
        // Stop once the layers are resolved, including when one is missing.
        private void Update()
        {
            if (_appliedInitialFocus || (_focusLayersResolved && !_focusLayersReady))
                return;

            TryApplyInitialFocus();
        }

        private void LateUpdate()
        {
            if (_animator == null || !_animator.enabled) return;

            BuildSnapshot();
            WriteAnimator();
            UpdateWeaponFocus();
        }

        private void BuildSnapshot()
        {
            // Sampled every frame on both paths so _lastPosition is never stale
            // when the owner path toggles (death, menu) back to the remote path.
            Vector3 positionVelocity = SampleRootVelocity();

            Vector3 worldVelocity = IsSimulatedLocally()
                ? _movementStateMachine.Velocity
                : positionVelocity;

            PlayerAnimFlags flags = PlayerAnimFlags.None;
            _flagSource?.TryGetFlags(out flags);

            bool airborne = (flags & PlayerAnimFlags.Airborne) != 0;

            Vector3 planarWorld = new Vector3(worldVelocity.x, 0f, worldVelocity.z);
            Vector3 local = transform.InverseTransformDirection(planarWorld);
            Vector2 planar = new Vector2(local.x, local.z);
            float planarSpeed = planar.magnitude;

            planar /= Mathf.Max(0.01f, _referenceSpeed);
            if (planar.sqrMagnitude > 1f) planar.Normalize();

            _snapshot = new PlayerAnimSnapshot
            {
                MoveX = planar.x,
                MoveY = planar.y,
                PlanarSpeed = planarSpeed,
                LocomotionSpeed = Mathf.Clamp(planarSpeed / Mathf.Max(0.01f, _referenceSpeed), 1f, Mathf.Max(1f, _maxLocomotionSpeed)),
                // Owner Velocity.y is pinned to -2 on the ground, so define
                // vertical speed as 0 while grounded on every peer.
                VerticalSpeed = airborne ? worldVelocity.y : 0f,
                IsCrouching = (flags & PlayerAnimFlags.Crouched) != 0,
                IsGrounded = !airborne,
                IsAirborne = airborne,
                IsSliding = (flags & PlayerAnimFlags.Sliding) != 0,
                IsWallRunning = (flags & PlayerAnimFlags.WallRunning) != 0,
                IsAbilityMoving = (flags & PlayerAnimFlags.AbilityMoving) != 0,
            };
        }

        private void WriteAnimator()
        {
            _animator.SetFloat(MoveXHash, _snapshot.MoveX, _dampTime, Time.deltaTime);
            _animator.SetFloat(MoveYHash, _snapshot.MoveY, _dampTime, Time.deltaTime);

            if (_hasLocomotionSpeedParam)
                _animator.SetFloat(LocomotionSpeedHash, _snapshot.LocomotionSpeed, _dampTime, Time.deltaTime);
        }

        private void TryApplyInitialFocus()
        {
            TryResolveFocusLayers();
            if (!_focusLayersReady) return;

            ApplyFocusWeights();
            _appliedInitialFocus = true;
        }

        private void UpdateWeaponFocus()
        {
            TryResolveFocusLayers();
            if (!_focusLayersReady) return;

            float desired = ComputeDesiredFocus();
            float step = Time.deltaTime / Mathf.Max(0.0001f, FocusBlendSeconds);
            _weaponFocus = Mathf.MoveTowards(_weaponFocus, desired, step);
            ApplyFocusWeights();
        }

        private float ComputeDesiredFocus()
        {
            bool moving = _snapshot.PlanarSpeed > StationaryPlanarSpeed;
            bool aiming = _adsController != null && _adsController.IsAdsInputHeld;
            bool shooting = IsShooting();
            return (aiming || shooting || !moving) ? 1f : 0f;
        }

        private bool IsShooting()
        {
            if (IsSimulatedLocally())
                return _fireHeld;

            return _remoteBeamActive || Time.time < _remoteFireUntil;
        }

        private void ApplyFocusWeights()
        {
            if (!_focusLayersReady || _animator == null) return;

            float relaxed = 1f - _weaponFocus;
            if (Mathf.Approximately(_animator.GetLayerWeight(_upperBodyAimLayer), _weaponFocus)
                && Mathf.Approximately(_animator.GetLayerWeight(_relaxedWeaponLayer), relaxed))
                return;

            _animator.SetLayerWeight(_upperBodyAimLayer, _weaponFocus);
            _animator.SetLayerWeight(_relaxedWeaponLayer, relaxed);
        }

        private void TryResolveFocusLayers()
        {
            if (_focusLayersResolved || _animator == null || !_animator.isInitialized)
                return;

            _upperBodyAimLayer = _animator.GetLayerIndex(UpperBodyAimLayerName);
            _relaxedWeaponLayer = _animator.GetLayerIndex(RelaxedWeaponLayerName);
            _focusLayersResolved = true;

            if (_upperBodyAimLayer >= 0 && _relaxedWeaponLayer >= 0)
            {
                _focusLayersReady = true;
                return;
            }

            string missing = _upperBodyAimLayer < 0 && _relaxedWeaponLayer < 0
                ? $"'{UpperBodyAimLayerName}' and '{RelaxedWeaponLayerName}'"
                : _upperBodyAimLayer < 0
                    ? $"'{UpperBodyAimLayerName}'"
                    : $"'{RelaxedWeaponLayerName}'";

            Debug.LogWarning(
                $"[{nameof(ThirdPersonLocomotionAnimator)}] Animator layer {missing} was not found on '{name}'. Weapon focus weights will not be driven.",
                this);
        }

        private void HandleFireStarted() => _fireHeld = true;

        private void HandleFireCanceled() => _fireHeld = false;

        private void HandleShotFired(NetworkObject attacker, GunData weapon, Vector3 origin, Vector3 end)
            => NoteRemoteShot(attacker);

        private void HandlePelletFired(NetworkObject attacker, GunData weapon, Vector3 origin, Vector3 end)
            => NoteRemoteShot(attacker);

        private void HandleProjectileSpawned(NetworkObject attacker, GunData weapon, NetworkObject projectile)
            => NoteRemoteShot(attacker);

        private void HandleBeamStarted(NetworkObject attacker, GunData weapon)
            => NoteRemoteBeam(attacker, true);

        private void HandleBeamUpdated(NetworkObject attacker, GunData weapon, Vector3 origin, Vector3 endPoint, bool didHit)
            => NoteRemoteBeam(attacker, true);

        private void HandleBeamStopped(NetworkObject attacker, GunData weapon)
            => NoteRemoteBeam(attacker, false);

        // Owner hitscan tracers are ExcludeOwner, and pellet/projectile RPCs
        // arrive after the local trigger. The owner uses the fire button.
        private void NoteRemoteShot(NetworkObject attacker)
        {
            if (!IsThisPlayer(attacker) || IsSimulatedLocally()) return;
            _remoteFireUntil = Time.time + RemoteFireHoldSeconds;
        }

        private void NoteRemoteBeam(NetworkObject attacker, bool active)
        {
            if (!IsThisPlayer(attacker) || IsSimulatedLocally()) return;
            _remoteBeamActive = active;
        }

        private bool IsThisPlayer(NetworkObject attacker)
        {
            return attacker != null && _playerNetworkObject != null && attacker == _playerNetworkObject;
        }

        private bool IsSimulatedLocally()
        {
            return _movementStateMachine != null
                && _movementStateMachine.enabled
                && _inputReader != null
                && _inputReader.enabled;
        }

        /// <summary>Root position delta per second. Always advances _lastPosition.</summary>
        private Vector3 SampleRootVelocity()
        {
            Vector3 position = transform.position;
            Vector3 velocity = Vector3.zero;

            if (_hasLastPosition)
            {
                Vector3 delta = position - _lastPosition;

                // Spawn/teleport: do not flash a huge one-frame "run".
                if (delta.sqrMagnitude <= 25f)
                    velocity = delta / Mathf.Max(Time.deltaTime, 0.0001f);
            }

            _lastPosition = position;
            _hasLastPosition = true;
            return velocity;
        }

        private void CacheAnimatorParameters()
        {
            _hasLocomotionSpeedParam = false;
            if (_animator == null) return;

            AnimatorControllerParameter[] parameters = _animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].nameHash == LocomotionSpeedHash && parameters[i].type == AnimatorControllerParameterType.Float)
                    _hasLocomotionSpeedParam = true;
            }
        }
    }
}
