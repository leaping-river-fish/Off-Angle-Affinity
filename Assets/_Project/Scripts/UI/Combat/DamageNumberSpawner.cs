// =============================================================================
// DamageNumberSpawner — scene singleton that pops FloatingDamageNumber instances.
//
// Subscribes to the static Health.DamageFeedback event so gameplay code
// (Combat namespace) doesn't need to reference the UI namespace. Every peer's
// Health.RpcOnDamaged fires the event locally; this spawner reacts on every
// peer to give each viewer their own local floating text.
//
// Setup: drop one empty GameObject with this component into the scene and
// assign the FloatingDamageNumber prefab.
// =============================================================================

using OffAngle.Combat;
using UnityEngine;

namespace OffAngle.UI.Combat
{
    public class DamageNumberSpawner : MonoBehaviour
    {
        [SerializeField] private FloatingDamageNumber _prefab;

        private static DamageNumberSpawner _instance;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"[{nameof(DamageNumberSpawner)}] Multiple instances found. Destroying duplicate on '{name}'.", this);
                Destroy(this);
                return;
            }
            _instance = this;
        }

        private void OnEnable()
        {
            Health.DamageFeedback += HandleDamageFeedback;
        }

        private void OnDisable()
        {
            Health.DamageFeedback -= HandleDamageFeedback;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        // ------------------------------------------------------------------
        // Event handler
        // ------------------------------------------------------------------

        private void HandleDamageFeedback(Vector3 position, float amount, AffinityType affinity)
        {
            if (_prefab == null) return;

            FloatingDamageNumber n = Instantiate(_prefab, position, Quaternion.identity);
            n.Initialize(amount, affinity);
        }
    }
}
