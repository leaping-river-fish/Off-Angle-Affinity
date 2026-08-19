// =============================================================================
// PlayerWeaponEquipper — applies the local LoadoutManager selection onto the
// player's weapon holder, and hands the active Gun to PlayerWeaponController.
//
// ARCHITECTURE:
//   Runs on the owner (same IsOwner gating PlayerWeaponController already
//   uses) AND independently on the server (see OnStartServer) - see the
//   MULTIPLAYER NOTE below for why both are needed. Instantiates one Gun per
//   equipped category (Primary, Sidearm, ...) under a single weapon holder,
//   keeps the inactive one(s) disabled, and re-homes PlayerWeaponController's
//   active Gun reference via SetGun() whenever the active category changes or
//   LoadoutManager reports a new selection for any category. Reuses the
//   existing SwitchWeaponEvent to cycle which equipped category is active,
//   plus SelectWeaponCategoryEvent (Primary/Sidearm keys) for direct slot select.
//
//   Re-equipping on selection change is immediate - if the player changes
//   their loadout in the menu (e.g. while dead, waiting to respawn), it takes
//   effect right away rather than needing a separate "on respawn" hook. This
//   already satisfies "equip appropriately on spawn or respawn" because the
//   player object is never destroyed/recreated by a respawn - only reset.
//
// MULTIPLAYER NOTE:
//   The owner equips locally (for its own first-person rendering + input
//   gating) AND the server independently equips its own logic-only copy (see
//   OnStartServer) so PlayerWeaponController.SeedAmmoFromData always has a
//   non-null _gun/GunData to seed ammo from - previously this only happened
//   to work for the host (owner == server, same instance) and every other
//   connected/late-joining client spawned with 0 ammo for every weapon,
//   because the server's copy of their PlayerWeaponController never received
//   a Gun. Both equips read LoadoutManager, which is deliberately NOT
//   networked - its _defaults are scene-serialized data so they already
//   match identically on the server and every client, which is sufficient
//   for "starts with the correct default loadout."
//
// THIRD-PERSON VISUALS (all peers, including remote observers):
//   The owner's first-person equip above is purely local cosmetics/input and
//   was never visible to anyone else. To fix that, the owner also reports
//   its selection to the server (CmdSetCategorySelection/CmdSetActiveIndex),
//   which re-validates it against _allDefinitions and republishes it via
//   _syncedDefinitionIds/_syncedActiveIndex (plain delimited SyncVar<string>/
//   SyncVar<int>, same OnChange pattern PlayerWeaponController's ammo already
//   uses - avoids needing a SyncList). Every peer - owner, server, and every
//   remote observer alike - reacts to that synced state by instantiating a
//   purely-visual Gun copy under _thirdPersonWeaponHolder (see
//   RebuildThirdPersonFromSync), independent of the owner-only first-person
//   copy. The owner's own third-person copy is hidden from themselves via
//   PlayerVisibility, same as the rest of their third-person body.
// =============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
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
        [Tooltip("Transform the OWNER-ONLY first-person viewmodel instances are parented under, e.g. the player's First Person Weapon Holder.")]
        [SerializeField] private Transform _weaponHolder;
        [Tooltip("Optional. Transform every peer (including remote observers) parents the visual-only third-person Gun instance under, e.g. the player's Third Person Weapon Holder. Leave null to skip third-person visuals entirely.")]
        [SerializeField] private Transform _thirdPersonWeaponHolder;
        [Tooltip("Optional - required only for third-person visuals. Hides this player's own third-person weapon model from themselves, same as the rest of their third-person body. Leave null to skip.")]
        [SerializeField] private PlayerVisibility _playerVisibility;

        [Header("Categories")]
        [Tooltip("Cycle order for SwitchWeaponEvent (mouse scroll). Index 0 is active by default at spawn. Add a category here when you add a new one to the game.")]
        [SerializeField] private WeaponCategory[] _categoryCycleOrder;

        [Tooltip("Every WeaponDefinition that can ever be selected, across every category. Must be identical on every peer (server + every client) so a WeaponDefinition.Id received over the network resolves to the same asset everywhere - add a new entry here whenever a new WeaponDefinition asset is created. REQUIRED for every weapon regardless of third-person visuals: CmdSetCategorySelection resolves the id through this same array to authorize a non-host client's equip request, so a missing entry silently strands that client on the scene-default weapon for that category - it looks equipped locally but the server (and therefore every shot) uses the wrong GunData. Do not leave weapons out of this list.")]
        [SerializeField] private WeaponDefinition[] _allDefinitions;

        private readonly Dictionary<WeaponCategory, Gun> _equippedInstances = new();
        private readonly Dictionary<WeaponCategory, string> _equippedDefinitionIds = new();
        private int _activeIndex;

        // Guards against equipping twice on the same component instance. In
        // host mode OnStartServer and OnStartClient(IsOwner) both fire on the
        // exact same instance for the host's own player - without this,
        // that instance would instantiate (and immediately discard) two full
        // sets of Gun objects.
        private bool _hasEquippedFromLoadout;

        // Owner-only cosmetic override driven by PlayerLifecycleController on
        // death/respawn (see SetEquippedVisible). Kept separate from
        // ActiveCategory so "which weapon is active" bookkeeping (and the
        // SetGun call driving ammo/fire logic) is unaffected by whether the
        // model is currently allowed to render.
        private bool _visibleWhileEquipped = true;

        // ------------------------------------------------------------------
        // Third-person sync — server-authoritative, read by every peer.
        // ------------------------------------------------------------------

        private const char SyncedIdSeparator = '|';

        // FishNet requires SyncVar<T> fields to be readonly-initialized. Every
        // category's equipped WeaponDefinition.Id, joined by SyncedIdSeparator
        // in _categoryCycleOrder order (an empty segment means that category
        // has nothing equipped). A single delimited string reuses the exact
        // SyncVar<T>.OnChange pattern PlayerWeaponController's ammo already
        // uses, rather than introducing a SyncList<T> for what is at most a
        // couple of short strings.
        private readonly SyncVar<string> _syncedDefinitionIds = new SyncVar<string>();

        // Which _categoryCycleOrder index is the currently drawn/active
        // weapon, replicated so remote observers show the same weapon as
        // active that the owner currently has drawn.
        private readonly SyncVar<int> _syncedActiveIndex = new SyncVar<int>();

        // Server-authoritative override that force-hides the equipped weapon
        // model for EVERYONE - the owner's first-person view AND every
        // peer's third-person view alike (e.g. Solar Ascension hiding the
        // gun while ascended). Composes with _visibleWhileEquipped/
        // SetEquippedVisible via AND rather than replacing it, since that
        // flag is still driven independently by death/respawn.
        private readonly SyncVar<bool> _weaponHiddenOverride = new SyncVar<bool>();

        private readonly Dictionary<WeaponCategory, Gun> _thirdPersonInstances = new();

        private WeaponCategory ActiveCategory =>
            (_categoryCycleOrder != null && _activeIndex >= 0 && _activeIndex < _categoryCycleOrder.Length)
                ? _categoryCycleOrder[_activeIndex]
                : null;

        // ------------------------------------------------------------------
        // Lifecycle — owner-only, same convention as PlayerWeaponController.
        // ------------------------------------------------------------------

        private void Awake()
        {
            _syncedDefinitionIds.OnChange += HandleSyncedDefinitionIdsChanged;
            _syncedActiveIndex.OnChange += HandleSyncedActiveIndexChanged;
            _weaponHiddenOverride.OnChange += HandleWeaponHiddenOverrideChanged;
        }

        private void OnDestroy()
        {
            try
            {
                _syncedDefinitionIds.OnChange -= HandleSyncedDefinitionIdsChanged;
                _syncedActiveIndex.OnChange -= HandleSyncedActiveIndexChanged;
                _weaponHiddenOverride.OnChange -= HandleWeaponHiddenOverrideChanged;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        /// <summary>
        /// Server-only equip pass so PlayerWeaponController's server-side
        /// _gun (used to seed/validate ammo, see PlayerWeaponController.
        /// SeedAmmoFromData) is populated for EVERY player, not just the
        /// host's own. Runs for the host's own object too, but
        /// _hasEquippedFromLoadout ensures it only actually equips once
        /// regardless of whether this or the owner's OnStartClient fires
        /// first.
        /// </summary>
        public override void OnStartServer()
        {
            base.OnStartServer();
            TryEquipFromLoadout();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Every peer, not just the owner: SyncVar.OnChange only fires on
            // future writes, not the value a client already received as part
            // of initial spawn (same caveat PlayerLifecycleController
            // documents for _lifeState) - a late-joining/observing peer needs
            // to be seeded directly from the already-synced value.
            RebuildThirdPersonFromSync();

            if (!base.IsOwner) return;

            if (_inputReader != null)
            {
                _inputReader.SwitchWeaponEvent += HandleSwitchWeapon;
                _inputReader.SelectWeaponCategoryEvent += HandleSelectWeaponCategory;
            }

            // Owned by this callback, NOT by EquipAllFromLoadout. On a host,
            // OnStartServer runs first and calls TryEquipFromLoadout() while
            // IsOwner is still false, which latched _hasEquippedFromLoadout and
            // made EquipAllFromLoadout skip its owner-only subscribe. By the
            // time this ran with IsOwner true, TryEquipFromLoadout() returned
            // immediately on that flag - so the host never heard
            // SelectionChanged and loadout picks silently did nothing.
            // Clients were fine because they only ever run OnStartClient.
            SubscribeToLoadoutSelection();

            TryEquipFromLoadout();
        }

        // Waits for LoadoutManager the same way TryEquipFromLoadout does: it is
        // a scene singleton seeded in its own Awake, normally alive before any
        // player spawns, but nothing guarantees that for every peer/timing.
        private void SubscribeToLoadoutSelection()
        {
            if (_hasSubscribedToSelection) return;

            if (LoadoutManager.Instance != null)
            {
                _hasSubscribedToSelection = true;
                LoadoutManager.Instance.SelectionChanged += HandleSelectionChanged;
                return;
            }

            if (_pendingSelectionSubscribe == null)
                _pendingSelectionSubscribe = StartCoroutine(WaitForLoadoutManagerThenSubscribe());
        }

        private IEnumerator WaitForLoadoutManagerThenSubscribe()
        {
            while (LoadoutManager.Instance == null)
                yield return null;

            _pendingSelectionSubscribe = null;
            _hasSubscribedToSelection = true;
            LoadoutManager.Instance.SelectionChanged += HandleSelectionChanged;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!base.IsOwner) return;

            // Runs synchronously as part of FishNet's despawn broadcast - contain
            // any exception here rather than letting it escape into the network
            // transport.
            try
            {
                // The subscribe may still be pending if LoadoutManager never
                // appeared (e.g. despawned during the scene load that creates it).
                if (_pendingSelectionSubscribe != null)
                {
                    StopCoroutine(_pendingSelectionSubscribe);
                    _pendingSelectionSubscribe = null;
                }
                _hasSubscribedToSelection = false;

                if (LoadoutManager.Instance != null)
                    LoadoutManager.Instance.SelectionChanged -= HandleSelectionChanged;
                if (_inputReader != null)
                {
                    _inputReader.SwitchWeaponEvent -= HandleSwitchWeapon;
                    _inputReader.SelectWeaponCategoryEvent -= HandleSelectWeaponCategory;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        // ------------------------------------------------------------------
        // Loadout reactions
        // ------------------------------------------------------------------

        // Guards WaitForLoadoutManagerThenEquip against being started twice
        // (once from OnStartServer, once from OnStartClient's owner branch,
        // on the same host instance).
        private Coroutine _pendingLoadoutEquip;

        // Separate from _hasEquippedFromLoadout on purpose: the equip is a
        // once-per-object action that either peer role may perform, while this
        // subscription is owner-only. Sharing one flag is what broke the host.
        private bool _hasSubscribedToSelection;
        private Coroutine _pendingSelectionSubscribe;

        /// <summary>
        /// Equips from LoadoutManager.Instance if it's already available,
        /// otherwise waits for it. LoadoutManager is a scene singleton seeded
        /// in its own Awake() - normally already alive by the time a player
        /// spawns, but nothing actually guarantees that ordering is safe for
        /// every peer/timing combination. The previous single best-effort
        /// call (EquipAllFromLoadout() with no retry) silently gave up
        /// forever whenever LoadoutManager.Instance happened to be null at
        /// that exact moment, stranding that peer with no weapon equipped
        /// and no way to recover this session.
        /// </summary>
        private void TryEquipFromLoadout()
        {
            if (_hasEquippedFromLoadout) return;

            if (LoadoutManager.Instance != null)
            {
                EquipAllFromLoadout();
                return;
            }

            if (_pendingLoadoutEquip == null)
                _pendingLoadoutEquip = StartCoroutine(WaitForLoadoutManagerThenEquip());
        }

        private IEnumerator WaitForLoadoutManagerThenEquip()
        {
            while (LoadoutManager.Instance == null)
                yield return null;

            _pendingLoadoutEquip = null;
            EquipAllFromLoadout();
        }

        private void EquipAllFromLoadout()
        {
            if (_hasEquippedFromLoadout) return;
            if (LoadoutManager.Instance == null || _categoryCycleOrder == null) return;

            // Set before applying (not after): ApplyCategory ultimately calls
            // SetGun, and there is no reason for that round trip to re-enter
            // this method.
            _hasEquippedFromLoadout = true;

            // The SelectionChanged subscription deliberately does NOT live here
            // any more. This method also runs from OnStartServer, where IsOwner
            // is still false on a host, so the owner-only subscribe was skipped
            // and then never retried - see SubscribeToLoadoutSelection().

            for (int i = 0; i < _categoryCycleOrder.Length; i++)
            {
                WeaponCategory category = _categoryCycleOrder[i];
                if (category == null) continue;

                WeaponDefinition definition = LoadoutManager.Instance.GetSelected(category);
                ApplyCategory(category, definition);

                // The server's own pass through this method (via
                // OnStartServer) already seeded a default synced state
                // independently and must not request itself over the
                // network. Only a genuinely remote owner (not also the
                // server) needs to correct the server's default-only guess
                // with whatever it actually has selected locally (e.g. a
                // pre-game menu choice made before this object even spawned).
                if (base.IsOwner && !IsServerInitialized)
                    CmdSetCategorySelection(i, definition != null ? definition.Id : string.Empty);
            }
        }

        private void HandleSelectionChanged(WeaponCategory category, WeaponDefinition definition)
        {
            ApplyCategory(category, definition);

            int index = IndexOfCategory(category);
            if (index >= 0)
                CmdSetCategorySelection(index, definition != null ? definition.Id : string.Empty);
        }

        /// <summary>
        /// (Re)instantiates the Gun for one category from its WeaponDefinition,
        /// destroying whatever was equipped there before. Safe to call with a
        /// null definition to unequip that category. Called for the OWNER's
        /// own local (first-person) equip, and separately for the SERVER's
        /// own bookkeeping/ammo (see OnStartServer, CmdSetCategorySelection) -
        /// the latter is what keeps _equippedDefinitionIds (and therefore
        /// _syncedDefinitionIds) authoritative.
        /// </summary>
        private void ApplyCategory(WeaponCategory category, WeaponDefinition definition)
        {
            if (category == null) return;

            if (_equippedInstances.TryGetValue(category, out Gun existing) && existing != null)
            {
                _weaponController?.ForgetSavedAmmo(existing);
                Destroy(existing.gameObject);
            }
            _equippedInstances.Remove(category);

            if (definition != null && definition.WeaponPrefab != null && _weaponHolder != null)
            {
                Gun instance = Instantiate(definition.WeaponPrefab, _weaponHolder);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                _equippedInstances[category] = instance;
            }

            _equippedDefinitionIds[category] = definition != null ? definition.Id : string.Empty;
            if (IsServerInitialized)
                _syncedDefinitionIds.Value = EncodeSyncedDefinitionIds();

            RefreshActiveGun();
        }

        // ------------------------------------------------------------------
        // Active-category switching (scroll cycle + direct category keys)
        // ------------------------------------------------------------------

        private void HandleSwitchWeapon(float direction)
        {
            if (_categoryCycleOrder == null || _categoryCycleOrder.Length < 2) return;
            if (Mathf.Approximately(direction, 0f)) return;

            int step = direction > 0f ? 1 : -1;
            SetActiveIndex((_activeIndex + step + _categoryCycleOrder.Length) % _categoryCycleOrder.Length);
        }

        /// <summary>
        /// Direct slot select from input (e.g. Primary / Sidearm keys).
        /// Matches the action name to WeaponCategory.Id in _categoryCycleOrder.
        /// </summary>
        private void HandleSelectWeaponCategory(string categoryId)
        {
            if (string.IsNullOrEmpty(categoryId) || _categoryCycleOrder == null) return;

            for (int i = 0; i < _categoryCycleOrder.Length; i++)
            {
                WeaponCategory category = _categoryCycleOrder[i];
                if (category == null || category.Id != categoryId) continue;

                SetActiveIndex(i);
                return;
            }
        }

        private void SetActiveIndex(int index)
        {
            if (_categoryCycleOrder == null || index < 0 || index >= _categoryCycleOrder.Length) return;
            if (_activeIndex == index) return;

            _activeIndex = index;
            RefreshActiveGun();
            CmdSetActiveIndex(_activeIndex);
        }

        /// <summary>Shows the Gun for the active category, hides every other equipped Gun, and hands the active one to PlayerWeaponController.</summary>
        private void RefreshActiveGun()
        {
            Gun activeGun = null;

            foreach (KeyValuePair<WeaponCategory, Gun> pair in _equippedInstances)
            {
                if (pair.Value == null) continue;
                bool isActive = pair.Key == ActiveCategory;
                pair.Value.gameObject.SetActive(isActive && _visibleWhileEquipped && !_weaponHiddenOverride.Value);
                if (isActive) activeGun = pair.Value;
            }

            _weaponController?.SetGun(activeGun);
        }

        // ------------------------------------------------------------------
        // Death / respawn visibility — called by PlayerLifecycleController.
        // ------------------------------------------------------------------

        /// <summary>
        /// Owner-only. Shows/hides the currently equipped weapon model
        /// without touching ammo, fire-lock, or which category is active.
        /// Called by PlayerLifecycleController alongside SetFireLocked so the
        /// viewmodel doesn't stay floating in place (parented under the
        /// camera pivot, not the ragdolling body) after death - the camera
        /// swap alone never reaches this sibling object. Safe to call before
        /// any weapon has been equipped yet; the flag is applied retroactively
        /// by RefreshActiveGun once EquipAllFromLoadout runs.
        /// </summary>
        public void SetEquippedVisible(bool visible)
        {
            if (!base.IsOwner) return;
            if (_visibleWhileEquipped == visible) return;

            _visibleWhileEquipped = visible;
            RefreshActiveGun();
        }

        /// <summary>
        /// Server-only. Force-hides (or restores) the equipped weapon model
        /// for every peer - the owner's first-person view AND every
        /// observer's third-person view alike. Unlike SetEquippedVisible
        /// (owner-only, driven by death/respawn), this replicates. Composes
        /// with SetEquippedVisible via AND, not a replacement.
        /// </summary>
        public void ServerSetWeaponHiddenOverride(bool hidden)
        {
            if (!IsServerInitialized) return;
            _weaponHiddenOverride.Value = hidden;
        }

        private void HandleWeaponHiddenOverrideChanged(bool prev, bool next, bool asServer)
        {
            RebuildThirdPersonFromSync();
            if (base.IsOwner) RefreshActiveGun();
        }

        // ------------------------------------------------------------------
        // Server — receives and validates the owner's requested selection,
        // never trusts it blindly.
        // ------------------------------------------------------------------

        [ServerRpc]
        private void CmdSetCategorySelection(int categoryIndex, string definitionId)
        {
            if (_categoryCycleOrder == null || categoryIndex < 0 || categoryIndex >= _categoryCycleOrder.Length) return;

            WeaponCategory category = _categoryCycleOrder[categoryIndex];
            if (category == null) return;

            // Empty id explicitly means "unequip this category" - anything
            // else must resolve to a known definition that actually belongs
            // to this category. A client cannot equip an arbitrary/foreign
            // WeaponDefinition.Id this way.
            WeaponDefinition definition = null;
            if (!string.IsNullOrEmpty(definitionId))
            {
                definition = ResolveDefinitionById(definitionId);
                if (definition == null || definition.Category != category) return;
            }

            ApplyCategory(category, definition);
        }

        [ServerRpc]
        private void CmdSetActiveIndex(int index)
        {
            if (_categoryCycleOrder == null || index < 0 || index >= _categoryCycleOrder.Length) return;

            // Host already applied this locally in SetActiveIndex; still
            // re-apply so a dedicated-server copy of this player swaps its
            // authoritative Gun (and therefore its per-weapon ammo) to match.
            _activeIndex = index;
            _syncedActiveIndex.Value = index;
            RefreshActiveGun();
        }

        // ------------------------------------------------------------------
        // Third-person sync — reacts on every peer, owner and observers alike.
        // ------------------------------------------------------------------

        private int IndexOfCategory(WeaponCategory category)
        {
            if (_categoryCycleOrder == null) return -1;
            for (int i = 0; i < _categoryCycleOrder.Length; i++)
            {
                if (_categoryCycleOrder[i] == category) return i;
            }
            return -1;
        }

        private WeaponDefinition ResolveDefinitionById(string id)
        {
            if (string.IsNullOrEmpty(id) || _allDefinitions == null) return null;

            foreach (WeaponDefinition definition in _allDefinitions)
            {
                if (definition != null && definition.Id == id) return definition;
            }
            return null;
        }

        private string EncodeSyncedDefinitionIds()
        {
            if (_categoryCycleOrder == null || _categoryCycleOrder.Length == 0) return string.Empty;

            string[] ids = new string[_categoryCycleOrder.Length];
            for (int i = 0; i < _categoryCycleOrder.Length; i++)
            {
                WeaponCategory category = _categoryCycleOrder[i];
                ids[i] = category != null && _equippedDefinitionIds.TryGetValue(category, out string id) ? id : string.Empty;
            }
            return string.Join(SyncedIdSeparator.ToString(), ids);
        }

        private void HandleSyncedDefinitionIdsChanged(string prev, string next, bool asServer) => RebuildThirdPersonFromSync();
        private void HandleSyncedActiveIndexChanged(int prev, int next, bool asServer) => RebuildThirdPersonFromSync();

        /// <summary>
        /// Rebuilds every third-person visual Gun from the current synced
        /// state. Runs on every peer (owner, server, and every remote
        /// observer) - this is the only place _thirdPersonInstances is
        /// touched, deliberately separate from the owner-only
        /// _equippedInstances/RefreshActiveGun pair above so a remote
        /// observer, which never runs the owner-only equip path, still ends
        /// up with the correct visual. Always does a full rebuild rather than
        /// diffing - the category count is tiny (Primary/Sidearm today) and
        /// this only runs on an actual equip/switch event, never per-frame.
        /// </summary>
        private void RebuildThirdPersonFromSync()
        {
            if (_thirdPersonWeaponHolder == null || _categoryCycleOrder == null) return;

            string raw = _syncedDefinitionIds.Value;
            string[] ids = string.IsNullOrEmpty(raw) ? Array.Empty<string>() : raw.Split(SyncedIdSeparator);

            foreach (Gun instance in _thirdPersonInstances.Values)
            {
                if (instance == null) continue;
                _playerVisibility?.UnregisterDynamicRenderers(instance.GetComponentsInChildren<Renderer>(true));
                Destroy(instance.gameObject);
            }
            _thirdPersonInstances.Clear();

            for (int i = 0; i < _categoryCycleOrder.Length; i++)
            {
                WeaponCategory category = _categoryCycleOrder[i];
                if (category == null) continue;

                string id = i < ids.Length ? ids[i] : string.Empty;
                WeaponDefinition definition = ResolveDefinitionById(id);
                if (definition == null || definition.WeaponPrefab == null) continue;

                Gun instance = Instantiate(definition.WeaponPrefab, _thirdPersonWeaponHolder);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.gameObject.SetActive(i == _syncedActiveIndex.Value && !_weaponHiddenOverride.Value);

                _thirdPersonInstances[category] = instance;
                _playerVisibility?.RegisterDynamicRenderers(instance.GetComponentsInChildren<Renderer>(true));
            }
        }
    }
}
