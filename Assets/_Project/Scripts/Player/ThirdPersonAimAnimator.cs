// =============================================================================
// ThirdPersonAimAnimator — visual-only upper-body aim overlay.
//
// v1: correct Mixamo rifle/torso offset and apply camera pitch on spine/chest/
// head. Does NOT rotate the player root, camera, hips, or legs. Does not touch
// movement, ADS, weapons, networking, or MoveX/MoveY.
//
// POSE PIPELINE (must not accumulate):
//   1. Animator evaluates IdleAiming (Upper Body Aim) + locomotion legs.
//   2. OnAnimatorIK reads that animated local pose (Humanoid-safe).
//   3. This frame's clamped AimYaw/AimPitch is applied once via
//      SetBoneLocalRotation.
//   4. Next frame the Animator starts from the clip pose again.
//
// OnAnimatorIK only runs on the GameObject that owns the Animator. This script
// lives on the player root, so Awake attaches ThirdPersonAimIkRelay to the
// visual's Animator.
// =============================================================================

using UnityEngine;

namespace OffAngle.Player
{
    public class ThirdPersonAimAnimator : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Humanoid Animator on Character_Rigged_Visual. Leave null to auto-find.")]
        [SerializeField] private Animator _animator;

        [Tooltip("Read-only aim source. Uses CameraTransform.forward. Never written.")]
        [SerializeField] private PlayerCameraController _cameraController;

        [Tooltip("Optional. NetworkPlayerAim (or any IAimPitchOverride). Remotes use this instead of the inactive camera.")]
        [SerializeField] private MonoBehaviour _pitchOverrideBehaviour;

        [Tooltip("Character facing for AimYaw. Leave null to use this transform (player root).")]
        [SerializeField] private Transform _characterRoot;

        [Tooltip("Animator layer that has IK Pass enabled (Upper Body Aim is layer 1).")]
        [SerializeField] private int _ikLayerIndex = 1;

        [Header("Mixamo Yaw Calibration")]
        [Tooltip("Degrees added to AimYaw to cancel Mixamo's baked rifle offset. Start at 0; raise until the upper body lines up with camera forward.")]
        [SerializeField] private float _mixamoYawCalibration = 0f;

        [Header("Clamps (degrees)")]
        [SerializeField] private float _maxYaw = 50f;
        [SerializeField] private float _maxPitch = 50f;

        [Header("Bone Weights (should sum to ~1 for a full offset)")]
        [SerializeField, Range(0f, 1f)] private float _spineWeight = 0.25f;
        [SerializeField, Range(0f, 1f)] private float _chestWeight = 0.5f;
        [SerializeField, Range(0f, 1f)] private float _upperChestWeight = 0f;
        [SerializeField, Range(0f, 1f)] private float _headWeight = 0.25f;

        [Header("Axis Toggles")]
        [Tooltip("Flip if the torso yaws the wrong way after Mixamo retargeting.")]
        [SerializeField] private bool _invertYaw = false;
        [Tooltip("Flip if look-up pitches the torso down. Default off: +AimPitch looks up.")]
        [SerializeField] private bool _invertPitch = false;

        private static readonly int AimYawHash = Animator.StringToHash("AimYaw");
        private static readonly int AimPitchHash = Animator.StringToHash("AimPitch");

        private bool _bonesCached;
        private bool _hasAimYawParam;
        private bool _hasAimPitchParam;
        private bool _hasSpine;
        private bool _hasChest;
        private bool _hasUpperChest;
        private bool _hasHead;
        private IAimPitchOverride _pitchOverride;

        private void Awake()
        {
            if (_characterRoot == null)
                _characterRoot = transform;

            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);

            if (_cameraController == null)
                _cameraController = GetComponentInChildren<PlayerCameraController>(true);

            _pitchOverride = _pitchOverrideBehaviour as IAimPitchOverride;

            CacheBones();
            CacheAnimatorParameters();
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

            if (!_bonesCached)
                CacheBones();

            ComputeAimAngles(out float yaw, out float pitch);

            if (_hasAimYawParam)
                _animator.SetFloat(AimYawHash, yaw);
            if (_hasAimPitchParam)
                _animator.SetFloat(AimPitchHash, pitch);

