// =============================================================================
// GameFlowController — centralizes Bootstrap -> MainMenu -> Lobby ->
// AffinitySelect -> Game scene sequencing.
//
// THREE STEPS, THREE LATCHES:
//   The Lobby's Start button no longer loads Game directly. It loads the
//   AffinitySelect scene (RequestStartMatch), and AffinitySelectCoordinator
//   loads Game once its countdown expires or everyone is ready
//   (RequestEnterGame) - deliberately WITHOUT unloading AffinitySelect, so any
//   player still incomplete keeps using that scene's UI to finish picking.
//   Once every connected player has submitted, the coordinator calls
//   RequestUnloadAffinitySelect to finally tear that scene down. Each
//   transition has its own latch so a second match can still run - a single
//   shared flag would let one step's latch block the others'.
//
//   RequestReturnToLobby is the reverse trip, called by the win/lose screen's
//   "Return to Lobby" button once MatchManager has declared a winner. It has
//   NO latch of its own (it must be callable again after every rematch), and
//   in addition to resetting the three latches above, it must also reset the
//   per-match state PlayerSpawner and AffinitySelectionService each hold on
//   the persistent NetworkManager GameObject - see that method for why.
//
// ARCHITECTURE:
//   NetworkMenuController only reports connection facts (ServerStarted);
//   scene-loading decisions live here instead, so this is the one place that
//   needs to change when a future Lobby map-selector loads different Game
//   scenes per match.
//
//   Scenes are loaded as FishNet "global" scenes (LoadGlobalScenes), not via
//   plain UnityEngine.SceneManagement.SceneManager -- LobbyPlayerList is a
//   scene NetworkObject and only stays in sync with clients when its scene is
//   loaded through FishNet's own SceneManager.
//
// PLACEMENT:
//   Lives on the same persistent GameObject as NetworkManager (which already
//   has DontDestroyOnLoad enabled), so it survives every scene transition.
//   That object lives in the Bootstrap scene -- NOT in MainMenu. Keeping it out
//   of MainMenu is what makes a duplicate NetworkManager impossible: returning
//   to the menu used to re-instantiate one, and FishNet's DestroyNewest
//   persistence then DestroyImmediate'd it along with the fresh menu's wiring.
// =============================================================================

