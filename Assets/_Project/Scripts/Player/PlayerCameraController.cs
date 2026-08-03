// =============================================================================
// PlayerCameraController — FPS pitch/yaw look, Keyboard+Mouse only.
//
// ARCHITECTURE:
//   Lives on the Camera child GameObject; completely decoupled from movement.
//   Subscribes to PlayerInputReader.LookEvent and applies:
//     Pitch (up/down) → _pitchTarget's localRotation.x
//     Yaw (left/right) → _playerRoot transform's rotation.y (whole body)
//
//   _pitchTarget defaults to this transform (camera-only pitch) if left
//   unassigned, but should be pointed at the shared camera/weapon pivot
//   (e.g. "Camera Pivot") that also parents the first-person weapon holder
//   and arms - otherwise looking up/down only tilts the camera and the
//   viewmodel visibly stays level while the world doesn't.
//
//   Separating camera from movement lets future states apply independent
//   camera effects (e.g. WallRunningState dutch roll, GrapplingState
//   swing sway) by adding a _roll field and blending it in ApplyRotation().
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

        [Header("Sensitivity")]
        [Tooltip("Degrees of rotation per pixel of mouse movement.")]
        [SerializeField] private float _sensitivity = 0.15f;
        [SerializeField] private bool  _invertY     = false;

        [Header("Pitch Clamp (degrees)")]
        [SerializeField, Range(1f, 89f)] private float _maxPitchUp   = 89f;
        [SerializeField, Range(1f, 89f)] private float _maxPitchDown = 89f;

        // Accumulated Euler angles
        private float _pitch; // applied to camera localRotation.x
        private float _yaw;   // applied to player root rotation.y

        // Accumulated mouse delta since last Update (flushed each frame)
        private Vector2 _pendingDelta;

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
        }

        private void OnEnable()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            // Seed angles from current transforms to avoid a snap on enable
            _yaw   = _playerRoot.eulerAngles.y;
            _pitch = _pitchTarget.localEulerAngles.x;
            // Unity stores pitch in 0–360; normalise to -180–180 for clamping
            if (_pitch > 180f) _pitch -= 360f;

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
            if (_pendingDelta == Vector2.zero)
                return;

            ApplyLook(_pendingDelta);
            _pendingDelta = Vector2.zero;
        }

        // ------------------------------------------------------------------
        // Input accumulation
        // ------------------------------------------------------------------

        // Called by Input System callback — may fire multiple times per frame
        private void OnLook(Vector2 delta) => _pendingDelta += delta;

        // ------------------------------------------------------------------
        // Rotation application
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

            // Rotate only _pitchTarget on the X axis so looking up/down does not
            // tilt the CharacterController capsule (which would break slope logic).
            // Everything parented under _pitchTarget (camera, and - once wired -
            // the first-person weapon holder/arms) tilts along with it for free.
            _pitchTarget.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }
    }
}
