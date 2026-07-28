// =============================================================================
// CameraCrouchOffset — smoothly lowers the Camera Pivot while crouching OR
// sliding.
//
// ARCHITECTURE:
//   Lives on the "Camera Pivot" GameObject, sibling to PlayerCameraController.
//   It is a SEPARATE component rather than logic bolted onto
//   PlayerCameraController, which stays pure look-only and decoupled from
//   movement (see that file's header). This component is the one place
//   movement's crouch progress crosses into the camera/presentation layer.
//
//   The movement controller remains the single source of truth for WHETHER
//   the camera should be dropped: this script reads
//   MovementStateMachine.CrouchAmount (already 0..1 smoothed by
//   CrouchingState) and MovementStateMachine.IsSliding (a plain bool) every
//   frame. It never writes back to movement state.
//
//   SlidingState deliberately does NOT touch ctx.CrouchAmount or the
//   CharacterController's capsule (see SlidingState.cs header - avoids
//   reintroducing the "ghost hitbox" networking problem NetworkPlayerCrouch
//   had to solve for crouch). The slide camera drop is purely a LOCAL, owner-
//   only cosmetic effect layered on top by THIS component instead - it has
//   no capsule/hitbox/networking implications, since remote peers never see
//   the local owner's own camera.
//
// WHY A SEPARATE LOCALLY-SMOOTHED VALUE FOR SLIDE:
//   CrouchAmount is smoothed by CrouchingState because that same value also
//   drives the capsule height (must ramp in lockstep). Sliding has no such
//   capsule requirement, so it's simplest for this component to smooth its
//   own _slideAmount from the plain IsSliding bool - same idiom
//   NetworkPlayerCrouch already uses to smooth a synced bool into a visual
//   scale. This also means the camera keeps rising back up smoothly even
//   after SlidingState has already exited (e.g. a slide-jump into Airborne).
//
// MULTIPLAYER NOTE:
//   No IsOwner check needed - the entire Camera Pivot subtree is only
//   activated for the owner (see NetworkPlayerController), and remote
//   MovementStateMachines are disabled, so CrouchAmount/IsSliding are
//   meaningless (and this component's GameObject never runs) on remote
//   instances.
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
        [Tooltip("Local Y position of this transform while fully crouched or fully slid. Standing height is captured automatically from this transform's authored position at Awake.")]
        [SerializeField] private float _crouchPivotY = 0.9f;

        [Header("Slide")]
        [Tooltip("Seconds to fully lower/raise the camera pivot when a slide starts/ends. Uses the same target height as crouch (_crouchPivotY) - a slide reads as a fast, low crouch from the camera's point of view.")]
        [SerializeField] private float _slideTransitionDuration = 0.12f;

        private float _standingPivotY;

        // Locally smoothed 0..1 slide drop amount - see header note above for
        // why this isn't just read from MovementStateContext.
        private float _slideAmount;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_stateMachine == null)
                _stateMachine = GetComponentInParent<MovementStateMachine>();

            _standingPivotY = transform.localPosition.y;
        }

        // LateUpdate so this always reads the CrouchAmount/IsSliding that
        // MovementStateMachine's Update() already advanced this frame.
        private void LateUpdate()
        {
            if (_stateMachine == null) return;

            float rate = 1f / Mathf.Max(0.01f, _slideTransitionDuration);
            float slideTarget = _stateMachine.IsSliding ? 1f : 0f;
            _slideAmount = Mathf.MoveTowards(_slideAmount, slideTarget, rate * Time.deltaTime);

            // Crouch and slide lower the camera to the same pivot height -
            // take whichever is stronger so entering one while still easing
            // out of the other (e.g. a slide that ends into a held crouch)
            // never fights itself or pops.
            float amount = Mathf.Max(_stateMachine.CrouchAmount, _slideAmount);

            Vector3 pos = transform.localPosition;
            pos.y = Mathf.Lerp(_standingPivotY, _crouchPivotY, amount);
            transform.localPosition = pos;
        }
    }
}
