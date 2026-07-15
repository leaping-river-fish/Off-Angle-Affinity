// =============================================================================
// CameraCrouchOffset — smoothly lowers the Camera Pivot while crouching.
//
// ARCHITECTURE:
//   Lives on the "Camera Pivot" GameObject, sibling to PlayerCameraController.
//   It is a SEPARATE component rather than logic bolted onto
//   PlayerCameraController, which stays pure look-only and decoupled from
//   movement (see that file's header). This component is the one place
//   movement's crouch progress crosses into the camera/presentation layer.
//
//   The movement controller remains the single source of truth: this script
//   only reads MovementStateMachine.CrouchAmount (0..1) every frame and lerps
//   the pivot's local Y between the standing height (captured from the
//   Inspector-authored transform) and a serialized crouch height. It never
//   writes back to movement state.
//
// MULTIPLAYER NOTE:
//   No IsOwner check needed - the entire Camera Pivot subtree is only
//   activated for the owner (see NetworkPlayerController), and remote
//   MovementStateMachines are disabled, so CrouchAmount is meaningless (and
//   this component's GameObject never runs) on remote instances.
// =============================================================================

using UnityEngine;
using OffAngle.Movement;

namespace OffAngle.Player
{
    public class CameraCrouchOffset : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Leave null to auto-resolve via GetComponentInParent.")]
        [SerializeField] private MovementStateMachine _stateMachine;

        [Header("Heights")]
        [Tooltip("Local Y position of this transform while fully crouched. Standing height is captured automatically from this transform's authored position at Awake.")]
        [SerializeField] private float _crouchPivotY = 0.9f;

        private float _standingPivotY;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_stateMachine == null)
                _stateMachine = GetComponentInParent<MovementStateMachine>();

            _standingPivotY = transform.localPosition.y;
        }

        // LateUpdate so this always reads the CrouchAmount that
        // MovementStateMachine's Update() already advanced this frame.
        private void LateUpdate()
        {
            if (_stateMachine == null) return;

            Vector3 pos = transform.localPosition;
            pos.y = Mathf.Lerp(_standingPivotY, _crouchPivotY, _stateMachine.CrouchAmount);
            transform.localPosition = pos;
        }
    }
}
