// =============================================================================
// CameraWallRunEffects — wall-run FOV widen + dutch-roll tilt (presentation).
//
// ARCHITECTURE:
//   Same pattern as CameraCrouchOffset: lives on the owner camera / Camera
//   Pivot subtree, polls MovementStateMachine, never writes movement state.
//   Calls PlayerCameraController.SetFovTarget / SetRollTarget /
//   ResetWallRunCamera so look code stays the single owner of Camera FOV and
//   pitch-target roll.
//
//   Inspired by Reference Files/PlayerCam.cs DoFov/DoTilt, but uses
//   MoveTowards smoothing inside PlayerCameraController (no DOTween).
//
// MULTIPLAYER NOTE:
//   No IsOwner check needed - the camera subtree is only active for the
//   owner (see NetworkPlayerController).
// =============================================================================

using UnityEngine;
using OffAngle.Movement;

namespace OffAngle.Player
{
    public class CameraWallRunEffects : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Leave null to auto-resolve via GetComponentInParent.")]
        [SerializeField] private MovementStateMachine _stateMachine;
        [Tooltip("Leave null to auto-resolve via GetComponentInParent / GetComponent.")]
        [SerializeField] private PlayerCameraController _cameraController;

        [Header("Wall Run FOV")]
        [Tooltip("Field of view while wall running (tutorial uses 90).")]
        [SerializeField] private float _wallRunFov = 90f;
        [Tooltip("If > 0, used as the restored FOV when leaving a wall run. If 0, uses PlayerCameraController.DefaultFov captured at Awake.")]
        [SerializeField] private float _defaultFovOverride = 0f;

        [Header("Wall Run Tilt")]
        [Tooltip("Absolute roll degrees while wall running. Left wall = negative, right wall = positive (tutorial ±5).")]
        [SerializeField] private float _tiltDegrees = 5f;

        private bool _wasWallRunning;

        private void Awake()
        {
            if (_stateMachine == null)
                _stateMachine = GetComponentInParent<MovementStateMachine>();

            if (_cameraController == null)
            {
                _cameraController = GetComponent<PlayerCameraController>();
                if (_cameraController == null)
                    _cameraController = GetComponentInParent<PlayerCameraController>();
                if (_cameraController == null)
                    _cameraController = GetComponentInChildren<PlayerCameraController>();
            }
        }

        private void OnDisable()
        {
            // Avoid leaving a stuck FOV/tilt if this component is disabled mid-run
            // (death camera swap, etc.).
            if (_wasWallRunning && _cameraController != null)
            {
                ApplyDefaultCamera();
                _wasWallRunning = false;
            }
        }

        // LateUpdate so IsWallRunning / WallRunSide already reflect this frame's
        // MovementStateMachine.Update transition.
        private void LateUpdate()
        {
            if (_stateMachine == null || _cameraController == null)
                return;

            bool wallRunning = _stateMachine.IsWallRunning;

            if (wallRunning)
            {
                _cameraController.SetFovTarget(_wallRunFov);

                float tilt = 0f;
                switch (_stateMachine.WallRunSide)
                {
                    case WallSide.Left:  tilt = -_tiltDegrees; break;
                    case WallSide.Right: tilt =  _tiltDegrees; break;
                }
                _cameraController.SetRollTarget(tilt);
                _wasWallRunning = true;
            }
            else if (_wasWallRunning)
            {
                ApplyDefaultCamera();
                _wasWallRunning = false;
            }
        }

        private void ApplyDefaultCamera()
        {
            float fov = _defaultFovOverride > 0f
                ? _defaultFovOverride
                : _cameraController.DefaultFov;

            _cameraController.SetFovTarget(fov);
            _cameraController.SetRollTarget(0f);
        }
    }
}
