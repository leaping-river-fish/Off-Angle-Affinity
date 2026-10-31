// =============================================================================
// CameraCrouchOffset — copies the animated eye/head anchor onto Camera Pivot.
//
// ARCHITECTURE:
//   Lives on the "Camera Pivot" GameObject. PlayerCameraController stays
//   look-only (pitch / roll / root yaw). This component writes POSITION only.
//
//   Camera Pivot remains a child of the player root. Parenting it under the
//   head would inherit Mixamo head rotation; instead we sample CameraAnchor's
//   world position after the Animator has posed the skeleton and convert that
//   into the pivot parent's local space.
//
// MULTIPLAYER NOTE:
//   No IsOwner check needed - the entire Camera Pivot subtree is only
//   activated for the owner (see NetworkPlayerController).
// =============================================================================

using UnityEngine;

namespace OffAngle.Player
{
    public class CameraCrouchOffset : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Empty under mixamorig:Head. Camera Pivot copies this transform's world position, never its rotation.")]
        [SerializeField] private Transform _eyeAnchor;

        // LateUpdate so mixamorig:Head / CameraAnchor already reflect this
        // frame's Animator (and OnAnimatorIK) pose.
        private void LateUpdate()
        {
            if (_eyeAnchor == null || transform.parent == null)
                return;

            transform.localPosition = transform.parent.InverseTransformPoint(_eyeAnchor.position);
        }
    }
}
