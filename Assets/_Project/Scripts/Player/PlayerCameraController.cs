// =============================================================================
// PlayerCameraController — FPS pitch/yaw look, Keyboard+Mouse only.
//
// ARCHITECTURE:
//   Lives on the Camera child GameObject; completely decoupled from movement.
//   Subscribes to PlayerInputReader.LookEvent and applies:
//     Pitch (up/down) → _pitchTarget's localRotation.x
//     Yaw (left/right) → _playerRoot transform's rotation.y (whole body)
//     Roll (wall-run tilt) → _pitchTarget's localRotation.z (smoothed target
//       set via SetRollTarget by CameraWallRunEffects)
//
//   _pitchTarget defaults to this transform (camera-only pitch) if left
//   unassigned, but should be pointed at the shared camera/weapon pivot
//   (e.g. "Camera Pivot") that also parents the first-person weapon holder
//   and arms - otherwise looking up/down only tilts the camera and the
//   viewmodel visibly stays level while the world doesn't.
//
//   FOV / roll presentation is driven by other components calling
//   SetFovTarget / SetRollTarget / ResetWallRunCamera - this class never
//   polls movement state itself (see CameraWallRunEffects).
//
// INPUT HANDLING:
//   Mouse delta is a per-frame pixel value, NOT a rate. Do NOT multiply by
//   Time.deltaTime — that would make sensitivity frame-rate dependent in the
//   wrong direction (faster frames = less mouse movement per frame).
//   Deltas are accumulated from events and flushed once per Update so that
//   multiple Input System events fired in a single frame are all captured.
//
// MULTIPLAYER NOTE:
//   Disable this component on remote player instances the same way
//   PlayerInputReader is disabled. Remote pitch/yaw are replicated as
//   network variables and applied directly without this component.
// =============================================================================

using UnityEngine;
using OffAngle.Core;

