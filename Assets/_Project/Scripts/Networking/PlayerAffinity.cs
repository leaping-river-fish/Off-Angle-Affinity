// =============================================================================
// PlayerAffinity — holds one player's validated Affinity loadout and owns the
// lifetime of every effect runtime it grants.
//
// AUTHORITY:
//   The server decides the loadout and publishes it; clients only read it.
//   The choice itself was made in the AffinitySelect scene and validated there
//   (see AffinitySelectCoordinator / AffinitySelectionService), so by the time
//   this component spawns the server ALREADY knows the answer - there is no
//   client round-trip on spawn and no window where a player exists without an
//   affinity.
//
// PLUMBING:
//   A single delimited SyncVar<string> of stable ids, the same idiom
//   PlayerWeaponEquipper uses for equipped weapons rather than a SyncList.
//   Ids resolve through one AffinityRegistry ASSET reference, deliberately not
//   the hand-maintained per-prefab array PlayerWeaponEquipper's own tooltip
//   warns about - an asset reference cannot drift out of sync between peers.
//
// SCOPE FILTERING (why this is not just "attach everything"):
//   This codebase splits authority hard - Shield and damage are server-only,
//   MovementStateMachine is DISABLED on non-owner players, VFX need every
//   peer. Each AffinityEffect declares where it belongs and only matching
//   peers build a runtime. On a listen host the same instance is both server
//   and owner, which is exactly the case that produces double-application
//   bugs; ApplyLoadout attaches each effect at most once regardless.
//
// SECONDARY AFFINITY:
//   Contributes its PERKS only. Its passive is deliberately skipped and it can
//   never contribute an ultimate. That rule lives here, in CollectEffects -
//   the assets themselves do not know which slot they were selected into.
//
// RE-ENTRANCY:
//   Apply is driven from several FishNet callbacks whose order differs between
//   host, dedicated server, and remote client. Rather than guess an ordering,
//   every entry point calls TryApplyFromSync, which no-ops unless the loadout
//   or this peer's authority flags have actually changed. That makes the
//   component correct on every topology without a single "which callback runs
//   first" assumption.
//
// MANUAL SETUP:
//   1. Add this component to the Player prefab root.
//   2. Assign the project's AffinityRegistry asset to _registry.
// =============================================================================

using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Affinities;
using OffAngle.Combat;
using OffAngle.Movement;
using OffAngle.Player;
using UnityEngine;

namespace OffAngle.Networking
{
    public class PlayerAffinity : NetworkBehaviour
    {
        [Header("Data")]
        [Tooltip("The project's AffinityRegistry. Required - ids received over the network resolve through this, so every peer must reference the same asset.")]
        [SerializeField] private AffinityRegistry _registry;

        [Header("References (leave null to auto-resolve on this GameObject)")]
        [SerializeField] private Health _health;
        [SerializeField] private Shield _shield;
        [SerializeField] private PlayerLifecycleController _lifecycle;
        [SerializeField] private MovementStateMachine _movement;
        [SerializeField] private PlayerGrapple _grapple;
        [SerializeField] private PlayerWeaponController _weapons;
        [SerializeField] private PlayerStats _stats;
        [SerializeField] private PlayerDamageModifiers _damageModifiers;
        [SerializeField] private CombatMemory _combatMemory;
        [SerializeField] private PlayerStatusEffects _statusEffects;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<string> _syncedLoadout = new SyncVar<string>();

        private readonly List<AffinityEffectRuntime> _runtimes = new List<AffinityEffectRuntime>();
        private readonly List<AffinityEffect> _effectBuffer = new List<AffinityEffect>();

        private AffinityRuntimeContext _context;
        private AffinityLoadout _loadout;

        // What the current runtimes were built from. Compared on every apply
        // attempt so repeated callbacks are free.
        private bool _hasApplied;
        private string _appliedEncoded;
        private bool _appliedIsServer;
        private bool _appliedIsOwner;

        private int _lastRespawnFrame = -1;

        // ------------------------------------------------------------------
        // Public read state
        // ------------------------------------------------------------------

        /// <summary>The applied loadout. Null until the first apply completes.</summary>
        public AffinityLoadout Loadout => _loadout;

        /// <summary>The selected ultimate, or null. PlayerUltimate reads this.</summary>
        public UltimateDefinition SelectedUltimate => _loadout?.Ultimate;

