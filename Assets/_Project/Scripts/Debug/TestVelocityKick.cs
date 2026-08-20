// =============================================================================
// TestVelocityKick — TEMPORARY. Attach next to a Rigidbody to give it a large
// constant velocity on start, purely to reproduce "fast-moving parent" in
// isolation while debugging the fireball particle issue. Delete when done.
// =============================================================================

using UnityEngine;

namespace OffAngle.Debugging
{
    [RequireComponent(typeof(Rigidbody))]
    public class TestVelocityKick : MonoBehaviour
    {
        [SerializeField] private Vector3 _velocity = new Vector3(40f, 0f, 0f);
        [SerializeField] private bool _useGravity = false;

        private void Start()
        {
            Rigidbody rb = GetComponent<Rigidbody>();
            rb.useGravity = _useGravity;
            rb.linearVelocity = _velocity;
        }
    }
}
