// =============================================================================
// KillCount — networked per-player kill tally for the "first to N kills" win
// condition. Sibling of Health/Shield/CombatMemory/PlayerLifecycleController
// on the Player prefab.
//
// ATTRIBUTION:
//   Subscribes to PlayerLifecycleController.AnyPlayerDied (server-side only,
//   via OnStartServer) - the same broadcast KilledByUI already reads for its
//   "Killed by X" label. Every spawned player's KillCount hears every death
//   and self-filters by comparing DeathInfo.Attacker against its own
//   NetworkObject, the same "no central router, everyone subscribes and
//   filters" idiom CombatMemory already uses for its own relationship
//   tracking. A death with no attacker (a hypothetical future environmental
//   hazard) or a self-inflicted one awards nobody a kill - matching
//   CombatMemory's own exclusion rule.
//
// WHY THIS RELIES ON AnyPlayerDied RATHER THAN Health.OnServerDied DIRECTLY:
//   AnyPlayerDied is already documented as the purpose-built hook for
//   "future kill feed / spectator systems" and fires exactly once per death
//   in this project's host-only topology (there is no dedicated-server mode -
//   NetworkMenuController only offers StartHost/StartClient - so the host's
//   own execution of the ObserversRpc that raises it IS the one server-side
//   firing). If a dedicated-server mode is ever added, re-verify this still
//   fires server-side before relying on it further.
//
// STATIC BROADCAST FOR MatchManager:
//   ServerKillScored is a second, server-only static event - same split
//   Health uses between OnHealthChanged (per-instance) and ServerDamageApplied
//   (static, for other gameplay systems). Keeps this file free of any
//   reference to MatchManager (Combat should not depend on Networking), and
//   means a null/not-yet-loaded MatchManager can never silently swallow a
//   winning kill the way a direct MatchManager.Instance?.Foo() call could.
//
// NOT CLEARED ON RESPAWN:
//   Deliberately does NOT subscribe to PlayerLifecycleController's respawn
//   events. Kills persist for the whole match, unlike Health/Shield/
//   CombatMemory, which all reset per life - only copy those components'
//   respawn-clear subscription if that is genuinely wanted here too.
// =============================================================================

using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace OffAngle.Combat
{
    public class KillCount : NetworkBehaviour
    {
        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<int> _kills = new SyncVar<int>();

        public int Kills => _kills.Value;

        /// <summary>Fires on every peer whenever this player's kill count changes.</summary>
        public event Action<int> OnKillsChanged;

        /// <summary>
        /// Server-only. Fires right after a kill is credited, carrying the
        /// scoring player's own KillCount and its new total. MatchManager
        /// subscribes to check the win threshold.
        /// </summary>
        public static event Action<KillCount, int> ServerKillScored;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            _kills.OnChange += HandleKillsChanged;
        }

        private void OnDestroy()
        {
            // Runs synchronously as part of FishNet's despawn broadcast on every
            // remaining peer - contain any exception here rather than letting it
            // escape into the network transport. Two independent subscriptions,
            // each guarded on its own so a failure in one still lets the other
            // detach.
            try
            {
                _kills.OnChange -= HandleKillsChanged;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            try
            {
                PlayerLifecycleController.AnyPlayerDied -= HandleAnyPlayerDied;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            PlayerLifecycleController.AnyPlayerDied += HandleAnyPlayerDied;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            try
            {
                PlayerLifecycleController.AnyPlayerDied -= HandleAnyPlayerDied;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Seed subscribers with the current value; SyncVar.OnChange only fires on future writes.
            OnKillsChanged?.Invoke(_kills.Value);
        }

        // ------------------------------------------------------------------
        // Attribution (server-only)
        // ------------------------------------------------------------------

        private void HandleAnyPlayerDied(DeathInfo info)
        {
            if (!IsServerInitialized) return;

            NetworkObject self = base.NetworkObject;
            if (self == null) return;

            // No credit for an unattributed (hypothetical future environmental)
            // death, or a self-inflicted one - same exclusion CombatMemory
            // applies to its own relationship tracking.
            if (info.Attacker == null || info.Attacker == info.Victim) return;
            if (info.Attacker != self) return;

            _kills.Value++;
            ServerKillScored?.Invoke(this, _kills.Value);
        }

        // ------------------------------------------------------------------
        // SyncVar change handler — routes to the local OnKillsChanged event
        // ------------------------------------------------------------------

        private void HandleKillsChanged(int prev, int next, bool asServer)
        {
            OnKillsChanged?.Invoke(next);
        }
    }
}
