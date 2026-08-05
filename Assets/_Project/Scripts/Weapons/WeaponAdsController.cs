// =============================================================================
// WeaponAdsController — central ADS state machine and orchestrator.
//
// ARCHITECTURE:
//   Lives on the weapon holder (Camera Pivot) alongside first-person weapon
//   instances. Polls MovementStateMachine, PlayerGrapple, and PlayerInputReader
//   to determine whether ADS is allowed, then smoothly transitions weapon pose,
//   camera FOV, and mouse sensitivity between hip-fire and ADS states.
//
//   Completely decoupled from Gun and PlayerWeaponController - those handle
//   firing/ammo/reload; this only handles aim state, camera, and weapon visual
//   positioning. No networking: ADS is purely client-side presentation for the
//   owning player (same as PlayerCameraController itself).
//
// PER-WEAPON ADS DATA:
//   Every GunData asset defines its own ADS position, rotation, FOV, transition
//   speed, and sensitivity multiplier. When the active weapon changes (via
//   PlayerWeaponEquipper), this component detects the new Gun and reads its
//   data. ADS exits cleanly during the weapon switch to avoid snapping.
//
// MOVEMENT INTEGRATION:
//   ADS is allowed during all movement states (standing, walking, crouching,
//   sprinting, sliding, airborne, wall running, grappling). ADS and movement
//   are independent - ADS controls weapon/camera presentation while movement
//   controls player velocity. This creates smooth transitions when the player
//   aims while performing any movement action.
//
//   Reload start (manual or auto) → exits ADS immediately (player returns to
//   hip-fire during reload). Detected by polling Gun.IsReloading each frame.
//
// FOV PRIORITY:
//   ADS yields to movement effects (wall-run FOV takes priority over ADS FOV
//   when both are active). When ADS exits, restores DefaultFov rather than
//   assuming a hardcoded value. Movement systems (CameraWallRunEffects) continue
//   calling SetFovTarget independently - no explicit coordination needed.
//
// MULTIPLAYER NOTE:
//   Owner-only component (Camera Pivot is only active for the owning client).
//   No SyncVars or RPCs - ADS state is never replicated. Remote players only
//   see the owner's muzzle flashes and tracers, which already work correctly
//   regardless of ADS state (shots originate from camera center, not weapon muzzle).
// =============================================================================

using System;
using UnityEngine;
using OffAngle.Core;
using OffAngle.Movement;
using OffAngle.Networking;
using OffAngle.Player;

