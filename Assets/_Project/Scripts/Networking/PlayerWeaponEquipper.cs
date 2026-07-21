// =============================================================================
// PlayerWeaponEquipper — applies the local LoadoutManager selection onto the
// player's weapon holder, and hands the active Gun to PlayerWeaponController.
//
// ARCHITECTURE:
//   Owner-only, same IsOwner gating PlayerWeaponController already uses.
//   Instantiates one Gun per equipped category (Primary, Sidearm, ...) under
//   a single weapon holder, keeps the inactive one(s) disabled, and re-homes
//   PlayerWeaponController's active Gun reference via SetGun() whenever the
//   active category changes or LoadoutManager reports a new selection for
//   any category. Reuses the existing (previously unused) SwitchWeaponEvent
//   to cycle which equipped category is active.
//
//   Re-equipping on selection change is immediate - if the player changes
//   their loadout in the menu (e.g. while dead, waiting to respawn), it takes
//   effect right away rather than needing a separate "on respawn" hook. This
//   already satisfies "equip appropriately on spawn or respawn" because the
//   player object is never destroyed/recreated by a respawn - only reset.
//
// MULTIPLAYER NOTE:
//   Owner-only means this only runs (and only instantiates a Gun) on the
//   machine that owns the player. In Host-mode solo testing that is also the
//   server, so PlayerWeaponController's server-side validation happens to see
//   the correct Gun too. With a real dedicated server or other connected
//   clients, the server and remote observers will NOT see this player's
//   equipped weapon (their _gun stays null) until loadout selection is
//   replicated - that is the deferred networking step called out in the plan,
//   not implemented here.
// =============================================================================

using System.Collections.Generic;
using FishNet.Object;
using OffAngle.Core;
using OffAngle.Weapons;
using UnityEngine;

namespace OffAngle.Networking
{
    public class PlayerWeaponEquipper : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private PlayerWeaponController _weaponController;
        [SerializeField] private PlayerInputReader _inputReader;
        [Tooltip("Transform new weapon instances are parented under, e.g. the player's Third Person Weapon Holder.")]
        [SerializeField] private Transform _weaponHolder;

        [Header("Categories")]
        [Tooltip("Cycle order for SwitchWeaponEvent (mouse scroll). Index 0 is active by default at spawn. Add a category here when you add a new one to the game.")]
        [SerializeField] private WeaponCategory[] _categoryCycleOrder;

        private readonly Dictionary<WeaponCategory, Gun> _equippedInstances = new();
        private int _activeIndex;

        private WeaponCategory ActiveCategory =>
            (_categoryCycleOrder != null && _activeIndex >= 0 && _activeIndex < _categoryCycleOrder.Length)
                ? _categoryCycleOrder[_activeIndex]
                : null;

        // ------------------------------------------------------------------
        // Lifecycle — owner-only, same convention as PlayerWeaponController.
        // ------------------------------------------------------------------

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!base.IsOwner) return;

            if (LoadoutManager.Instance != null)
                LoadoutManager.Instance.SelectionChanged += HandleSelectionChanged;
            if (_inputReader != null)
                _inputReader.SwitchWeaponEvent += HandleSwitchWeapon;

            EquipAllFromLoadout();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!base.IsOwner) return;

            if (LoadoutManager.Instance != null)
                LoadoutManager.Instance.SelectionChanged -= HandleSelectionChanged;
            if (_inputReader != null)
                _inputReader.SwitchWeaponEvent -= HandleSwitchWeapon;
        }

        // ------------------------------------------------------------------
        // Loadout reactions
        // ------------------------------------------------------------------

        private void EquipAllFromLoadout()
        {
            if (LoadoutManager.Instance == null || _categoryCycleOrder == null) return;

            foreach (WeaponCategory category in _categoryCycleOrder)
            {
                if (category == null) continue;
                ApplyCategory(category, LoadoutManager.Instance.GetSelected(category));
            }
        }

        private void HandleSelectionChanged(WeaponCategory category, WeaponDefinition definition)
        {
            ApplyCategory(category, definition);
        }

        /// <summary>
        /// (Re)instantiates the Gun for one category from its WeaponDefinition,
        /// destroying whatever was equipped there before. Safe to call with a
        /// null definition to unequip that category.
        /// </summary>
        private void ApplyCategory(WeaponCategory category, WeaponDefinition definition)
        {
            if (category == null) return;

            if (_equippedInstances.TryGetValue(category, out Gun existing) && existing != null)
                Destroy(existing.gameObject);
            _equippedInstances.Remove(category);

            if (definition != null && definition.WeaponPrefab != null && _weaponHolder != null)
            {
                Gun instance = Instantiate(definition.WeaponPrefab, _weaponHolder);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                _equippedInstances[category] = instance;
            }

            RefreshActiveGun();
        }

        // ------------------------------------------------------------------
        // Active-category switching (SwitchWeaponEvent — mouse scroll today)
        // ------------------------------------------------------------------

        private void HandleSwitchWeapon(float direction)
        {
            if (_categoryCycleOrder == null || _categoryCycleOrder.Length < 2) return;
            if (Mathf.Approximately(direction, 0f)) return;

            int step = direction > 0f ? 1 : -1;
            _activeIndex = (_activeIndex + step + _categoryCycleOrder.Length) % _categoryCycleOrder.Length;
            RefreshActiveGun();
        }

        /// <summary>Shows the Gun for the active category, hides every other equipped Gun, and hands the active one to PlayerWeaponController.</summary>
        private void RefreshActiveGun()
        {
            Gun activeGun = null;

            foreach (KeyValuePair<WeaponCategory, Gun> pair in _equippedInstances)
            {
                if (pair.Value == null) continue;
                bool isActive = pair.Key == ActiveCategory;
                pair.Value.gameObject.SetActive(isActive);
                if (isActive) activeGun = pair.Value;
            }

            _weaponController?.SetGun(activeGun);
        }
    }
}