        /// <summary>Shared context handed to effects and to UltimateBehavior.ServerActivate.</summary>
        public AffinityRuntimeContext Context => _context;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_health == null) _health = GetComponent<Health>();
            if (_shield == null) _shield = GetComponent<Shield>();
            if (_lifecycle == null) _lifecycle = GetComponent<PlayerLifecycleController>();
            if (_movement == null) _movement = GetComponent<MovementStateMachine>();
            if (_grapple == null) _grapple = GetComponent<PlayerGrapple>();
            if (_weapons == null) _weapons = GetComponent<PlayerWeaponController>();
            if (_stats == null) _stats = GetComponent<PlayerStats>();
            if (_damageModifiers == null) _damageModifiers = GetComponent<PlayerDamageModifiers>();
            if (_combatMemory == null) _combatMemory = GetComponent<CombatMemory>();
            if (_statusEffects == null) _statusEffects = GetComponent<PlayerStatusEffects>();

            // Resolved once so no effect ever calls GetComponent.
            _context = new AffinityRuntimeContext
            {
                PlayerRoot = gameObject,
                Health = _health,
                Shield = _shield,
                Lifecycle = _lifecycle,
                Movement = _movement,
                Grapple = _grapple,
                Weapons = _weapons,
                Stats = _stats,
                DamageModifiers = _damageModifiers,
                CombatMemory = _combatMemory,
                StatusEffects = _statusEffects,
            };

            _syncedLoadout.OnChange += HandleSyncedLoadoutChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            _context.NetworkObject = base.NetworkObject;

            PublishLoadoutFromService();

            if (_lifecycle != null)
                _lifecycle.OnServerRespawned += HandleRespawn;

