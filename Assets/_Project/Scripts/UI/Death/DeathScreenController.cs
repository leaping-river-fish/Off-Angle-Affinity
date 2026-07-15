// =============================================================================
// DeathScreenController — shows/hides the death panel; caches the latest
// DeathInfo for the widgets nested inside it.
//
// ARCHITECTURE:
//   Lives on "HUD Canvas" (always active for the owner once spawned, unlike
//   the "Death Screen" panel it controls, which starts inactive). Subscribes
//   directly to PlayerLifecycleController.OnLocalDied/OnLocalRespawned - the
//   same "gameplay fires events, UI subscribes" rule HealthHUD/ShieldHUD/
//   AmmoHUD already follow, no UIManager involved.
//
// WHY LastDeathInfo IS CACHED HERE RATHER THAN READ DIRECTLY FROM THE EVENT:
//   The "Death Screen" panel (and everything under it, e.g. RespawnCountdownUI
//   and KilledByUI) starts inactive, so their Awake/OnEnable/Start do not run
//   until this controller calls _panel.SetActive(true) in reaction to
//   OnLocalDied. That means those widgets cannot simply subscribe to
//   OnLocalDied themselves and expect to catch the firing that just happened.
//   Instead, this controller caches the payload BEFORE activating the panel,
//   and each widget reads LastDeathInfo from its own OnEnable, which fires
//   synchronously as part of the SetActive(true) call below - by then the
//   cached value is already current.
//
// EXTENSIBILITY:
//   Additional death-screen widgets (damage recap, assists, etc.) are just
//   more siblings under the same panel that read LastDeathInfo the same way -
//   no changes needed here or in PlayerLifecycleController.
// =============================================================================

using OffAngle.Combat;
using UnityEngine;

namespace OffAngle.UI.Death
{
    public class DeathScreenController : MonoBehaviour
    {
        [Tooltip("Leave null to auto-resolve via GetComponentInParent (the player root that owns the camera/HUD this lives under).")]
        [SerializeField] private PlayerLifecycleController _lifecycle;

        [Tooltip("The death panel this controller shows/hides. Should start INACTIVE in the prefab.")]
        [SerializeField] private GameObject _panel;

        /// <summary>The most recent death payload, cached for widgets under the panel to read from their own OnEnable.</summary>
        public DeathInfo LastDeathInfo { get; private set; }

        private void Start()
        {
            if (_lifecycle == null)
                _lifecycle = GetComponentInParent<PlayerLifecycleController>();

            if (_lifecycle == null)
            {
                Debug.LogWarning($"[{nameof(DeathScreenController)}] No PlayerLifecycleController assigned or found in parents for '{name}'.", this);
                return;
            }

            _lifecycle.OnLocalDied += HandleLocalDied;
            _lifecycle.OnLocalRespawned += HandleLocalRespawned;

            if (_panel != null)
                _panel.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_lifecycle != null)
            {
                _lifecycle.OnLocalDied -= HandleLocalDied;
                _lifecycle.OnLocalRespawned -= HandleLocalRespawned;
            }
        }

        private void HandleLocalDied(DeathInfo info)
        {
            LastDeathInfo = info;
            if (_panel != null)
                _panel.SetActive(true);
        }

        private void HandleLocalRespawned()
        {
            if (_panel != null)
                _panel.SetActive(false);
        }
    }
}
