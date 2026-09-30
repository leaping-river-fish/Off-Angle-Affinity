// =============================================================================
// ThirdPersonWeaponHandIk — visual-only left-hand IK onto the active
// third-person weapon's LeftHandAttach.
//
// Right hand remains the weapon anchor (WeaponSocket parent). This only
// pulls the left hand to each weapon's support-grip marker. No right-hand
// IK, first-person weapons, firing, ADS, or networking.
//
// TIMING:
//   ThirdPersonAimAnimator applies torso pitch in OnAnimatorIK via
//   SetBoneLocalRotation. Unity Humanoid SetIKPosition also snapshots in
//   that pass, so it would aim at the pre-pitch attach pose. This script
//   zeros LeftHand IK goals in OnAnimatorIK, then solves a two-bone arm
//   in LateUpdate after the Animator has written the pitched skeleton
//   (WeaponSocket / gun / LeftHandAttach already moved).
//
// OnAnimatorIK only runs on the GameObject that owns the Animator. This
// script lives on the player root, so Awake attaches ThirdPersonWeaponHandIkRelay
// to the visual's Animator.
// =============================================================================

using UnityEngine;

namespace OffAngle.Player
{
    public class ThirdPersonWeaponHandIk : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Humanoid Animator on the third-person character visual. Leave null to auto-find.")]
        [SerializeField] private Animator _animator;

        [Tooltip("WeaponSocket (third-person weapon holder). Active children are scanned for LeftHandAttach.")]
        [SerializeField] private Transform _thirdPersonWeaponHolder;

        [Tooltip("Animator layer that has IK Pass enabled (Upper Body Aim is layer 1).")]
        [SerializeField] private int _ikLayerIndex = 1;

        [Header("Target")]
        [Tooltip("Child name to find under the active third-person weapon.")]
        [SerializeField] private string _leftHandAttachName = "LeftHandAttach";

        [Header("Weights")]
        [SerializeField, Range(0f, 1f)] private float _positionWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _rotationWeight = 1f;

        [Header("Calibration")]
        [Tooltip("Euler offset applied to LeftHandAttach.rotation for Mixamo wrist alignment.")]
        [SerializeField] private Vector3 _rotationOffsetEuler = Vector3.zero;

        private void Awake()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);

            EnsureIkRelay();
        }

        private void OnAnimatorIK(int layerIndex)
        {
            ProcessAnimatorIK(layerIndex);
        }

        internal void ProcessAnimatorIK(int layerIndex)
        {
            if (layerIndex != _ikLayerIndex)
                return;
            if (_animator == null || !_animator.enabled || !_animator.isInitialized)
                return;

            // Do not use Humanoid SetIKPosition here — it samples LeftHandAttach
            // before torso aim writes, so pitch would pull the hand off the gun.
            ClearLeftHandIk();
        }

        private void LateUpdate()
        {
            if (_animator == null || !_animator.enabled || !_animator.isInitialized)
                return;
            if (!_animator.isHuman)
                return;
            if (_positionWeight <= 0f && _rotationWeight <= 0f)
                return;

            Transform attach = ResolveActiveLeftHandAttach();
            if (attach == null)
                return;

            Transform upper = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            Transform lower = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            Transform hand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            if (upper == null || lower == null || hand == null)
                return;

            if (_positionWeight > 0f)
                SolveTwoBone(upper, lower, hand, attach.position, _positionWeight);

            if (_rotationWeight > 0f)
            {
                Quaternion desired = attach.rotation * Quaternion.Euler(_rotationOffsetEuler);
                hand.rotation = Quaternion.Slerp(hand.rotation, desired, _rotationWeight);
            }
        }

        private static void SolveTwoBone(
            Transform upper, Transform lower, Transform hand, Vector3 target, float weight)
        {
            Quaternion upper0 = upper.rotation;
            Quaternion lower0 = lower.rotation;

            float upperLen = Vector3.Distance(upper.position, lower.position);
            float lowerLen = Vector3.Distance(lower.position, hand.position);
            if (upperLen < 0.0001f || lowerLen < 0.0001f)
                return;

            Vector3 root = upper.position;
            Vector3 toTarget = target - root;
            float maxReach = upperLen + lowerLen;
            float minReach = Mathf.Abs(upperLen - lowerLen);
            float dist = Mathf.Clamp(toTarget.magnitude, minReach + 0.001f, maxReach - 0.001f);
            Vector3 dir = toTarget.sqrMagnitude > 0.0000001f ? toTarget.normalized : upper.forward;
            Vector3 desiredHand = root + dir * dist;

            // Keep the animated elbow side as the pole so the arm does not flip.
            Vector3 pole = Vector3.Cross(dir, Vector3.Cross(lower.position - root, dir));
            if (pole.sqrMagnitude < 0.0000001f)
                pole = Vector3.Cross(dir, Vector3.up);
            pole.Normalize();

            float cosUpper = (upperLen * upperLen + dist * dist - lowerLen * lowerLen) / (2f * upperLen * dist);
            cosUpper = Mathf.Clamp(cosUpper, -1f, 1f);
            float sinUpper = Mathf.Sqrt(Mathf.Max(0f, 1f - cosUpper * cosUpper));
            Vector3 desiredElbow = root + dir * (cosUpper * upperLen) + pole * (sinUpper * upperLen);

            RotateJoint(upper, lower.position, desiredElbow);
            RotateJoint(lower, hand.position, desiredHand);

            upper.rotation = Quaternion.Slerp(upper0, upper.rotation, weight);
            lower.rotation = Quaternion.Slerp(lower0, lower.rotation, weight);
        }

        private static void RotateJoint(Transform joint, Vector3 currentEnd, Vector3 desiredEnd)
        {
            Vector3 from = currentEnd - joint.position;
            Vector3 to = desiredEnd - joint.position;
            if (from.sqrMagnitude < 0.0000001f || to.sqrMagnitude < 0.0000001f)
                return;

            joint.rotation = Quaternion.FromToRotation(from, to) * joint.rotation;
        }

        private Transform ResolveActiveLeftHandAttach()
        {
            if (_thirdPersonWeaponHolder == null || string.IsNullOrEmpty(_leftHandAttachName))
                return null;

            for (int i = 0; i < _thirdPersonWeaponHolder.childCount; i++)
            {
                Transform weapon = _thirdPersonWeaponHolder.GetChild(i);
                if (weapon == null || !weapon.gameObject.activeInHierarchy)
                    continue;

                Transform attach = FindChildByName(weapon, _leftHandAttachName);
                if (attach != null)
                    return attach;
            }

            return null;
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root.name == name)
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildByName(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private void ClearLeftHandIk()
        {
            _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
            _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
        }

        private void EnsureIkRelay()
        {
            if (_animator == null)
                return;
            if (_animator.gameObject == gameObject)
                return;

            ThirdPersonWeaponHandIkRelay relay = _animator.GetComponent<ThirdPersonWeaponHandIkRelay>();
            if (relay == null)
                relay = _animator.gameObject.AddComponent<ThirdPersonWeaponHandIkRelay>();
            relay.Bind(this);
        }
    }

    /// <summary>
    /// Forwards OnAnimatorIK from the Animator GameObject to ThirdPersonWeaponHandIk
    /// on the player root. Added at runtime; do not put gameplay logic here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ThirdPersonWeaponHandIkRelay : MonoBehaviour
    {
        private ThirdPersonWeaponHandIk _target;

        public void Bind(ThirdPersonWeaponHandIk target)
        {
            _target = target;
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (_target != null)
                _target.ProcessAnimatorIK(layerIndex);
        }
    }
}
