// =============================================================================
// PlayerNameRegistry — server-authoritative clientId -> display name map, kept
// in sync with every peer so the Lobby, Scoreboard, and Match End screens can
// all show a real chosen name instead of LobbyPlayerList's "Player #"
// placeholder.
//
// ARCHITECTURE:
//   NOT a scene object like LobbyPlayerList/AffinitySelectCoordinator - this
//   must survive the Bootstrap -> MainMenu transition, and GameFlowController.
//   Start() does that with a plain single-mode UnitySceneManager.LoadScene,
//   which unloads Bootstrap entirely except for whatever is DontDestroyOnLoad
//   (the NetworkManager GameObject). A scene object placed in Bootstrap dies
//   right there, before the player even reaches the Main Menu. FishNet also
//   explicitly ignores the IsGlobal flag on scene objects (see NetworkObject.
//   Preinitialize's warning) - IsGlobal only works for spawned prefabs. So
//   this is a spawned PREFAB, marked global, instantiated once by
//   GameFlowController.HandleServerStarted() the moment hosting begins.
//   FishNet keeps a global spawned object alive and visible to every
//   connection (including late joiners) regardless of which scenes are
//   loaded, which is exactly "survive every scene transition" for free.
//
//   Names is the source of truth every peer can read directly; LobbyPlayerList.
//   LabelFor() is the one call site all three UIs already share, so it now
//   checks here first and only falls back to "Player {id}" for a connection
//   that has never submitted a name.
//
// WHERE A NAME COMES FROM:
//   The Main Menu's naming panel (PlayerNameEntryUI) writes straight to
//   PlayerPrefs and calls RequestLocalNameChange - that happens BEFORE any
//   connection exists, so it cannot reach this object's ServerRpc yet.
//   OnStartClient is what actually pushes it over the network: it fires once
//   per peer (including the host's own loopback client) the moment this
//   persistent object starts for them, which is exactly when a session first
//   becomes live. A later in-session rename (if some future panel ever offers
//   one) goes through RequestLocalNameChange's live push instead.
//
// AUTHORITY:
//   Clients propose, the server disposes - same rule AffinitySelectCoordinator
//   applies to loadouts. Sanitize() is re-run on the server regardless of what
//   the sending client already trimmed, since a modified client could call
//   CmdSetDisplayName directly with an arbitrary string.
//
// PER-SESSION RESET:
//   Names.Clear() only happens in OnStopServer (a full server stop - starting
//   a brand new hosting session shouldn't carry over the last one's names
//   against what may now be different players holding the same client ids).
//   Unlike AffinitySelectionService/PlayerSpawner, a return-to-lobby mid-
//   session does NOT clear this - a player's chosen name should survive
//   match-to-match for as long as they stay connected.
//
// MANUAL SETUP:
//   1. Create a PREFAB (not a scene object): empty GameObject (e.g. "Player
//      Name Registry"), add a NetworkObject component, then this component.
//   2. Tick "Is Global" on the NetworkObject in the Inspector (belt and
//      braces - HandleServerStarted also sets this in code, since FishNet
//      only allows changing it immediately after Instantiate, before Spawn).
//   3. Assign the prefab to GameFlowController's _playerNameRegistryPrefab
//      field on the persistent NetworkManager GameObject in Bootstrap.
// =============================================================================

using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace OffAngle.Networking
{
    public class PlayerNameRegistry : NetworkBehaviour
    {
        /// <summary>PlayerPrefs key the Main Menu naming panel reads/writes.</summary>
        public const string PlayerNamePrefsKey = "PlayerDisplayName";

        /// <summary>Longest name accepted, after trimming. Server-enforced, not just a UI character limit.</summary>
        public const int MaxNameLength = 20;

        public readonly SyncDictionary<int, string> Names = new SyncDictionary<int, string>();

        /// <summary>Scene singleton, set once the object initializes on every peer.</summary>
        public static PlayerNameRegistry Instance { get; private set; }

        /// <summary>
        /// Fires right after Instance is set. FishNet activates scene
        /// NetworkObjects a beat later than plain scene MonoBehaviours, so a UI
        /// object enabling in the same scene load cannot just check Instance
        /// once. Same caveat LobbyPlayerList documents.
        /// </summary>
        public static event System.Action InstanceReady;

        private void Awake()
        {
            // Guards only the static pointer, not the whole component - see
            // LobbyPlayerList.Awake for why disabling a FishNet-driven
            // NetworkBehaviour here isn't the right tool.
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(PlayerNameRegistry)}] Duplicate instance detected; keeping the first.", this);
                return;
            }

            Instance = this;
            InstanceReady?.Invoke();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            Names.Clear();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Seed the server with whatever this peer already had saved locally -
            // see the WHERE A NAME COMES FROM note above for why this can't
            // happen any earlier.
            string savedName = PlayerPrefs.GetString(PlayerNamePrefsKey, "");
            if (!string.IsNullOrEmpty(savedName))
                CmdSetDisplayName(savedName);
        }

        // ------------------------------------------------------------------
        // Client -> server
        // ------------------------------------------------------------------

        [ServerRpc(RequireOwnership = false)]
        private void CmdSetDisplayName(string requestedName, NetworkConnection sender = null)
        {
            if (sender == null) return;

            string sanitized = Sanitize(requestedName);
            if (string.IsNullOrEmpty(sanitized))
                Names.Remove(sender.ClientId);
            else
                Names[sender.ClientId] = sanitized;
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Saves the given name to PlayerPrefs for future sessions and, if a
        /// network session is already live, pushes it to the server
        /// immediately. Safe to call before ever connecting (e.g. from the
        /// Main Menu naming panel) - the PlayerPrefs write always happens, and
        /// OnStartClient covers pushing it once a session actually starts.
        /// </summary>
        public static void RequestLocalNameChange(string rawName)
        {
            string sanitized = Sanitize(rawName);

            if (string.IsNullOrEmpty(sanitized))
                PlayerPrefs.DeleteKey(PlayerNamePrefsKey);
            else
                PlayerPrefs.SetString(PlayerNamePrefsKey, sanitized);
            PlayerPrefs.Save();

            if (Instance != null && Instance.IsClientInitialized)
                Instance.CmdSetDisplayName(sanitized);
        }

        /// <summary>True if this connection has a submitted, non-empty name; out param carries it.</summary>
        public static bool TryGetName(int clientId, out string name)
        {
            if (Instance != null && Instance.Names.TryGetValue(clientId, out name) && !string.IsNullOrEmpty(name))
                return true;

            name = null;
            return false;
        }

        // Trims and clamps length; also strips '<'/'>' so a malicious client
        // can't inject TMP rich-text tags (e.g. <size=300>) into every other
        // peer's Lobby/Scoreboard/Match End labels via CmdSetDisplayName.
        private static string Sanitize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            string cleaned = raw.Replace("<", "").Replace(">", "").Trim();
            return cleaned.Length > MaxNameLength ? cleaned.Substring(0, MaxNameLength) : cleaned;
        }
    }
}
