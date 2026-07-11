// =============================================================================
// AmmoHUD — screen-space UI that mirrors the local player's ammo state.
//
// Purely cosmetic, exactly like WorldHealthBar. PlayerWeaponController's
// SyncVars do the actual multiplayer sync; this component just subscribes to
// the local OnAmmoChanged event and updates a text label. Lives under Camera
// Pivot, which NetworkPlayerController only activates for the owning client,
// so this HUD naturally never appears for remote players.
// =============================================================================
using OffAngle.Networking;
using TMPro;
using UnityEngine;

namespace OffAngle.UI.Combat
{
    public class AmmoHUD : MonoBehaviour
    {
        [SerializeField] private PlayerWeaponController _weaponController;
        [SerializeField] private TMP_Text _label;

        private void Start()
        {
            if (_weaponController == null)
                _weaponController = GetComponentInParent<PlayerWeaponController>();
            if (_weaponController == null)
            {
                Debug.LogWarning($"[{nameof(AmmoHUD)}] No PlayerWeaponController assigned or found in parents for '{name}'.", this);
                return;
            }
            _weaponController.OnAmmoChanged += HandleAmmoChanged;
            HandleAmmoChanged(_weaponController.MagazineAmmo, _weaponController.ReserveAmmo, _weaponController.IsReloading);
        }

        private void OnDestroy()
        {
            if (_weaponController != null)
                _weaponController.OnAmmoChanged -= HandleAmmoChanged;
        }

        private void HandleAmmoChanged(int magazine, int reserve, bool isReloading)
        {
            if (_label == null) return;
            
            _label.text = isReloading ? "RELOADING" : $"{magazine} / {reserve}";
        }
    }
}