// =============================================================================
// MatchManager — server-authoritative "first to N kills" win condition.
//
// ARCHITECTURE:
//   Scene NetworkObject singleton living in the Game scene, same shape as
//   LobbyPlayerList: Instance/InstanceReady set in Awake (not OnStartServer -
//   this must be visible before any KillCount has scored). Subscribes to
//   KillCount.ServerKillScored (server-only) and, once some player's total
//   reaches _killsToWin, latches a single winner. Deliberately triggers NO
//   scene transition itself - it only flips a SyncVar so every client's UI
//   can react (see MatchEndUI). The actual return-to-lobby scene change only
//   happens later, from a manual button click, so players get a moment to
//   see the result rather than being yanked away the instant the last kill
//   lands.
//
// WHY THE WINNER IS A ClientId, NOT A NetworkObject:
//   A SyncVar<NetworkObject> would make HasEnded silently un-latch if the
//   winning player's object is ever despawned (Unity's overloaded == treats
//   a destroyed object reference as null) - e.g. during the later return-to-
//   lobby scene unload. An int sidesteps that entirely, and it's what the UI
//   needs anyway: LobbyPlayerList.LabelFor(int connectionId) already keys on
//   ClientId, matching that project-wide convention (see also CombatMemory's
//   "KEYED BY ObjectId, NOT BY REFERENCE" for the same underlying reasoning).
//   NoWinner is -1, not 0 - the host's own loopback client is ClientId 0.
//
// DOUBLE-FIRE GUARD:
//   FishNet's SyncVar.OnChange fires TWICE on the host for a value the host
//   itself wrote - once as the server-side write, once as the host's own
//   client-side receipt of it. Health/KillCount's UI-facing events get away
//   with this because setting a label twice is harmless; OnMatchEnded firing
//   twice would run MatchEndUI's show-and-build-tally logic twice. _endRaised
//   is a single latch here (not one per UI subscriber) that collapses both
//   the SyncVar callback and the OnStartClient late-bind seed into exactly
//   one broadcast.
// =============================================================================

using System;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Combat;
using UnityEngine;

namespace OffAngle.Networking
{
    public class MatchManager : NetworkBehaviour
    {
        private const int NoWinner = -1;

        [Tooltip("First player to reach this many kills wins the match.")]
        [SerializeField, Min(1)] private int _killsToWin = 30;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<int> _winnerClientId = new SyncVar<int>(NoWinner);

        private bool _endRaised;

        /// <summary>Scene singleton, set once the object initializes on every peer.</summary>
        public static MatchManager Instance { get; private set; }

        /// <summary>
        /// Fires right after Instance is set. FishNet activates scene
        /// NetworkObjects a beat later than plain scene MonoBehaviours, so a
        /// UI object that awakens/enables in the same scene load can't just
        /// check Instance once - it needs to hear about it if it's still
        /// null at that point.
        /// </summary>
        public static event Action InstanceReady;

        public int KillsToWin => _killsToWin;
        public int WinnerClientId => _winnerClientId.Value;
        public bool HasEnded => _winnerClientId.Value != NoWinner;

        /// <summary>True on the server/host peer. Lets UI host-gate the Return to Lobby button without importing FishNet.</summary>
        public bool LocalIsHost => IsServerInitialized;

        /// <summary>
        /// True once the match has ended AND the local peer is the declared
        /// winner. Lets MatchEndUI pick "Victory" vs "Defeat" without
        /// importing FishNet directly, matching this project's convention of
        /// keeping FishNet out of UI scripts (see NetworkMenuUI's header).
        /// </summary>
        public bool LocalPlayerWon
        {
            get
            {
                NetworkConnection localConnection = InstanceFinder.ClientManager != null ? InstanceFinder.ClientManager.Connection : null;
                return HasEnded && localConnection != null && localConnection.ClientId == _winnerClientId.Value;
            }
        }

        /// <summary>Fires on every peer exactly once, carrying the winner's ClientId, the moment the match ends.</summary>
        public event Action<int> OnMatchEnded;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            // Guards only the static pointer, not the whole component - same
            // reasoning LobbyPlayerList documents: this is a FishNet-driven
            // NetworkBehaviour whose real lifecycle runs through
            // OnStartServer/OnStartClient, not Unity's Awake/Start/enabled.
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(MatchManager)}] Duplicate instance detected; keeping the first.", this);
                return;
            }

            Instance = this;
            InstanceReady?.Invoke();

            _winnerClientId.OnChange += HandleWinnerChanged;
        }

        private void OnDestroy()
        {
            // Runs synchronously as part of FishNet's despawn broadcast on every
            // remaining peer - contain any exception here rather than letting it
            // escape into the network transport.
            try
            {
                _winnerClientId.OnChange -= HandleWinnerChanged;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            if (Instance == this)
                Instance = null;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            KillCount.ServerKillScored += HandleServerKillScored;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            try
            {
                KillCount.ServerKillScored -= HandleServerKillScored;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Covers a UI element that binds to this instance (via Instance/
            // InstanceReady) AFTER the winner was already set - SyncVar.OnChange
            // only fires on future writes, and RaiseEnded's own latch makes this
            // safe to call regardless of whether HandleWinnerChanged already ran.
            if (HasEnded)
                RaiseEnded();
        }

        // ------------------------------------------------------------------
        // Win check (server-only)
        // ------------------------------------------------------------------

        private void HandleServerKillScored(KillCount scorer, int kills)
        {
            if (!IsServerInitialized || HasEnded || kills < _killsToWin) return;

            NetworkConnection owner = scorer.Owner;
            if (owner == null || !owner.IsValid) return;

            _winnerClientId.Value = owner.ClientId;
        }

        // ------------------------------------------------------------------
        // Broadcast (every peer)
        // ------------------------------------------------------------------

        private void HandleWinnerChanged(int previous, int next, bool asServer)
        {
            RaiseEnded();
        }

        private void RaiseEnded()
        {
            if (_endRaised || !HasEnded) return;
            _endRaised = true;
            OnMatchEnded?.Invoke(_winnerClientId.Value);
        }
    }
}