namespace OffAngle.Player
{
    public class PlayerCameraController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The player root that owns the CharacterController. " +
                 "Leave null to auto-resolve as transform.parent.")]
        [SerializeField] private Transform         _playerRoot;
        [Tooltip("Transform that receives pitch (up/down look) rotation. Leave " +
                 "unassigned to pitch only this transform (camera-only, the old " +
                 "default). Point this at the shared pivot that also parents the " +
                 "first-person weapon holder/arms (e.g. \"Camera Pivot\") so the " +
                 "gun and arms tilt with the camera instead of staying level.")]
        [SerializeField] private Transform         _pitchTarget;
        [SerializeField] private PlayerInputReader _inputReader;
        [Tooltip("Camera whose field of view is smoothed for wall-run FOV. Leave null to auto-resolve on this GameObject or a child.")]
        [SerializeField] private Camera            _camera;

        [Header("Sensitivity")]
        [Tooltip("Degrees of rotation per pixel of mouse movement.")]
        [SerializeField] private float _sensitivity = 0.15f;
        [SerializeField] private bool  _invertY     = false;

        [Header("Pitch Clamp (degrees)")]
        [SerializeField, Range(1f, 89f)] private float _maxPitchUp   = 89f;
        [SerializeField, Range(1f, 89f)] private float _maxPitchDown = 89f;

        [Header("Wall-Run Camera Smoothing")]
        [Tooltip("Seconds to ease FOV toward SetFovTarget (tutorial DoFov uses ~0.25s).")]
        [SerializeField] private float _fovBlendDuration = 0.25f;
        [Tooltip("Seconds to ease roll toward SetRollTarget (tutorial DoTilt uses ~0.25s).")]
        [SerializeField] private float _rollBlendDuration = 0.25f;

        // Accumulated Euler angles
        private float _pitch; // applied to camera localRotation.x
        private float _yaw;   // applied to player root rotation.y
        private float _roll;  // wall-run dutch tilt on pitch target local Z

        private float _fov;
        private float _fovTarget;
        private float _rollTarget;
        private float _defaultFov;
        private float _defaultRoll;

        // Accumulated mouse delta since last Update (flushed each frame)
        private Vector2 _pendingDelta;

        /// <summary>FOV captured at Awake (or current Camera.fieldOfView). CameraWallRunEffects can restore to this.</summary>
        public float DefaultFov => _defaultFov;

        /// <summary>
        /// The actual Camera's transform (falls back to this component's transform
        /// if no Camera is resolved). Used by WeaponAdsController to solve the
        /// weapon-holder pose that aligns a gun's SightVector with the camera.
        /// </summary>
        public Transform CameraTransform => _camera != null ? _camera.transform : transform;

        /// <summary>
        /// Current mouse sensitivity in degrees per pixel. Can be modified at
        /// runtime by ADS or other systems. WeaponAdsController multiplies this
        /// during aim to reduce sensitivity for precise aiming.
        /// </summary>
        public float Sensitivity
        {
            get => _sensitivity;
            set => _sensitivity = value;
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_playerRoot == null)
                _playerRoot = transform.parent;

            if (_pitchTarget == null)
                _pitchTarget = transform;

            if (_inputReader == null)
                _inputReader = GetComponentInParent<PlayerInputReader>();

            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
                if (_camera == null)
                    _camera = GetComponentInChildren<Camera>();
            }

            _defaultFov = _camera != null ? _camera.fieldOfView : 80f;
            _fov = _defaultFov;
            _fovTarget = _defaultFov;

            // Captured once, before any wall-run tilt (SetRollTarget) has ever
            // run, so it reflects only whatever Z a designer authored on the
            // pitch target (usually 0) - see OnEnable for why this must not be
            // re-read from the live transform on every enable.
            _defaultRoll = NormalizeAngle(_pitchTarget.localEulerAngles.z);
            _roll = _defaultRoll;
            _rollTarget = _defaultRoll;
        }

        private void OnEnable()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            // Seed angles from current transforms to avoid a snap on enable
            _yaw   = _playerRoot.eulerAngles.y;
            _pitch = NormalizeAngle(_pitchTarget.localEulerAngles.x);

            // Unlike pitch/yaw, roll has no legitimate external driver - it's
            // wall-run dutch tilt, entirely owned by this component via
            // SetRollTarget (see class header). Reading the live value back
            // here would reinstate whatever tilt was frozen in place when this
            // GameObject got disabled mid wall-run (e.g. on death), leaving it
            // stuck post-respawn since Update() stops applying MoveTowards
            // while disabled - same failure mode as the FOV reset below.
            _roll = _defaultRoll;
            _rollTarget = _defaultRoll;

            // Unlike pitch/yaw, FOV has no legitimate external driver - this
            // component is the sole owner of Camera.fieldOfView (see class
            // header). Reading the live value back here would just reinstate
            // whatever ability/ADS FOV was frozen in place when this GameObject
            // got disabled (e.g. on death), leaving it stuck post-respawn since
            // Update() stops applying MoveTowards while disabled.
            _fov = _defaultFov;
            _fovTarget = _defaultFov;
            if (_camera != null)
                _camera.fieldOfView = _fov;

            // Apply the reset pitch/roll immediately so the first rendered
            // frame doesn't show one frame of the stale pre-disable rotation
            // before Update() ticks.
            ApplyPitchRollRotation();

            _inputReader.LookEvent += OnLook;
        }

        private void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;

            _inputReader.LookEvent -= OnLook;
            _pendingDelta = Vector2.zero;
        }

        private void Update()
        {
            if (_pendingDelta != Vector2.zero)
            {
                ApplyLook(_pendingDelta);
                _pendingDelta = Vector2.zero;
            }

            // FOV/roll must ease even when the mouse is still - otherwise
            // wall-run enter/exit camera FX only updates on look input.
            ApplyCameraPresentation(Time.deltaTime);
        }

        // ------------------------------------------------------------------
        // Public presentation API (CameraWallRunEffects)
        // ------------------------------------------------------------------

        /// <summary>Smoothly ease Camera.fieldOfView toward <paramref name="fov"/>.</summary>
        public void SetFovTarget(float fov)
        {
            _fovTarget = fov;
        }

        /// <summary>Smoothly ease pitch-target local Z roll toward <paramref name="rollDegrees"/> (negative = left wall, positive = right).</summary>
        public void SetRollTarget(float rollDegrees)
        {
            _rollTarget = rollDegrees;
        }

        /// <summary>Restore default FOV and zero roll (wall-run exit).</summary>
        public void ResetWallRunCamera()
        {
            _fovTarget = _defaultFov;
            _rollTarget = 0f;
        }

        // ------------------------------------------------------------------
        // Input accumulation
        // ------------------------------------------------------------------

        // Called by Input System callback — may fire multiple times per frame
        private void OnLook(Vector2 delta) => _pendingDelta += delta;

        // ------------------------------------------------------------------
        // Rotation / presentation application
        // ------------------------------------------------------------------

        private void ApplyLook(Vector2 delta)
        {
            float yawDelta   =  delta.x * _sensitivity;
            float pitchDelta = -delta.y * _sensitivity * (_invertY ? -1f : 1f);

            _yaw   += yawDelta;
            _pitch  = Mathf.Clamp(_pitch + pitchDelta, -_maxPitchDown, _maxPitchUp);

            // Rotate the whole player body on the Y axis so that movement
            // direction always matches where the player is looking horizontally
            _playerRoot.rotation = Quaternion.Euler(0f, _yaw, 0f);

            ApplyPitchRollRotation();
        }

        private void ApplyCameraPresentation(float deltaTime)
        {
            // Degree-per-second rates sized so a typical FOV step (~10) and
            // tilt (±5) finish near _fovBlendDuration / _rollBlendDuration.
            float fovSpeed = 40f / Mathf.Max(0.01f, _fovBlendDuration);
            float rollSpeed = 20f / Mathf.Max(0.01f, _rollBlendDuration);

            _fov = Mathf.MoveTowards(_fov, _fovTarget, fovSpeed * deltaTime);
            _roll = Mathf.MoveTowards(_roll, _rollTarget, rollSpeed * deltaTime);

            if (_camera != null)
                _camera.fieldOfView = _fov;

            ApplyPitchRollRotation();
        }

        private void ApplyPitchRollRotation()
        {
            // Pitch + wall-run roll on the shared pivot so viewmodel tilts too
            // when _pitchTarget is Camera Pivot.
            _pitchTarget.localRotation = Quaternion.Euler(_pitch, 0f, _roll);
        }

        // Unity stores Euler angles in 0-360; normalise to -180-180 so clamping
        // (pitch) and delta comparisons behave as expected.
        private static float NormalizeAngle(float degrees)
        {
            if (degrees > 180f) degrees -= 360f;
            return degrees;
        }
    }
}
