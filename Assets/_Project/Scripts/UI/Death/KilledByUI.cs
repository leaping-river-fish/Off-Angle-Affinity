// =============================================================================
// KilledByUI — displays "Killed by <attacker> using <weapon>".
//
// Reads DeathScreenController.LastDeathInfo on OnEnable - see
// DeathScreenController's header for why this widget does not subscribe to
// PlayerLifecycleController.OnLocalDied directly.
//
// Environmental deaths (null attacker, e.g. FallOffMapKill) skip the
// "Killed by" line and show a dedicated phrase instead.
//
// The attacker is labelled with LobbyPlayerList.LabelFor (Main Menu display
// name, or "Player {id}" if they never set one). Weapon text is the
// WeaponDefinition.DisplayName already resolved into DeathInfo.WeaponLabel
// on the server.
// =============================================================================

using OffAngle.Combat;
using OffAngle.Networking;
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
            _label.text = FormatLabel(info);
        }

        private static string FormatLabel(DeathInfo info)
        {
            if (info.Attacker == null)
            {
                if (info.WeaponLabel == FallOffMapKill.DeathWeaponLabel)
                    return "Fell off the map";

                return string.IsNullOrEmpty(info.WeaponLabel) ? "Died" : info.WeaponLabel;
            }

            return $"Killed by {ResolveAttackerLabel(info)} using {info.WeaponLabel}";
        }

        private static string ResolveAttackerLabel(DeathInfo info)
        {
            if (info.Attacker == null || info.Attacker.Owner == null)
                return "Unknown";

            return LobbyPlayerList.LabelFor(info.Attacker.Owner.ClientId);
        }
    }
}