            TryApplyFromSync();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            try
            {
                if (_lifecycle != null)
                    _lifecycle.OnServerRespawned -= HandleRespawn;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            DetachAll();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            _context.NetworkObject = base.NetworkObject;

            if (IsOwner && _lifecycle != null)
                _lifecycle.OnLocalRespawned += HandleRespawn;

            // Seeded directly rather than waiting on OnChange: a SyncVar's initial
            // replicated value never raises OnChange, the same caveat Health,
            // Shield and PlayerLifecycleController all document.
            TryApplyFromSync();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            try
            {
                if (_lifecycle != null)
                    _lifecycle.OnLocalRespawned -= HandleRespawn;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            DetachAll();
        }

        // Ownership can be assigned after OnStartServer/OnStartClient have already
        // run, which would otherwise leave the owning peer without its Owner-scoped
        // effects. TryApplyFromSync notices the changed authority flags and rebuilds.
        public override void OnOwnershipServer(NetworkConnection prevOwner)
        {
            base.OnOwnershipServer(prevOwner);
            PublishLoadoutFromService();
            TryApplyFromSync();
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            TryApplyFromSync();
        }

        private void OnDestroy()
        {
            try
            {
                _syncedLoadout.OnChange -= HandleSyncedLoadoutChanged;

                if (_lifecycle != null)
                {
                    _lifecycle.OnServerRespawned -= HandleRespawn;
                    _lifecycle.OnLocalRespawned -= HandleRespawn;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            DetachAll();
        }

        private void Update()
        {
            if (_runtimes.Count == 0) return;

            float deltaTime = Time.deltaTime;

            // Count re-read each iteration: a runtime is permitted to end itself,
            // which mutates this list mid-tick.
            for (int i = 0; i < _runtimes.Count; i++)
                _runtimes[i]?.OnTick(_context, deltaTime);
        }

        // ------------------------------------------------------------------
        // Server: publish the validated choice
        // ------------------------------------------------------------------

        private void PublishLoadoutFromService()
        {
            if (!IsServerInitialized) return;
            if (_registry == null)
            {
                Debug.LogError($"[{nameof(PlayerAffinity)}] No AffinityRegistry assigned on '{name}'. This player will have no affinity.", this);
                return;
            }

            // Null for a player who never picked - a late joiner, or anyone who
            // launched straight into the Game scene while testing. Sanitize turns
            // that into the full default build rather than nothing.
            AffinityLoadout requested = AffinitySelectionService.Instance != null
                ? AffinitySelectionService.Instance.GetLoadoutFor(Owner)
                : null;

            AffinityLoadout sanitized = AffinityLoadoutRules.Sanitize(_registry, requested);
            string encoded = AffinityLoadoutCodec.Encode(sanitized);

            // Guarded because this runs from both OnStartServer and
            // OnOwnershipServer; republishing an identical value would dirty the
            // SyncVar for no reason.
            if (_syncedLoadout.Value == encoded) return;

            _syncedLoadout.Value = encoded;
        }

        private void HandleSyncedLoadoutChanged(string prev, string next, bool asServer) => TryApplyFromSync();

        // ------------------------------------------------------------------
        // Apply
        // ------------------------------------------------------------------

        private void TryApplyFromSync()
        {
            string encoded = _syncedLoadout.Value;
            bool isServer = IsServerInitialized;
            bool isOwner = IsOwner;

            if (_hasApplied &&
                _appliedEncoded == encoded &&
                _appliedIsServer == isServer &&
                _appliedIsOwner == isOwner)
            {
                return;
            }

            ApplyLoadout(encoded, isServer, isOwner);
        }

        private void ApplyLoadout(string encoded, bool isServer, bool isOwner)
        {
            DetachAll();

            _hasApplied = true;
            _appliedEncoded = encoded;
            _appliedIsServer = isServer;
            _appliedIsOwner = isOwner;

            if (_registry == null) return;
            if (!AffinityLoadoutCodec.TryDecode(encoded, _registry, out AffinityLoadout decoded)) return;

            _loadout = decoded;

            _context.NetworkObject = base.NetworkObject;
            _context.Loadout = decoded;
            _context.IsServer = isServer;
            _context.IsOwner = isOwner;

            CollectEffects(decoded, _effectBuffer);

            for (int i = 0; i < _effectBuffer.Count; i++)
            {
                AffinityEffect effect = _effectBuffer[i];
                if (effect == null) continue;
                if (!ScopeMatches(effect.Scope, isServer, isOwner)) continue;

                AffinityEffectRuntime runtime = effect.CreateRuntime();
                if (runtime == null) continue;

                _runtimes.Add(runtime);
                runtime.OnAttach(_context);
            }

            AffinityEvents.RaiseLoadoutApplied(base.NetworkObject, decoded);
        }

        private void DetachAll()
        {
            if (_runtimes.Count == 0) return;

            for (int i = 0; i < _runtimes.Count; i++)
            {
                // One misbehaving effect must not strand the rest still attached -
                // this also runs inside FishNet's despawn broadcast.
                try
                {
                    _runtimes[i]?.OnDetach(_context);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }

            _runtimes.Clear();
        }

        private void HandleRespawn()
        {
            // On a listen host the server event and the owner's respawn RPC can
            // both reach this. Same-frame arrivals are collapsed here; runtimes are
            // still required to make OnRespawn idempotent, since the two can land
            // in different frames.
            if (_lastRespawnFrame == Time.frameCount) return;
            _lastRespawnFrame = Time.frameCount;

            for (int i = 0; i < _runtimes.Count; i++)
            {
                try
                {
                    _runtimes[i]?.OnRespawn(_context);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }
        }

        // ------------------------------------------------------------------
        // Effect collection — the one place the primary/secondary rule lives
        // ------------------------------------------------------------------

        private static void CollectEffects(AffinityLoadout loadout, List<AffinityEffect> into)
        {
            into.Clear();
            if (loadout == null) return;

            loadout.EnsureArraySizes();

            // Primary passive. The SECONDARY's passive is deliberately never
            // collected - a secondary affinity contributes perks only.
            if (loadout.Primary != null && loadout.Primary.Passive != null)
                AddRange(into, loadout.Primary.Passive.Effects);

            for (int row = 0; row < AffinityLoadoutRules.PerkRowCount; row++)
            {
                PerkDefinition primaryPerk = loadout.PrimaryPerks[row];
                if (primaryPerk != null) AddRange(into, primaryPerk.Effects);

                PerkDefinition secondaryPerk = loadout.SecondaryPerks[row];
                if (secondaryPerk != null) AddRange(into, secondaryPerk.Effects);
            }
        }

        private static void AddRange(List<AffinityEffect> into, List<AffinityEffect> source)
        {
            if (source == null) return;

            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                    into.Add(source[i]);
            }
        }

        private static bool ScopeMatches(AffinityEffectScope scope, bool isServer, bool isOwner)
        {
            switch (scope)
            {
                case AffinityEffectScope.Server: return isServer;
                case AffinityEffectScope.Owner: return isOwner;
                case AffinityEffectScope.AllPeers: return true;
                default: return false;
            }
        }
    }
}
