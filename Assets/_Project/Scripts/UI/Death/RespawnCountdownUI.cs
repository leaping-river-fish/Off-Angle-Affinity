// =============================================================================
// RespawnCountdownUI — displays the seconds remaining until respawn.
//
// Reads its starting duration from DeathScreenController.LastDeathInfo on
// OnEnable (fired when the death panel activates) rather than subscribing to
// PlayerLifecycleController directly - see DeathScreenController's header for
// why. Ticks down locally using Time.time; sub-second drift against the
// server's actual respawn is not gameplay-relevant, only a countdown label.
// =============================================================================

using TMPro;
using UnityEngine;

namespace OffAngle.UI.Death
{
    public class RespawnCountdownUI : MonoBehaviour
    {
        [Tooltip("Leave null to auto-resolve via GetComponentInParent.")]
        [SerializeField] private DeathScreenController _deathScreen;
        [SerializeField] private TMP_Text _label;

        private float _respawnAt;

        private void OnEnable()
        {
            if (_deathScreen == null)
                _deathScreen = GetComponentInParent<DeathScreenController>();

            if (_deathScreen == null) return;

            _respawnAt = Time.time + _deathScreen.LastDeathInfo.RespawnDuration;
            UpdateLabel();
        }

        private void Update()
        {
            UpdateLabel();
        }

        private void UpdateLabel()
        {
            if (_label == null) return;

            float remaining = Mathf.Max(0f, _respawnAt - Time.time);
            _label.text = $"Respawning in {remaining:F1}s";
        }
    }
}
