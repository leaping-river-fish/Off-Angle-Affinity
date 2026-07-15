// =============================================================================
// PlayerRagdoll — makes the third-person capsule topple over on death.
//
// There is no skeleton/Animator on the player (Third Person Body/Capsule is a
// single primitive mesh + CapsuleCollider), so a full bone-based ragdoll does
// not apply. Instead this drives a single Rigidbody on that same capsule:
// kinematic while alive (so it never fights CharacterController), briefly
// dynamic on death so gravity + a small impulse tip it over, then reset back
// to its standing pose on respawn.
//
// NOT networked. PlayerLifecycleController tells every peer to
// Enter/ExitRagdoll at the same triggering moment (death/respawn), and each
// peer's physics engine simulates the fall locally from there. The fall is
// cosmetic only - a few frames of per-peer divergence while it settles is
// imperceptible, and syncing it tick-by-tick would cost bandwidth for no
// gameplay benefit.
// =============================================================================

using UnityEngine;

namespace OffAngle.Combat
{
    public class PlayerRagdoll : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Leave null to auto-resolve via GetComponent on this GameObject.")]
        [SerializeField] private Rigidbody _rigidbody;

        [Header("Fall")]
        [Tooltip("Random torque range (Nm) applied on death so the capsule doesn't always fall the same direction.")]
        [SerializeField] private float _minTorque = 2f;
        [SerializeField] private float _maxTorque = 5f;

        [Tooltip("Small forward push (m/s) applied on death, in addition to torque, so the fall reads as a collapse rather than a pure spin.")]
        [SerializeField] private float _fallPushSpeed = 0.5f;

        private Vector3    _standingLocalPosition;
        private Quaternion _standingLocalRotation;
        private bool       _isRagdolled;

        private void Awake()
        {
            if (_rigidbody == null)
                _rigidbody = GetComponent<Rigidbody>();

            _standingLocalPosition = transform.localPosition;
            _standingLocalRotation = transform.localRotation;

            if (_rigidbody != null)
                _rigidbody.isKinematic = true;
        }

        /// <summary>
        /// Makes the capsule dynamic and gives it a small push/torque so it
        /// falls over. Safe to call multiple times; a second call while already
        /// ragdolled just re-applies the impulse.
        /// </summary>
        public void EnterRagdoll()
        {
            if (_rigidbody == null) return;

            _isRagdolled = true;
            _rigidbody.isKinematic = false;
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;

            Vector3 randomTorque = Random.insideUnitSphere.normalized * Random.Range(_minTorque, _maxTorque);
            // Zero the vertical axis so the capsule topples sideways/forward rather than spinning like a top.
            randomTorque.y = 0f;
            _rigidbody.AddTorque(randomTorque, ForceMode.Impulse);
            _rigidbody.AddForce(transform.forward * _fallPushSpeed, ForceMode.VelocityChange);
        }

        /// <summary>
        /// Freezes physics and snaps the capsule back to its standing local
        /// pose. Called by PlayerLifecycleController on respawn.
        /// </summary>
        public void ExitRagdoll()
        {
            if (_rigidbody == null) return;

            _isRagdolled = false;
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
            _rigidbody.isKinematic = true;

            transform.localPosition = _standingLocalPosition;
            transform.localRotation = _standingLocalRotation;
        }

        public bool IsRagdolled => _isRagdolled;
    }
}