namespace OffAngle.Weapons
{
    public class WeaponAdsController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Leave null to auto-resolve via GetComponentInParent.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("Leave null to auto-resolve via GetComponent or GetComponentInParent.")]
        [SerializeField] private PlayerCameraController _cameraController;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent.")]
        [SerializeField] private PlayerWeaponController _weaponController;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent.")]
        [SerializeField] private MovementStateMachine _movementStateMachine;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent. Used to check IsGrappling.")]
        [SerializeField] private PlayerGrapple _grapple;

        [Tooltip("Leave null to auto-resolve via GetComponentInParent. Used to gate ADS by input state.")]
        [SerializeField] private PlayerInputStateController _stateController;

        [Tooltip("Transform where Gun instances are spawned (usually this transform itself, or a child named 'First Person Weapon Holder'). ADS positioning is applied here.")]
        [SerializeField] private Transform _weaponHolder;

        [Header("Transition")]
        [Tooltip("Threshold for IsFullyAimed / IsInAdsTransition. AdsBlend >= this value means fully aimed.")]
        [SerializeField, Range(0.9f, 1f)] private float _fullyAimedThreshold = 0.98f;

        // ------------------------------------------------------------------
        // State tracking
        // ------------------------------------------------------------------

        private Gun _currentGun;
        private GunData _currentGunData;

        private Vector3 _hipLocalPosition;
        private Quaternion _hipLocalRotation;

        private float _adsBlend; // 0 = hip, 1 = fully aimed
        private float _targetAdsBlend;

        private bool _isAdsInputHeld;
        private float _originalSensitivity;
        private bool _hasStoredOriginalSensitivity;

        // Tracks whether we are currently applying ADS sensitivity/FOV so we
        // know whether to restore them on exit.
        private bool _isAdsEffectsActive;

        // Track previous reload state to detect when reload starts (manual or auto).
        private bool _wasReloading;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>True if the aim input is currently held (regardless of whether ADS is allowed).</summary>
        public bool IsAdsInputHeld => _isAdsInputHeld;

        /// <summary>True if ADS is allowed by movement state and weapon support.</summary>
        public bool IsAdsAllowed => IsAdsAllowedByMovement() && IsWeaponSupportsAds();

        /// <summary>True if ADS blend is at or above the fully-aimed threshold.</summary>
        public bool IsFullyAimed => _adsBlend >= _fullyAimedThreshold;

        /// <summary>True if ADS blend is between 0 and the fully-aimed threshold (transitioning in or out).</summary>
        public bool IsInAdsTransition => _adsBlend > 0f && _adsBlend < _fullyAimedThreshold;

        /// <summary>Current ADS blend from 0 (hip) to 1 (fully aimed). Smoothly interpolated.</summary>
        public float AdsBlend => _adsBlend;

        /// <summary>Raised once when ADS input is pressed and movement allows ADS.</summary>
        public event Action OnAdsStarted;

        /// <summary>Raised once when ADS blend reaches the fully-aimed threshold.</summary>
        public event Action OnAdsFullyEntered;

        /// <summary>Raised once when ADS exits (blend returns to 0).</summary>
        public event Action OnAdsExited;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_inputReader == null)
                _inputReader = GetComponentInParent<PlayerInputReader>();

            if (_cameraController == null)
            {
                _cameraController = GetComponent<PlayerCameraController>();
                if (_cameraController == null)
                    _cameraController = GetComponentInParent<PlayerCameraController>();
                if (_cameraController == null)
                    _cameraController = GetComponentInChildren<PlayerCameraController>();
            }

            if (_weaponController == null)
                _weaponController = GetComponentInParent<PlayerWeaponController>();

            if (_movementStateMachine == null)
                _movementStateMachine = GetComponentInParent<MovementStateMachine>();

            if (_grapple == null)
                _grapple = GetComponentInParent<PlayerGrapple>();

            if (_stateController == null)
                _stateController = GetComponentInParent<PlayerInputStateController>();

            if (_weaponHolder == null)
                _weaponHolder = transform;
        }

        private void OnEnable()
        {
            if (_inputReader != null)
            {
                _inputReader.AimChanged += OnAimChanged;
            }

            if (_stateController != null)
            {
                _stateController.OnStateChanged += HandleInputStateChanged;
            }

            // Seed from current input state in case this component was enabled
            // mid-aim (e.g. respawn while key held).
            _isAdsInputHeld = _inputReader != null && _inputReader.IsAiming;

            // Store original sensitivity on first enable.
            if (_cameraController != null && !_hasStoredOriginalSensitivity)
            {
                _originalSensitivity = _cameraController.Sensitivity;
                _hasStoredOriginalSensitivity = true;
            }
        }

        private void OnDisable()
        {
            if (_inputReader != null)
            {
                _inputReader.AimChanged -= OnAimChanged;
            }

            if (_stateController != null)
            {
                _stateController.OnStateChanged -= HandleInputStateChanged;
            }

            // Force exit ADS on disable (death, respawn, etc.).
            ForceExitAds();
        }

        private void Update()
        {
            // Detect weapon switches by polling CurrentGun each frame.
            // PlayerWeaponEquipper.RefreshActiveGun() is called on switch.
            if (_weaponController != null && _weaponController.CurrentGun != _currentGun)
            {
                HandleWeaponChanged(_weaponController.CurrentGun);
            }

            // Detect reload start (manual or auto-reload) by polling Gun.IsReloading.
            if (_currentGun != null)
            {
                bool isReloading = _currentGun.IsReloading;
                if (isReloading && !_wasReloading)
                {
                    // Reload just started → exit ADS.
                    OnReloadStarted();
                }
                _wasReloading = isReloading;
            }

            // Update target blend based on input and movement restrictions.
            UpdateTargetAdsBlend();

            // Smoothly interpolate current blend toward target.
            UpdateAdsBlend();

            // Apply weapon position/rotation, FOV, and sensitivity.
            ApplyAdsEffects();
        }

        // ------------------------------------------------------------------
        // Input callbacks
        // ------------------------------------------------------------------

        private void OnAimChanged(bool isAiming)
        {
            _isAdsInputHeld = isAiming;

            // Raise OnAdsStarted only if ADS is allowed right now.
            if (isAiming && IsAdsAllowed)
            {
                OnAdsStarted?.Invoke();
            }
        }

        private void OnReloadStarted()
        {
            // Reload (manual or auto) → exit ADS immediately.
            if (_adsBlend > 0f)
            {
                _isAdsInputHeld = false; // Override input to force exit.
            }
        }

        // ------------------------------------------------------------------
        // Weapon switching
        // ------------------------------------------------------------------

        private void HandleWeaponChanged(Gun newGun)
        {
            // Exit ADS cleanly on weapon switch to avoid snapping.
            if (_adsBlend > 0f)
            {
                _isAdsInputHeld = false; // Override input to force exit.
                _targetAdsBlend = 0f;
            }

            _currentGun = newGun;
            _currentGunData = newGun != null ? newGun.Data : null;

            // Cache the new weapon's hip-fire pose from its current local transform.
            // Gun instances are instantiated at (0,0,0) rotation/position by
            // PlayerWeaponEquipper, so this captures the authored prefab pose.
            if (_weaponHolder != null)
            {
                _hipLocalPosition = _weaponHolder.localPosition;
                _hipLocalRotation = _weaponHolder.localRotation;
            }
        }

        // ------------------------------------------------------------------
        // ADS state update
        // ------------------------------------------------------------------

        private void UpdateTargetAdsBlend()
        {
            // Target is 1 if input held AND movement/weapon allows ADS, else 0.
            bool shouldBeAimed = _isAdsInputHeld && IsAdsAllowed;
            _targetAdsBlend = shouldBeAimed ? 1f : 0f;
        }

        private void UpdateAdsBlend()
        {
            if (_currentGunData == null)
            {
                _adsBlend = 0f;
                return;
            }

            float previousBlend = _adsBlend;

            // Use MoveTowards for frame-rate independent interpolation.
            // Speed is data.AdsTransitionSpeed units per second.
            float speed = _currentGunData.AdsTransitionSpeed;
            _adsBlend = Mathf.MoveTowards(_adsBlend, _targetAdsBlend, speed * Time.deltaTime);

            // Raise events on state changes.
            if (previousBlend < _fullyAimedThreshold && _adsBlend >= _fullyAimedThreshold)
            {
                OnAdsFullyEntered?.Invoke();
            }

            if (previousBlend > 0f && _adsBlend == 0f)
            {
                OnAdsExited?.Invoke();
            }
        }

        // ------------------------------------------------------------------
        // Apply ADS effects (weapon pose, FOV, sensitivity)
        // ------------------------------------------------------------------

        private void ApplyAdsEffects()
        {
            if (_currentGunData == null || _weaponHolder == null)
                return;

            // 1. Weapon position and rotation.
            Vector3 targetPosition = Vector3.Lerp(_hipLocalPosition, _hipLocalPosition + _currentGunData.AdsLocalPosition, _adsBlend);
            Quaternion adsRotation = Quaternion.Euler(_currentGunData.AdsLocalRotation);
            Quaternion targetRotation = Quaternion.Slerp(_hipLocalRotation, _hipLocalRotation * adsRotation, _adsBlend);

            _weaponHolder.localPosition = targetPosition;
            _weaponHolder.localRotation = targetRotation;

            // 2. Camera FOV (only update when transitioning in/out, not every frame).
            if (_cameraController != null)
            {
                bool shouldApplyAdsFov = _adsBlend > 0f;

                if (shouldApplyAdsFov && !_isAdsEffectsActive)
                {
                    // Entering ADS: set FOV target.
                    _cameraController.SetFovTarget(_currentGunData.AdsFov);
                    _isAdsEffectsActive = true;
                }
                else if (!shouldApplyAdsFov && _isAdsEffectsActive)
                {
                    // Exiting ADS: restore default FOV.
                    // Check if wall-running to avoid overwriting movement FOV.
                    if (_movementStateMachine == null || !_movementStateMachine.IsWallRunning)
                    {
                        _cameraController.SetFovTarget(_cameraController.DefaultFov);
                    }
                    _isAdsEffectsActive = false;
                }
            }

            // 3. Mouse sensitivity (blend continuously for smooth feel).
            if (_cameraController != null && _hasStoredOriginalSensitivity)
            {
                float sensitivityMultiplier = Mathf.Lerp(1f, _currentGunData.AdsSensitivityMultiplier, _adsBlend);
                _cameraController.Sensitivity = _originalSensitivity * sensitivityMultiplier;
            }
        }

        // ------------------------------------------------------------------
        // Movement restrictions
        // ------------------------------------------------------------------

        private bool IsAdsAllowedByMovement()
        {
            // ADS is only allowed in Gameplay state (not in Menu or Dead).
            if (_stateController != null && _stateController.CurrentState != PlayerInputState.Gameplay)
                return false;

            // ADS is allowed during all movement states including sprinting and
            // sliding. ADS takes visual priority (weapon positioning, FOV, 
            // sensitivity) while the movement system independently controls speed.
            return true;
        }

        private bool IsWeaponSupportsAds()
        {
            if (_currentGunData == null)
                return false;

            return _currentGunData.SupportsAds;
        }

        // ------------------------------------------------------------------
        // Force exit (death, disable, etc.)
        // ------------------------------------------------------------------

        private void ForceExitAds()
        {
            _isAdsInputHeld = false;
            _targetAdsBlend = 0f;
            _adsBlend = 0f;

            // Restore FOV and sensitivity immediately (no smooth transition).
            if (_cameraController != null)
            {
                _cameraController.SetFovTarget(_cameraController.DefaultFov);

                if (_hasStoredOriginalSensitivity)
                    _cameraController.Sensitivity = _originalSensitivity;
            }

            _isAdsEffectsActive = false;
        }

        // ------------------------------------------------------------------
        // Input state integration
        // ------------------------------------------------------------------

        /// <summary>
        /// React to PlayerInputStateController state changes. Exit ADS when
        /// entering Menu or Dead state to prevent the player from remaining
        /// aimed while unable to fire.
        /// </summary>
        private void HandleInputStateChanged(PlayerInputState oldState, PlayerInputState newState)
        {
            // Exit ADS when leaving Gameplay state
            if (oldState == PlayerInputState.Gameplay && newState != PlayerInputState.Gameplay)
            {
                ForceExitAds();
            }
        }
    }
}
