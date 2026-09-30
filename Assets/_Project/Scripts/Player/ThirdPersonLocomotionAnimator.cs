using UnityEngine;
using OffAngle.Movement;
using OffAngle.Core;

namespace OffAngle.Player {
    public class ThirdPersonLocomotionAnimator : MonoBehaviour
    {
        [SerializeField] private Animator _animator;
        [SerializeField] private MovementStateMachine _movementStateMachine;
        [SerializeField] private PlayerInputReader _inputReader;

        [Tooltip("Horizontal speed (m/s) that maps to blend-tree length 1. Match sprint (~7).")]
        [SerializeField] private float _referenceSpeed = 7f;

        [Tooltip("Animator.SetFloat damp time. Higher = slower blend.")]
        [SerializeField] private float _dampTime = 0.12f;

        [Tooltip("Minimum horizontal speed (m/s) before IsSprinting can be true. Keeps the Sprint tree from showing while standing still with sprint held.")]
        [SerializeField] private float _sprintMinSpeed = 0.5f;

        private static readonly int MoveXHash = Animator.StringToHash("MoveX");
        private static readonly int MoveYHash = Animator.StringToHash("MoveY");
        private static readonly int IsSprintingHash = Animator.StringToHash("IsSprinting");

        private Vector3 _lastPosition;
        private bool _hasLastPosition;
        private bool _hasIsSprintingParam;
        private ISprintStateOverride _sprintOverride;

        private void Awake()
        {
            if (_inputReader == null) _inputReader = GetComponent<PlayerInputReader>();
            
            if (_movementStateMachine == null) _movementStateMachine = GetComponent<MovementStateMachine>();
            
            if(_animator == null) _animator = GetComponentInChildren<Animator>(true);

            _sprintOverride = GetComponent<ISprintStateOverride>();

            CacheAnimatorParameters();
        }

        private void LateUpdate() 
        {
            if (_animator == null || !_animator.enabled) return;

            bool simulated = IsSimulatedLocally();
            Vector3 worldVelocity = GetWorldVelocity(simulated);
            Vector3 local = transform.InverseTransformDirection(worldVelocity);
            Vector2 planar = new Vector2(local.x, local.z);

            float max = Mathf.Max(0.01f, _referenceSpeed);
            planar /= max;

            if (planar.sqrMagnitude > 1f) planar.Normalize();

            _animator.SetFloat(MoveXHash, planar.x, _dampTime, Time.deltaTime);
            _animator.SetFloat(MoveYHash, planar.y, _dampTime, Time.deltaTime);

            if (_hasIsSprintingParam)
            {
                bool moving = worldVelocity.sqrMagnitude > _sprintMinSpeed * _sprintMinSpeed;
                _animator.SetBool(IsSprintingHash, moving && ResolveSprintState(simulated));
            }
        }

        private bool ResolveSprintState(bool simulated)
        {
            if (simulated)
                return _movementStateMachine != null && _movementStateMachine.IsSprinting;

            if (_sprintOverride != null && _sprintOverride.TryGetSprinting(out bool networkedSprinting))
                return networkedSprinting;

            return false;
        }

        private bool IsSimulatedLocally()
        {
            return _movementStateMachine != null
                && _movementStateMachine.enabled
                && _inputReader != null
                && _inputReader.enabled;
        }

        private Vector3 GetWorldVelocity(bool useSimulatedVelocity)
        {
            if (useSimulatedVelocity)
            {
                Vector3 v = _movementStateMachine.Velocity;
                v.y = 0f;
                return v;
            }

            Vector3 position = transform.position;

            if (!_hasLastPosition)
            {
                _lastPosition = position;
                _hasLastPosition = true;
                return Vector3.zero;
            }

            Vector3 delta = position - _lastPosition;
            _lastPosition = position;
            delta.y = 0f;

            // Spawn/teleport: do not flash a huge one-frame "run".
            if (delta.sqrMagnitude > 25f) return Vector3.zero;

            return delta / Mathf.Max(Time.deltaTime, 0.0001f);
        }

        private void CacheAnimatorParameters()
        {
            _hasIsSprintingParam = false;
            if (_animator == null) return;

            AnimatorControllerParameter[] parameters = _animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].nameHash == IsSprintingHash && parameters[i].type == AnimatorControllerParameterType.Bool)
                {
                    _hasIsSprintingParam = true;
                    break;
                }
            }
        }
    }
}
