// =============================================================================
// LocalAffinitySelection — the LOCAL player's in-progress Affinity pick.
//
// ARCHITECTURE:
//   Plain MonoBehaviour, zero FishNet - the same "local UI state only" role
//   LoadoutManager plays for weapons, with one critical difference: this one is
//   DontDestroyOnLoad. LoadoutManager lives in the Game scene, but an affinity
//   is chosen in the AffinitySelect scene and that scene is unloaded by
//   ReplaceOption.All the moment the match starts. This object has to outlive
//   that, because the pick still has to be readable afterwards.
//
//   The selection UI reads and writes here. Nothing else does. Getting the
//   choice to the SERVER is AffinitySelectCoordinator's job (it encodes what
//   lives here and sends it), and the server's own copy is authoritative from
//   that point on - this class never becomes a source of truth for gameplay.
//
// RULE ENFORCEMENT:
//   Illegal picks are refused here too, not just server-side, so the UI cannot
//   put the player into a state the server would silently rewrite. Both sides
//   call the same AffinityLoadoutRules.
//
// CASCADING CLEARS:
//   Changing a primary or secondary affinity clears everything that hung off
//   it (its ultimate, its perks). Those references belong to the old affinity
//   and would otherwise be stale picks the server strips out later - better to
//   have the UI reflect that immediately.
//
// MANUAL SETUP:
//   Add this component to the persistent NetworkManager GameObject in the
//   Bootstrap scene, alongside GameFlowController / PlayerSpawner.
// =============================================================================

using System;
using UnityEngine;

namespace OffAngle.Affinities
{
    public class LocalAffinitySelection : MonoBehaviour
    {
        [Tooltip("Registry the selection UI browses. Also used to seed a default primary if DefaultAffinity is set and nothing has been picked.")]
        [SerializeField] private AffinityRegistry _registry;

        /// <summary>
        /// Persistent singleton, same convention as GameFlowController.Instance.
        /// Consumers should still null-check - it only exists from Bootstrap
        /// onward, and the Game scene can be entered directly when testing.
        /// </summary>
        public static LocalAffinitySelection Instance { get; private set; }

        private readonly AffinityLoadout _current = new AffinityLoadout();

        /// <summary>Registry the UI should browse. May be null if not wired.</summary>
        public AffinityRegistry Registry => _registry;

        /// <summary>Raised on any change. The UI re-reads Current and refreshes.</summary>
        public event Action SelectionChanged;

        /// <summary>True once every required slot is filled - gate the Submit button on this.</summary>
        public bool IsComplete => AffinityLoadoutRules.IsComplete(_current);

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(LocalAffinitySelection)}] Duplicate instance detected; keeping the first on '{Instance.name}'.", this);
                enabled = false;
                return;
            }

            Instance = this;
            _current.EnsureArraySizes();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------------
        // Read
        // ------------------------------------------------------------------

        /// <summary>
        /// A COPY of the current pick. Copied rather than exposed directly so a
        /// caller cannot bypass the rule checks below by mutating the arrays.
        /// </summary>
        public AffinityLoadout GetSelection() => _current.Clone();

        /// <summary>The perk currently taken from a row, or null. Pass asSecondary to read the secondary grid.</summary>
        public PerkDefinition GetPerk(int row, bool asSecondary)
        {
            if (row < 0 || row >= AffinityLoadoutRules.PerkRowCount) return null;
            return asSecondary ? _current.SecondaryPerks[row] : _current.PrimaryPerks[row];
        }

        public AffinityDefinition Primary => _current.Primary;
        public AffinityDefinition Secondary => _current.Secondary;
        public UltimateDefinition Ultimate => _current.Ultimate;

        // ------------------------------------------------------------------
        // Write
        // ------------------------------------------------------------------

        /// <summary>
        /// Sets the primary affinity, clearing its ultimate and perks. Also clears
        /// the secondary if it would now duplicate the primary.
        /// </summary>
        public void SetPrimary(AffinityDefinition affinity)
        {
            if (_current.Primary == affinity) return;

            _current.Primary = affinity;
            _current.Ultimate = null;
            ClearPerks(_current.PrimaryPerks);

            if (affinity != null && _current.Secondary == affinity)
            {
                _current.Secondary = null;
                ClearPerks(_current.SecondaryPerks);
            }

            SelectionChanged?.Invoke();
        }

        /// <summary>Sets the secondary affinity, clearing its perks. Refuses an affinity already taken as primary.</summary>
        public void SetSecondary(AffinityDefinition affinity)
        {
            if (affinity != null && affinity == _current.Primary)
            {
                Debug.LogWarning($"[{nameof(LocalAffinitySelection)}] '{affinity.name}' is already the primary affinity and cannot also be the secondary.", this);
                return;
            }

            if (_current.Secondary == affinity) return;

            _current.Secondary = affinity;
            ClearPerks(_current.SecondaryPerks);

            SelectionChanged?.Invoke();
        }

        /// <summary>Sets the ultimate. Refuses one that does not belong to the current primary.</summary>
        public void SetUltimate(UltimateDefinition ultimate)
        {
            if (ultimate != null && (_current.Primary == null || !_current.Primary.HasUltimate(ultimate)))
            {
                Debug.LogWarning($"[{nameof(LocalAffinitySelection)}] Ultimate '{ultimate.name}' does not belong to the selected primary affinity.", this);
                return;
            }

            if (_current.Ultimate == ultimate) return;

            _current.Ultimate = ultimate;
            SelectionChanged?.Invoke();
        }

        /// <summary>
        /// Takes a perk. The row is derived from the affinity's own grid rather than
        /// passed in, so the UI cannot file a perk under the wrong row. Passing null
        /// clears that row - which needs the row index, hence the overload below.
        /// </summary>
        public void SetPerk(PerkDefinition perk, bool asSecondary)
        {
            if (perk == null) return;

            AffinityDefinition affinity = asSecondary ? _current.Secondary : _current.Primary;

            if (!AffinityLoadoutRules.CanSelectPerk(affinity, perk, asSecondary))
            {
                Debug.LogWarning($"[{nameof(LocalAffinitySelection)}] Perk '{perk.name}' cannot be taken from '{(affinity != null ? affinity.name : "<none>")}' as {(asSecondary ? "secondary" : "primary")}.", this);
                return;
            }

            affinity.TryGetPerkRow(perk, out int row);

            PerkDefinition[] target = asSecondary ? _current.SecondaryPerks : _current.PrimaryPerks;
            if (target[row] == perk) return;

            target[row] = perk;
            SelectionChanged?.Invoke();
        }

        /// <summary>Clears whatever perk was taken from a row.</summary>
        public void ClearPerk(int row, bool asSecondary)
        {
            if (row < 0 || row >= AffinityLoadoutRules.PerkRowCount) return;

            PerkDefinition[] target = asSecondary ? _current.SecondaryPerks : _current.PrimaryPerks;
            if (target[row] == null) return;

            target[row] = null;
            SelectionChanged?.Invoke();
        }

        /// <summary>Resets to nothing picked. Called when returning to the menu between matches.</summary>
        public void Clear()
        {
            _current.Primary = null;
            _current.Secondary = null;
            _current.Ultimate = null;
            ClearPerks(_current.PrimaryPerks);
            ClearPerks(_current.SecondaryPerks);

            SelectionChanged?.Invoke();
        }

        private static void ClearPerks(PerkDefinition[] perks)
        {
            for (int i = 0; i < perks.Length; i++)
                perks[i] = null;
        }
    }
}
