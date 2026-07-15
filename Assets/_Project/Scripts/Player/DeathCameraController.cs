// =============================================================================
// DeathCameraController — third-person orbit camera shown while dead.
//
// ARCHITECTURE:
//   Lives on the "Death Camera" GameObject, a sibling of "First Person Camera"
//   under Camera Pivot. PlayerLifecycleController SetActive-swaps between the
//   two exactly the way NetworkPlayerController already swaps Camera Pivot
//   itself for ownership - no new dependency, no Cinemachine.
//
//   Reuses PlayerInputReader.LookEvent (the same event PlayerCameraController
//   subscribes to) so mouse-look keeps working while dead, even though
//   MovementStateMachine and weapon input are locked - satisfying "look
//   around... unless required by the death camera" without PlayerInputReader
//   itself ever being disabled.
//
// BLEND:
//   On enable, the camera starts at the outgoing first-person camera's last
//   world pose (via _blendFromTransform) and lerps to the orbit pose over
//   _blendDuration seconds, so the FPS -> death-cam cut does not feel jarring.
//
// ORBIT:
//   Standard pivot-orbit math: the pivot is a point above the player's death
//   position (roughly chest height on the fallen capsule); the camera sits at
//   pivot + (rotation * back) * distance and looks back at the pivot.
// =============================================================================

using UnityEngine;
using OffAngle.Core;

namespace OffAngle.Player
{
    public class DeathCameraController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The player root whose death position the camera orbits. Leave null to auto-resolve as transform.parent.parent (Camera Pivot's parent).")]
        [SerializeField] private Transform _playerRoot;
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("The outgoing first-person camera transform. Its world pose at the moment this camera is enabled is used as the blend start, so the transition is smooth rather than an instant cut.")]
        [SerializeField] private Transform _blendFromTransform;

        [Header("Orbit")]
        [Tooltip("Height above the player root's death position that the camera orbits around, roughly chest height on the fallen capsule.")]
        [SerializeField] private float _pivotHeight = 0.9f;
        [SerializeField] private float _distance = 3.5f;
        [SerializeField, Range(-10f, 80f)] private float _defaultPitch = 20f;
        [SerializeField, Range(-10f, 80f)] private float _minPitch = -5f;
        [SerializeField, Range(-10f, 80f)] private float _maxPitch = 70f;

        [Header("Sensitivity")]
        [Tooltip("Degrees of rotation per pixel of mouse movement.")]
        [SerializeField] private float _sensitivity = 0.15f;
        [SerializeField] private bool _invertY;

        [Header("Blend")]
        [Tooltip("Seconds to lerp from the outgoing first-person pose into the orbit pose.")]
        [SerializeField] private float _blendDuration = 0.4f;

        private float _yaw;
        private float _pitch;
        private Vector2 _pendingDelta;

        private Vector3 _blendStartPosition;
        private Quaternion _blendStartRotation;
        private float _blendElapsed;

        private Vector3 _deathPivotOrigin;

        private void Awake()
        {
            if (_playerRoot == null)
                _playerRoot = transform.parent != null ? transform.parent.parent : null;

            if (_inputReader == null)
                _inputReader = GetComponentInParent<PlayerInputReader>();
        }

        private void OnEnable()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            _deathPivotOrigin = _playerRoot != null ? _playerRoot.position : transform.position;

            // Seed from the player's facing so the camera doesn't spin to an
            // arbitrary angle on the cut - continuity with the view the player
            // just had.
            _yaw = _playerRoot != null ? _playerRoot.eulerAngles.y : transform.eulerAngles.y;
            _pitch = _defaultPitch;

            _blendElapsed = 0f;
            if (_blendFromTransform != null)
            {
                _blendStartPosition = _blendFromTransform.position;
                _blendStartRotation = _blendFromTransform.rotation;
            }
            else
            {
                _blendStartPosition = transform.position;
                _blendStartRotation = transform.rotation;
            }

            if (_inputReader != null)
                _inputReader.LookEvent += OnLook;
        }

        private void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (_inputReader != null)
                _inputReader.LookEvent -= OnLook;
            _pendingDelta = Vector2.zero;
        }

        private void Update()
        {
            if (_pendingDelta != Vector2.zero)
            {
                ApplyLookDelta(_pendingDelta);
                _pendingDelta = Vector2.zero;
            }

            (Vector3 targetPosition, Quaternion targetRotation) = ComputeOrbitPose();

            if (_blendElapsed < _blendDuration)
            {
                _blendElapsed += Time.deltaTime;
                float t = Mathf.Clamp01(_blendElapsed / _blendDuration);
                transform.SetPositionAndRotation(
                    Vector3.Lerp(_blendStartPosition, targetPosition, t),
                    Quaternion.Slerp(_blendStartRotation, targetRotation, t));
            }
            else
            {
                transform.SetPositionAndRotation(targetPosition, targetRotation);
            }
        }

        private void OnLook(Vector2 delta) => _pendingDelta += delta;

        private void ApplyLookDelta(Vector2 delta)
        {
            float yawDelta = delta.x * _sensitivity;
            float pitchDelta = -delta.y * _sensitivity * (_invertY ? -1f : 1f);

            _yaw += yawDelta;
            _pitch = Mathf.Clamp(_pitch + pitchDelta, _minPitch, _maxPitch);
        }

        private (Vector3 position, Quaternion rotation) ComputeOrbitPose()
        {
            Vector3 pivot = _deathPivotOrigin + Vector3.up * _pivotHeight;
            Quaternion orbitRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 direction = orbitRotation * Vector3.back;
            Vector3 position = pivot + direction * _distance;
            Quaternion rotation = Quaternion.LookRotation((pivot - position).normalized, Vector3.up);
            return (position, rotation);
        }
    }
}
