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

        [Tooltip("Name of the Animator layer that has IK Pass enabled. Resolved to an index at runtime.")]
        [SerializeField] private string _ikLayerName = "Upper Body Aim";

        [Header("Target")]
        [Tooltip("Child name to find under the active third-person weapon.")]
        [SerializeField] private string _leftHandAttachName = "LeftHandAttach";

        [Header("Weights")]
        [SerializeField, Range(0f, 1f)] private float _positionWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _rotationWeight = 1f;
        [Tooltip("Master multiplier on position and rotation weights. 1 = current behavior. Fade to 0 later for reloads/actions.")]
        [SerializeField, Range(0f, 1f)] private float _ikWeight = 1f;

        [Header("Calibration")]
        [Tooltip("Euler offset applied to LeftHandAttach.rotation for Mixamo wrist alignment.")]
        [SerializeField] private Vector3 _rotationOffsetEuler = Vector3.zero;

        private int _ikLayerIndex = -1;
        private bool _ikLayerLookupFailed;
        private bool _bonesCached;
        private Transform _leftUpperArm;
        private Transform _leftLowerArm;
        private Transform _leftHand;
        private Transform _cachedAttach;
        private Transform _cachedWeaponRoot;

        private void Awake()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);

            if (_animator != null)
                _ikLayerIndex = _animator.GetLayerIndex(_ikLayerName);

            EnsureIkRelay();
        }

        /// <summary>
        /// Master IK weight in 0–1. Multiplies the existing position and rotation weights.
        /// Default 1 leaves the current look unchanged.
        /// </summary>
        public void SetIkWeight(float weight)
        {
            _ikWeight = Mathf.Clamp01(weight);
        }

        // Retried on the first IK callback because the Animator may not be
        // initialized yet in Awake. Warns once, then the IK-weight clear is skipped.
        private void EnsureIkLayer()
        {
            if (_ikLayerLookupFailed || _animator == null || !_animator.isInitialized)
                return;

            _ikLayerIndex = _animator.GetLayerIndex(_ikLayerName);
            if (_ikLayerIndex < 0)
            {
                _ikLayerLookupFailed = true;
                Debug.LogWarning($"[{nameof(ThirdPersonWeaponHandIk)}] Animator layer '{_ikLayerName}' not found.", this);
            }
        }

        private void OnAnimatorIK(int layerIndex)
        {
            ProcessAnimatorIK(layerIndex);
        }

        internal void ProcessAnimatorIK(int layerIndex)
        {
            if (_ikLayerIndex < 0)
                EnsureIkLayer();

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

            float posWeight = _positionWeight * _ikWeight;
            float rotWeight = _rotationWeight * _ikWeight;
            if (posWeight <= 0f && rotWeight <= 0f)
                return;

            EnsureArmBones();
            if (_leftUpperArm == null || _leftLowerArm == null || _leftHand == null)
                return;

            Transform attach = GetOrRefreshLeftHandAttach();
            if (attach == null)
                return;

            if (posWeight > 0f)
                SolveTwoBone(_leftUpperArm, _leftLowerArm, _leftHand, attach.position, posWeight);

            if (rotWeight > 0f)
            {
                Quaternion desired = attach.rotation * Quaternion.Euler(_rotationOffsetEuler);
                _leftHand.rotation = Quaternion.Slerp(_leftHand.rotation, desired, rotWeight);
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

        private void EnsureArmBones()
        {
            if (_bonesCached)
                return;
            if (_animator == null || !_animator.isInitialized || !_animator.isHuman)
                return;

            _leftUpperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _leftLowerArm = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _leftHand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _bonesCached = true;
        }

        private Transform GetOrRefreshLeftHandAttach()
        {
            Transform activeRoot = FindActiveHolderChild();
            if (activeRoot == null)
            {
                _cachedAttach = null;
                _cachedWeaponRoot = null;
                return null;
            }

            if (_cachedAttach != null
                && _cachedWeaponRoot == activeRoot
                && _cachedWeaponRoot.gameObject.activeInHierarchy)
                return _cachedAttach;

            _cachedWeaponRoot = activeRoot;
            _cachedAttach = string.IsNullOrEmpty(_leftHandAttachName)
                ? null
                : FindChildByName(activeRoot, _leftHandAttachName);
            return _cachedAttach;
        }

        private Transform FindActiveHolderChild()
        {
            if (_thirdPersonWeaponHolder == null)
                return null;

            for (int i = 0; i < _thirdPersonWeaponHolder.childCount; i++)
            {
                Transform weapon = _thirdPersonWeaponHolder.GetChild(i);
                if (weapon != null && weapon.gameObject.activeInHierarchy)
                    return weapon;
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