            ApplyUpperBodyOffsets(yaw, pitch);
        }

        private void EnsureIkRelay()
        {
            if (_animator == null)
                return;
            if (_animator.gameObject == gameObject)
                return;

            ThirdPersonAimIkRelay relay = _animator.GetComponent<ThirdPersonAimIkRelay>();
            if (relay == null)
                relay = _animator.gameObject.AddComponent<ThirdPersonAimIkRelay>();
            relay.Bind(this);
        }

        // ------------------------------------------------------------------
        // Angle math — read-only. Never writes camera, root, or movement.
        // ------------------------------------------------------------------

        private void ComputeAimAngles(out float yaw, out float pitch)
        {
            if (_pitchOverride != null && _pitchOverride.TryGetPitch(out float networkedPitch))
                pitch = Mathf.Clamp(networkedPitch, -_maxPitch, _maxPitch);
            else
                pitch = ComputeLiveCameraPitch();

            yaw = ComputeYaw();
        }

        /// <summary>
        /// Sign: positive = looking up (aimFwd.y &gt; 0), negative = down.
        /// Shared with NetworkPlayerAim so owner writes match local visual pitch.
        /// </summary>
        public static float PitchFromAimForward(Vector3 aimFwd, float maxPitch)
        {
            float horizontal = new Vector2(aimFwd.x, aimFwd.z).magnitude;
            float pitch = Mathf.Atan2(aimFwd.y, horizontal) * Mathf.Rad2Deg;
            return Mathf.Clamp(pitch, -maxPitch, maxPitch);
        }

        private float ComputeLiveCameraPitch()
        {
            Transform aimSource = GetLiveAimSource();
            if (aimSource == null)
                return 0f;

            return PitchFromAimForward(aimSource.forward, _maxPitch);
        }

        private float ComputeYaw()
        {
            Vector3 bodyFwd = _characterRoot.forward;
            bodyFwd.y = 0f;

            Transform live = GetLiveAimSource();
            if (live == null)
                return Mathf.Clamp(_mixamoYawCalibration, -_maxYaw, _maxYaw);

            Vector3 aimPlanar = live.forward;
            aimPlanar.y = 0f;

            if (bodyFwd.sqrMagnitude < 0.0001f || aimPlanar.sqrMagnitude < 0.0001f)
                return Mathf.Clamp(_mixamoYawCalibration, -_maxYaw, _maxYaw);

            float yaw = Vector3.SignedAngle(bodyFwd.normalized, aimPlanar.normalized, Vector3.up);
            yaw += _mixamoYawCalibration;
            return Mathf.Clamp(yaw, -_maxYaw, _maxYaw);
        }

        private Transform GetLiveAimSource()
        {
            if (_cameraController == null)
                return null;

            Transform aim = _cameraController.CameraTransform;
            if (aim == null || !aim.gameObject.activeInHierarchy)
                return null;

            return aim;
        }

        // ------------------------------------------------------------------
        // Bone pose — SetBoneLocalRotation once per IK pass, parent-first.
        // ------------------------------------------------------------------

        private void ApplyUpperBodyOffsets(float yaw, float pitch)
        {
            ApplyBoneOffset(HumanBodyBones.Spine, _hasSpine, yaw * _spineWeight, pitch * _spineWeight);
            ApplyBoneOffset(HumanBodyBones.Chest, _hasChest, yaw * _chestWeight, pitch * _chestWeight);
            ApplyBoneOffset(HumanBodyBones.UpperChest, _hasUpperChest, yaw * _upperChestWeight, pitch * _upperChestWeight);
            ApplyBoneOffset(HumanBodyBones.Head, _hasHead, yaw * _headWeight, pitch * _headWeight);
        }

        private void ApplyBoneOffset(HumanBodyBones humanBone, bool mapped, float yawDeg, float pitchDeg)
        {
            if (!mapped)
                return;
            if (Mathf.Approximately(yawDeg, 0f) && Mathf.Approximately(pitchDeg, 0f))
                return;

            Transform bone = _animator.GetBoneTransform(humanBone);
            if (bone == null)
                return;

            Quaternion animatedLocal = bone.localRotation;
            Quaternion finalLocal = OffsetAnimatedLocal(bone, animatedLocal, yawDeg, pitchDeg);
            _animator.SetBoneLocalRotation(humanBone, finalLocal);
        }

        private Quaternion OffsetAnimatedLocal(Transform bone, Quaternion animatedLocal, float yawDeg, float pitchDeg)
        {
            float yaw = _invertYaw ? -yawDeg : yawDeg;
            float pitch = _invertPitch ? -pitchDeg : pitchDeg;

            Transform parent = bone.parent;
            Quaternion parentWorld = parent != null ? parent.rotation : Quaternion.identity;

            Vector3 pitchAxis = _characterRoot != null ? _characterRoot.right : Vector3.right;
            Quaternion worldOffset =
                Quaternion.AngleAxis(yaw, Vector3.up) *
                Quaternion.AngleAxis(-pitch, pitchAxis);

            Quaternion animatedWorld = parentWorld * animatedLocal;
            return Quaternion.Inverse(parentWorld) * (worldOffset * animatedWorld);
        }

        private void CacheBones()
        {
            _bonesCached = false;
            _hasSpine = false;
            _hasChest = false;
            _hasUpperChest = false;
            _hasHead = false;

            if (_animator == null)
                return;

            if (!_animator.isInitialized)
                return;
            if (!_animator.isHuman)
            {
                _bonesCached = true;
                return;
            }

            _hasSpine = _animator.GetBoneTransform(HumanBodyBones.Spine) != null;
            _hasChest = _animator.GetBoneTransform(HumanBodyBones.Chest) != null;
            _hasUpperChest = _animator.GetBoneTransform(HumanBodyBones.UpperChest) != null;
            _hasHead = _animator.GetBoneTransform(HumanBodyBones.Head) != null;
            _bonesCached = true;
        }

        private void CacheAnimatorParameters()
        {
            _hasAimYawParam = false;
            _hasAimPitchParam = false;
            if (_animator == null)
                return;

            AnimatorControllerParameter[] parameters = _animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                int hash = parameters[i].nameHash;
                if (hash == AimYawHash) _hasAimYawParam = true;
                else if (hash == AimPitchHash) _hasAimPitchParam = true;
            }
        }
    }

    /// <summary>
    /// Forwards OnAnimatorIK from the Animator GameObject to ThirdPersonAimAnimator
    /// on the player root. Added at runtime; do not put gameplay logic here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ThirdPersonAimIkRelay : MonoBehaviour
    {
        private ThirdPersonAimAnimator _target;

        public void Bind(ThirdPersonAimAnimator target)
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
