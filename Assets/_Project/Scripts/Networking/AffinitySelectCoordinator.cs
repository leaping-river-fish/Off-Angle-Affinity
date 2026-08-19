// =============================================================================
// AffinitySelectCoordinator — the networked surface of the AffinitySelect
// scene: submission and the countdown that starts the match.
//
// ARCHITECTURE:
//   A scene NetworkObject, built the same way as LobbyPlayerList - it exists on
//   every peer from scene load and FishNet syncs it automatically, with no
//   manual Spawn() call. It collects submissions and hands them to
//   AffinitySelectionService, which is where they actually live: this object
//   is destroyed the moment AffinitySelect is unloaded, and the picks have to
//   outlive it.
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
// STRAGGLERS ARE NEVER AUTO-FILLED:
//   When the countdown expires, only players who have already submitted move
//   into the Game scene - see BeginMatch. Anyone still incomplete is not
//   given a default build and is not forced in; GameFlowController.RequestEnterGame
//   deliberately leaves the AffinitySelect scene loaded (rather than replacing
//   it) so those players can keep using this same UI to finish picking.
//   AffinitySelectionService.ServerReadyStateChanged -> PlayerSpawner already
//   spawns a straggler the instant they submit, exactly like a join-in-progress
//   connection - see PlayerSpawner.ServerSpawnIfReady. Once every connected
//   player has submitted, RequestUnloadAffinitySelect finally tears this scene
//   down (see the Update loop below).
//
// MANUAL SETUP:
//   1. Create the AffinitySelect scene and add it to Build Settings, between
//      Lobby and Game.
//   2. Add an empty GameObject (e.g. "Affinity Select State") SAVED IN THAT
//      SCENE - it must be part of the scene so FishNet bakes it a scene ID.
//   3. Add a NetworkObject component, then this component.
//   4. Assign the project's AffinityRegistry.
//   5. Point the selection UI at CmdSubmitLoadout / SecondsRemaining.
// =============================================================================

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
        [Tooltip("Seconds players have to choose before ready players enter the match. Anyone still incomplete keeps selecting and is spawned in automatically the moment they submit.")]
        [SerializeField, Min(5f)] private float _selectionSeconds = 90f;

        [Tooltip("Start immediately once every connected player has submitted, rather than waiting out the clock.")]
        [SerializeField] private bool _startEarlyWhenAllReady = true;

        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<int> _secondsRemaining = new SyncVar<int>();

        /// <summary>Whole seconds left before ready players enter the match automatically.</summary>
        public int SecondsRemaining => _secondsRemaining.Value;

        /// <summary>Scene singleton, set once the object initializes on every peer.</summary>
        public static AffinitySelectCoordinator Instance { get; private set; }

        /// <summary>
        /// Fires right after Instance is set. FishNet activates scene
        /// NetworkObjects a beat later than plain scene MonoBehaviours, so a UI
        /// object enabling in the same scene load cannot just check Instance once.
        /// Same caveat LobbyPlayerList documents.
        /// </summary>
        public static event System.Action InstanceReady;

        private float _deadline;
        private bool _countdownRunning;
        private bool _matchStarted;
        private bool _affinitySelectFinalized;

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
            // Fires on every peer, server or client, the instant the AffinitySelect
            // scene actually unloads locally - the single clearest signal for
            // telling apart "the scene never unloaded for this peer" from "the
            // scene unloaded but its UI didn't disappear with it".
            Debug.Log($"[{nameof(AffinitySelectCoordinator)}] Destroyed on this peer (AffinitySelect scene unloading here now). IsServer={IsServerInitialized}, IsClient={IsClientInitialized}.");

            if (Instance == this)
                Instance = null;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            _deadline = Time.time + _selectionSeconds;
            _countdownRunning = true;
            _matchStarted = false;
            _affinitySelectFinalized = false;
            _secondsRemaining.Value = Mathf.CeilToInt(_selectionSeconds);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            _countdownRunning = false;
        }

        private void Update()
        {
            if (!IsServerInitialized) return;

            if (_countdownRunning)
            {
                int remaining = Mathf.Max(0, Mathf.CeilToInt(_deadline - Time.time));

                // Written only when the whole-second value actually changes - one
                // small message per second rather than one per frame.
                if (remaining != _secondsRemaining.Value)
                    _secondsRemaining.Value = remaining;

                if (remaining <= 0)
                {
                    _countdownRunning = false;
                    BeginMatch();
                }
                else if (_startEarlyWhenAllReady &&
                         AffinitySelectionService.Instance != null &&
                         AffinitySelectionService.Instance.AreAllConnectedReady())
                {
                    _countdownRunning = false;
                    BeginMatch();
                }
            }

            // Independent of the countdown above: once every connected player has
            // submitted (whether that happened before the deadline or, for a
            // straggler, well after ready players already entered the match),
            // AffinitySelect has nothing left to do and can be torn down.
            if (_matchStarted && !_affinitySelectFinalized &&
                AffinitySelectionService.Instance != null &&
                AffinitySelectionService.Instance.AreAllConnectedReady())
            {
                _affinitySelectFinalized = true;
                Debug.Log($"[{nameof(AffinitySelectCoordinator)}] Every connected player is ready (readyCount={AffinitySelectionService.Instance.ReadyCount}) - unloading AffinitySelect.");
                GameFlowController.Instance?.RequestUnloadAffinitySelect();
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

        /// <summary>
        /// Moves every currently-ready player into the Game scene. Anyone not yet
        /// ready is left exactly where they are - see the STRAGGLERS ARE NEVER
        /// AUTO-FILLED note at the top of this file.
        /// </summary>
        private void BeginMatch()
        {
            if (_matchStarted) return;
            _matchStarted = true;
            _secondsRemaining.Value = 0;

            if (GameFlowController.Instance == null)
            {
                Debug.LogError($"[{nameof(AffinitySelectCoordinator)}] No {nameof(GameFlowController)} found - cannot start the match.", this);
                return;
            }

            Debug.Log($"[{nameof(AffinitySelectCoordinator)}] BeginMatch - readyCount={AffinitySelectionService.Instance?.ReadyCount.ToString() ?? "n/a"}, connected clients={FishNet.InstanceFinder.ServerManager?.Clients.Count.ToString() ?? "n/a"}.");
            GameFlowController.Instance.RequestEnterGame();
        }
    }
}
