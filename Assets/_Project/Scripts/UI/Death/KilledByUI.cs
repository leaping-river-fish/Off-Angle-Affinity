// =============================================================================
// KilledByUI — displays "Killed by <attacker> (<weapon>)".
//
// Reads DeathScreenController.LastDeathInfo on OnEnable - see
// DeathScreenController's header for why this widget does not subscribe to
// PlayerLifecycleController.OnLocalDied directly.
//
// KNOWN LIMITATION: there is no player-name/identity system in the project
// yet, so the attacker is labelled with its NetworkObject's GameObject name.
// ResolveAttackerLabel is the single place to update once a real display-name
// system exists.
// =============================================================================

using OffAngle.Combat;
using TMPro;
using UnityEngine;

namespace OffAngle.UI.Death
{
    public class KilledByUI : MonoBehaviour
    {
        [Tooltip("Leave null to auto-resolve via GetComponentInParent.")]
        [SerializeField] private DeathScreenController _deathScreen;
        [SerializeField] private TMP_Text _label;

        private void OnEnable()
        {
            if (_deathScreen == null)
                _deathScreen = GetComponentInParent<DeathScreenController>();

            if (_deathScreen == null) return;
            if (_label == null) return;

            DeathInfo info = _deathScreen.LastDeathInfo;
            _label.text = $"Killed by {ResolveAttackerLabel(info)} ({info.WeaponLabel})";
        }

        private static string ResolveAttackerLabel(DeathInfo info)
        {
            return info.Attacker != null ? info.Attacker.name : "Unknown";
        }
    }
}
