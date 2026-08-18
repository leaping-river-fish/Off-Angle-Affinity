// =============================================================================
// AffinitySelectCoordinator — the networked surface of the AffinitySelect
// scene: submission, the ready roster, and the countdown that starts the match.
//
// ARCHITECTURE:
//   A scene NetworkObject, built the same way as LobbyPlayerList - it exists on
//   every peer from scene load and FishNet syncs it automatically, with no
//   manual Spawn() call. It collects submissions and hands them to
//   AffinitySelectionService, which is where they actually live: this object
//   is destroyed the moment the Game scene replaces AffinitySelect, and the
//   picks have to outlive it.
//
// AUTHORITY:
//   Clients propose, the server disposes. CmdSubmitLoadout carries only ids;
//   the server decodes them against its own registry and runs
//   AffinityLoadoutRules.Sanitize before storing anything, so a modified client
//   cannot grant itself a perk from a row it may not use, an ultimate from
//   another affinity, or the same affinity twice.
//
//   RequireOwnership = false because a scene NetworkObject has no owner;
//   FishNet fills in the sending connection.
//
// COUNTDOWN BANDWIDTH:
//   _secondsRemaining is written at most once per second, not per frame. That
//   is one small message a second for the length of the selection phase, and
//   it avoids needing a synchronized clock for what is a display value.
//
// UNREADY PLAYERS:
//   By default anyone who has not submitted when the countdown expires is
//   auto-filled with the default build so nobody is stranded. Turning that off
//   gives the stricter "not spawned until ready" behaviour - the service and
//   PlayerSpawner fully support a player readying up mid-match - but it
//   requires the selection UI to be reachable from the Game scene, otherwise an
//   unready player has no way to ever pick. See the tooltip.
//
// MANUAL SETUP:
//   1. Create the AffinitySelect scene and add it to Build Settings, between
//      Lobby and Game.
//   2. Add an empty GameObject (e.g. "Affinity Select State") SAVED IN THAT
//      SCENE - it must be part of the scene so FishNet bakes it a scene ID.
//   3. Add a NetworkObject component, then this component.
//   4. Assign the project's AffinityRegistry.
//   5. Point the selection UI at CmdSubmitLoadout / SecondsRemaining /
//      ReadyConnectionIds.
// =============================================================================

using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Affinities;
using UnityEngine;

namespace OffAngle.Networking
{
    public class AffinitySelectCoordinator : NetworkBehaviour
    {
        [Header("Data")]
        [Tooltip("The project's AffinityRegistry. Required - submitted ids are resolved and validated against this on the server.")]
        [SerializeField] private AffinityRegistry _registry;

        [Header("Countdown")]
        [Tooltip("Seconds players have to choose before the match starts automatically.")]
        [SerializeField, Min(5f)] private float _selectionSeconds = 90f;

        [Tooltip("Start immediately once every connected player has submitted, rather than waiting out the clock.")]
        [SerializeField] private bool _startEarlyWhenAllReady = true;

        [Tooltip("ON: anyone who hasn't submitted when the clock runs out is given the default build, so nobody is stranded. OFF: they are simply not spawned until they submit - which needs the selection UI to be reachable from the Game scene, or they can never pick at all.")]
        [SerializeField] private bool _autoReadyUnpickedOnExpiry = true;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<int> _secondsRemaining = new SyncVar<int>();

        /// <summary>ClientIds that have submitted. UI binds to OnChange for a live roster; reuse LobbyPlayerList.LabelFor for names.</summary>
        public readonly SyncList<int> ReadyConnectionIds = new SyncList<int>();

        /// <summary>Whole seconds left before the match starts automatically.</summary>
        public int SecondsRemaining => _secondsRemaining.Value;

        /// <summary>Scene singleton, set once the object initializes on every peer.</summary>
        public static AffinitySelectCoordinator Instance { get; private set; }

        /// <summary>
        /// Fires right after Instance is set. FishNet activates scene
        /// NetworkObjects a beat later than plain scene MonoBehaviours, so a UI
        /// object enabling in the same scene load cannot just check Instance once.
        /// Same caveat LobbyPlayerList documents.
        /// </summary>
        public static event Action InstanceReady;

        private readonly List<NetworkConnection> _unreadyBuffer = new List<NetworkConnection>();

        private float _deadline;
        private bool _countdownRunning;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            Instance = this;
            InstanceReady?.Invoke();
        }