using FishNet;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace OffAngle.Networking
{
    public class GameFlowController : MonoBehaviour
    {
        public static GameFlowController Instance { get; private set; }

        [Tooltip("Scene loaded on startup from Bootstrap. Clear this to disable the automatic first load.")]
        [SerializeField] private string _mainMenuSceneName = "MainMenu";

        [Tooltip("Scene loaded once the server starts.")]
        [SerializeField] private string _lobbySceneName = "Lobby";

        [Tooltip("Scene loaded once the host clicks Start Game. Players pick their Affinity here; AffinitySelectCoordinator loads the Game scene when its countdown ends.")]
        [SerializeField] private string _affinitySelectSceneName = "AffinitySelect";

        [Tooltip("Scene loaded once the Affinity selection countdown expires or every player is ready.")]
        [SerializeField] private string _gameSceneName = "Game";

        [Tooltip("Leave null to auto-resolve via GetComponent (same GameObject).")]
        [SerializeField] private NetworkMenuController _networkMenuController;

        [Tooltip("Root NetworkObject of the PlayerNameRegistry prefab. Spawned once, as a global object, the moment hosting starts - see HandleServerStarted. Must NOT be a scene object; FishNet ignores IsGlobal on those.")]
        [SerializeField] private NetworkObject _playerNameRegistryPrefab;

        private bool _hasEnteredAffinitySelect;
        private bool _hasEnteredGame;
        private bool _hasUnloadedAffinitySelect;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(GameFlowController)}] Duplicate instance detected; keeping the first.", this);

                // Disabling here is what stops OnEnable running on the duplicate.
                // It returns before _networkMenuController is resolved below, so
                // an enabled duplicate would dereference null in OnEnable.
                enabled = false;
                return;
            }
            Instance = this;

            if (_networkMenuController == null)
                _networkMenuController = GetComponent<NetworkMenuController>();
        }

        private void Start()
        {
            if (InstanceFinder.ServerManager != null)
                InstanceFinder.ServerManager.OnServerConnectionState += HandleServerConnectionState;

            // Runs on every peer, not just the host - RequestEnterGame's own
            // OnLoadEnd subscription below only exists on the server (see its
            // IsServerStarted gate), so a pure joining client had nothing ever
            // setting ITS OWN active scene to Game. Since Game loads with
            // ReplaceOption.None specifically to keep AffinitySelect loaded for
            // stragglers, a joining client's Unity active scene stayed on
            // AffinitySelect indefinitely - and because RenderSettings
            // (skybox/ambient/fog) are sourced only from the active scene, and
            // AffinitySelect ships with no skybox assigned, that client's camera
            // rendered a flat background color plus Unity's default fallback
            // reflection cubemap instead of the sky. Subscribed once, forever,
            // same as the connection-state handler right above - it is stateless
            // and has nothing per-match to reset.
            if (InstanceFinder.SceneManager != null)
                InstanceFinder.SceneManager.OnLoadEnd += HandleAnyPeerGameSceneLoaded;

            // Bootstrap contains only the NetworkManager object, so something has
            // to move the player to an actual screen. This runs exactly once:
            // the object is DontDestroyOnLoad, so Start never fires again on
            // later scene loads. Plain Unity load, not FishNet's -- there is no
            // session yet, and FishNet's SceneManager only works while connected.
            if (!string.IsNullOrWhiteSpace(_mainMenuSceneName) &&
                UnitySceneManager.GetActiveScene().name != _mainMenuSceneName)
            {
                UnitySceneManager.LoadScene(_mainMenuSceneName);
            }
        }

        private void OnDestroy()
        {
            if (InstanceFinder.ServerManager != null)
                InstanceFinder.ServerManager.OnServerConnectionState -= HandleServerConnectionState;

            if (InstanceFinder.SceneManager != null)
                InstanceFinder.SceneManager.OnLoadEnd -= HandleAnyPeerGameSceneLoaded;

            if (Instance == this)
                Instance = null;
        }

        // Null-guarded because Awake returns early on a duplicate. Belt and
        // braces alongside the enabled = false above.
        private void OnEnable()
        {
            if (_networkMenuController != null)
                _networkMenuController.ServerStarted += HandleServerStarted;
        }

        private void OnDisable()
        {
            if (_networkMenuController != null)
                _networkMenuController.ServerStarted -= HandleServerStarted;
        }

        // Match state is per-session, but this object is DontDestroyOnLoad, so
        // without an explicit reset a SECOND match can never start:
        // RequestStartMatch()/RequestEnterGame() below return immediately on
        // their latched flags.
        private void HandleServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;

            _hasEnteredAffinitySelect = false;
            _hasEnteredGame = false;
            _hasUnloadedAffinitySelect = false;

            // Still subscribed if the server stopped mid-load, i.e. before
            // HandleGameSceneLoaded ever ran to remove it.
            if (InstanceFinder.SceneManager != null)
                InstanceFinder.SceneManager.OnLoadEnd -= HandleGameSceneLoaded;
        }

        private void HandleServerStarted()
        {
            SpawnPlayerNameRegistry();
            InstanceFinder.SceneManager.LoadGlobalScenes(new SceneLoadData(_lobbySceneName) { ReplaceScenes = ReplaceOption.All });
        }

        // PlayerNameRegistry must survive every scene transition from here on,
        // which rules out a scene object the way LobbyPlayerList/
        // AffinitySelectCoordinator use one - Start() above already unloads
        // Bootstrap (and anything placed in it) the moment MainMenu loads.
        // Spawning it here, as a global object, is what actually keeps it
        // alive: FishNet visibility-tracks a global spawned object for every
        // connection regardless of loaded scenes, including a client that
        // joins after this already ran. SetIsGlobal must run between
        // Instantiate and Spawn - FishNet only allows changing it before the
        // object initializes onto the network.
        private void SpawnPlayerNameRegistry()
        {
            if (_playerNameRegistryPrefab == null)
            {
                Debug.LogError($"[{nameof(GameFlowController)}] No PlayerNameRegistry prefab assigned; player display names will never leave PlayerPrefs.", this);
                return;
            }

            if (PlayerNameRegistry.Instance != null) return;

            NetworkObject instance = Instantiate(_playerNameRegistryPrefab);
            instance.SetIsGlobal(true);
            InstanceFinder.ServerManager.Spawn(instance);
        }

        /// <summary>
        /// Called by LobbyMenuUI's Start Game button (host-only). Loads the
        /// AffinitySelect scene, NOT the Game scene - the match itself starts from
        /// there once players have chosen. See RequestEnterGame.
        /// </summary>
        public void RequestStartMatch()
        {
            if (!InstanceFinder.IsServerStarted || _hasEnteredAffinitySelect)
                return;
            _hasEnteredAffinitySelect = true;

            InstanceFinder.SceneManager.LoadGlobalScenes(new SceneLoadData(_affinitySelectSceneName) { ReplaceScenes = ReplaceOption.All });
        }

        /// <summary>
        /// Called by AffinitySelectCoordinator (host-only) when its countdown
        /// expires or every player is ready. Loads the Game scene and spawns
        /// whoever has already submitted a loadout.
        /// </summary>
        public void RequestEnterGame()
        {
            if (!InstanceFinder.IsServerStarted || _hasEnteredGame)
                return;
            _hasEnteredGame = true;

            InstanceFinder.SceneManager.OnLoadEnd += HandleGameSceneLoaded;

            // ReplaceOption.None, not .All: AffinitySelect deliberately stays
            // loaded so a player who is still incomplete can keep using its UI to
            // finish picking. It is a global scene either way, so it is already
            // loaded for every connection, including ready ones - RequestUnloadAffinitySelect
            // tears it down once nobody needs it anymore.
            SceneLoadData data = new SceneLoadData(_gameSceneName) { ReplaceScenes = ReplaceOption.None };

            // Explicitly telling FishNet the preferred active scene (for BOTH
            // client and server) is what actually fixes the active scene here,
            // not HandleGameSceneLoaded's own SetActiveScene call below - that
            // call only reacts to scenes FishNet reports as freshly LOADED this
            // pass, but on a host, the local client's own pass of this SAME
            // Game load reports it as SKIPPED (already loaded server-side a
            // moment earlier in the same process), so nothing ever sees it in
            // that pass's LoadedScenes to react to. FishNet's own internal
            // active-scene resolution still runs unconditionally for that
            // skipped pass regardless, and defaults back to AffinitySelect
            // (since Game loaded with ReplaceOption.None) - silently undoing
            // HandleGameSceneLoaded's fix sometime after it ran, with nothing
            // in the Console to explain it. A player who finished their pick
            // late - after this had already happened - spawned into whatever
            // scene was active at that moment (AffinitySelect), got swept away
            // the instant that scene unloaded moments later, and never got a
            // working camera. Setting this up front means FishNet's own
            // resolution gets it right on every pass, load or skip, server or
            // client - see SceneManager.GetUserPreferredActiveScene.
            data.PreferredActiveScene = new PreferredScene(new SceneLookupData(_gameSceneName));

            InstanceFinder.SceneManager.LoadGlobalScenes(data);
        }

        /// <summary>
        /// Called by AffinitySelectCoordinator (host-only) once every connected
        /// player has submitted a loadout. Unloads the AffinitySelect scene that
        /// RequestEnterGame deliberately left loaded for stragglers.
        /// </summary>
        public void RequestUnloadAffinitySelect()
        {
            if (!InstanceFinder.IsServerStarted || _hasUnloadedAffinitySelect)
                return;
            _hasUnloadedAffinitySelect = true;

            InstanceFinder.SceneManager.UnloadGlobalScenes(new SceneUnloadData(_affinitySelectSceneName));
        }

        /// <summary>
        /// Called by MatchEndUI's "Return to Lobby" button (host-only) once
        /// MatchManager has declared a winner. Unlike the three forward
        /// transitions above, this has NO latch - it must be callable again
        /// for match 3, 4, etc. Everything a match latches outside this class
        /// (PlayerSpawner's queue/started flag, AffinitySelectionService's
        /// ready set) is reset here too, in a specific order, BEFORE the scene
        /// load - without that, the Lobby this loads looks fine but is broken
        /// underneath: PlayerSpawner would still think the match already
        /// started (kicking new joiners, spawning nobody on the next
        /// SpawnAll()), and AffinitySelectionService would still show every
        /// connection as ready (skipping the picker entirely on the next
        /// AffinitySelect screen).
        /// </summary>
        public void RequestReturnToLobby()
        {
            if (!InstanceFinder.IsServerStarted)
                return;

            // Despawn players explicitly rather than relying on the scene
            // unload below to clean them up. FishNet's graceful "move spawned
            // objects out before the scene unloads" path
            // (SceneManager.MoveClientHostObjects) never actually runs for a
            // root-level NetworkObject in the installed FishNet version - its
            // own root-check continues unconditionally, since Transform.root
            // returns the transform itself, never null, for something that IS
            // already a root. Left alone, players would go through FishNet's
            // "unexpectedly destroyed" cleanup path instead (which does still
            // fire OnStopServer/OnDestroy correctly, so nothing here is broken
            // by skipping this step - it's just untested territory in this
            // project, and despawning explicitly is one small method).
            PlayerSpawner.Instance?.ServerDespawnAll();

            // Order matters: PlayerSpawner's _hasGameStarted must already be
            // false before AffinitySelectionService clears _ready, since a
            // ready-state change there can call back into
            // PlayerSpawner.ServerSpawnIfReady.
            PlayerSpawner.Instance?.ServerResetForNewMatch();
            AffinitySelectionService.Instance?.ServerResetForNewMatch();

            _hasEnteredAffinitySelect = false;
            _hasEnteredGame = false;
            _hasUnloadedAffinitySelect = false;

            // Belt and braces against a stale subscription double-spawning on
            // the next RequestEnterGame - same guard HandleServerConnectionState
            // already applies for a full server stop.
            if (InstanceFinder.SceneManager != null)
                InstanceFinder.SceneManager.OnLoadEnd -= HandleGameSceneLoaded;

            SceneLoadData data = new SceneLoadData(_lobbySceneName) { ReplaceScenes = ReplaceOption.All };
            data.PreferredActiveScene = new PreferredScene(new SceneLookupData(_lobbySceneName));
            InstanceFinder.SceneManager.LoadGlobalScenes(data);
        }

        /// <summary>
        /// Sets Game active locally for WHICHEVER peer just finished loading it -
        /// host or a joining client alike. Unlike HandleGameSceneLoaded (which
        /// only ever runs server-side, for spawning), this has no per-peer role
        /// check and no PlayerSpawner work; it exists purely to fix each peer's
        /// own Unity active-scene pointer so RenderSettings resolve to Game's
        /// skybox instead of quietly falling back to the flat background color +
        /// default reflection cubemap. Safe to fire redundantly alongside
        /// HandleGameSceneLoaded's own SetActiveScene call on the host.
        /// </summary>
        private void HandleAnyPeerGameSceneLoaded(SceneLoadEndEventArgs args)
        {
            foreach (UnityEngine.SceneManagement.Scene scene in args.LoadedScenes)
            {
                if (scene.name != _gameSceneName) continue;

                UnitySceneManager.SetActiveScene(scene);
                break;
            }
        }

        private void HandleGameSceneLoaded(SceneLoadEndEventArgs args)
        {
            if (!args.QueueData.AsServer)
                return;
            InstanceFinder.SceneManager.OnLoadEnd -= HandleGameSceneLoaded;

            // Game was loaded with ReplaceOption.None, so FishNet has no reason to
            // hand it the "active scene" - with AffinitySelect still the first
            // global scene, FishNet's own SetActiveScene leaves (or puts) AffinitySelect
            // active instead. Left alone, PlayerSpawner's Instantiate calls below
            // would drop new players into AffinitySelect by default, and lose them
            // the instant RequestUnloadAffinitySelect tears that scene down. Forcing
            // Game active here, once, is enough for every later spawn too (join-in-
            // progress, stragglers) - nothing re-touches the active scene until
            // AffinitySelect actually unloads, at which point Game is the only
            // global scene left and FishNet's own resolution agrees anyway.
            UnityEngine.SceneManagement.Scene gameScene = UnitySceneManager.GetSceneByName(_gameSceneName);
            if (gameScene.IsValid())
                UnitySceneManager.SetActiveScene(gameScene);
            else
                Debug.LogError($"[{nameof(GameFlowController)}] Game scene '{_gameSceneName}' was not valid right after its own OnLoadEnd fired - active scene was left unchanged.", this);

            PlayerSpawner.Instance?.ResolveSpawnPoints();
            PlayerSpawner.Instance?.SpawnAll();
        }
    }
}