        private void OnDestroy()
        {
            // Also here, not just OnStopServer. This object lives only in the
            // AffinitySelect scene, and starting the match loads Game with
            // ReplaceOption.All - which unloads that scene and destroys this. If
            // OnStopServer does not run during that unload, the service delegate
            // below keeps pointing at a destroyed object for the whole match.
            // Unsubscribing twice is harmless; not unsubscribing is not. Exactly
            // the trap LobbyPlayerList documents.
            if (AffinitySelectionService.Instance != null)
                AffinitySelectionService.Instance.ServerReadyStateChanged -= HandleReadyStateChanged;

            if (Instance == this)
                Instance = null;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (AffinitySelectionService.Instance != null)
                AffinitySelectionService.Instance.ServerReadyStateChanged += HandleReadyStateChanged;

            RebuildReadyList();

            _deadline = Time.time + _selectionSeconds;
            _countdownRunning = true;
            _secondsRemaining.Value = Mathf.CeilToInt(_selectionSeconds);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            try
            {
                if (AffinitySelectionService.Instance != null)
                    AffinitySelectionService.Instance.ServerReadyStateChanged -= HandleReadyStateChanged;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            _countdownRunning = false;
            ReadyConnectionIds.Clear();
        }

        private void Update()
        {
            if (!_countdownRunning) return;
            if (!IsServerInitialized) return;

            int remaining = Mathf.Max(0, Mathf.CeilToInt(_deadline - Time.time));

            // Written only when the whole-second value actually changes - one small
            // message per second rather than one per frame.
            if (remaining != _secondsRemaining.Value)
                _secondsRemaining.Value = remaining;

            if (remaining <= 0)
            {
                BeginMatch(autoReadyStragglers: _autoReadyUnpickedOnExpiry);
                return;
            }

            if (_startEarlyWhenAllReady &&
                AffinitySelectionService.Instance != null &&
                AffinitySelectionService.Instance.AreAllConnectedReady())
            {
                // Nobody to auto-ready: everyone submitted, which is why we are here.
                BeginMatch(autoReadyStragglers: false);
            }
        }

        // ------------------------------------------------------------------
        // Client -> server
        // ------------------------------------------------------------------

        /// <summary>
        /// Submits this client's pick and marks it ready. Encode with
        /// AffinityLoadoutCodec.Encode(LocalAffinitySelection.Instance.GetSelection()).
        /// The server validates before storing - whatever is sent here, what gets
        /// stored is always a legal, playable build.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void CmdSubmitLoadout(string encoded, NetworkConnection sender = null)
        {
            if (sender == null) return;

            if (_registry == null)
            {
                Debug.LogError($"[{nameof(AffinitySelectCoordinator)}] No AffinityRegistry assigned on '{name}'. Submissions cannot be validated.", this);
                return;
            }

            if (AffinitySelectionService.Instance == null)
            {
                Debug.LogError($"[{nameof(AffinitySelectCoordinator)}] No {nameof(AffinitySelectionService)} in the session - add one to the NetworkManager GameObject. Submissions cannot be stored.", this);
                return;
            }

            // Decode is lenient by design; Sanitize is the gate. Between them, an
            // arbitrary string always becomes a legal build rather than an error.
            AffinityLoadoutCodec.TryDecode(encoded, _registry, out AffinityLoadout decoded);
            AffinityLoadout sanitized = AffinityLoadoutRules.Sanitize(_registry, decoded);

            AffinitySelectionService.Instance.ServerSetLoadout(sender, sanitized);
        }

        /// <summary>Withdraws readiness, keeping the pick. Lets a player re-open their build before the clock runs out.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void CmdClearReady(NetworkConnection sender = null)
        {
            if (sender == null) return;
            AffinitySelectionService.Instance?.ServerClearReady(sender);
        }

        // ------------------------------------------------------------------
        // Server internals
        // ------------------------------------------------------------------

        private void HandleReadyStateChanged(NetworkConnection conn) => RebuildReadyList();

        private void RebuildReadyList()
        {
            if (!IsServerInitialized) return;

            AffinitySelectionService service = AffinitySelectionService.Instance;
            if (service == null) return;

            // Full rebuild rather than a diff: the roster is at most a handful of
            // ints and changes a few times per match.
            ReadyConnectionIds.Clear();

            if (InstanceFinder.ServerManager == null) return;

            foreach (NetworkConnection conn in InstanceFinder.ServerManager.Clients.Values)
            {
                if (conn == null || !conn.IsActive) continue;
                if (!service.IsReady(conn)) continue;

                ReadyConnectionIds.Add(conn.ClientId);
            }
        }

        private void BeginMatch(bool autoReadyStragglers)
        {
            _countdownRunning = false;
            _secondsRemaining.Value = 0;

            AffinitySelectionService service = AffinitySelectionService.Instance;

            if (autoReadyStragglers && service != null && _registry != null)
            {
                service.CollectUnready(_unreadyBuffer);

                for (int i = 0; i < _unreadyBuffer.Count; i++)
                {
                    // Sanitize(null) is the full default build, not an empty one.
                    AffinityLoadout fallback = AffinityLoadoutRules.Sanitize(_registry, null);
                    service.ServerSetLoadout(_unreadyBuffer[i], fallback);
                }

                if (_unreadyBuffer.Count > 0)
                    Debug.Log($"[{nameof(AffinitySelectCoordinator)}] Countdown expired; {_unreadyBuffer.Count} player(s) auto-filled with the default build.");
            }

            if (GameFlowController.Instance == null)
            {
                Debug.LogError($"[{nameof(AffinitySelectCoordinator)}] No {nameof(GameFlowController)} found - cannot start the match.", this);
                return;
            }

            GameFlowController.Instance.RequestEnterGame();
        }
    }
}
