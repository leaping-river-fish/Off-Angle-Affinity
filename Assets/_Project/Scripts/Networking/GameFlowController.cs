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
            => InstanceFinder.SceneManager.LoadGlobalScenes(new SceneLoadData(_lobbySceneName) { ReplaceScenes = ReplaceOption.All });

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
            InstanceFinder.SceneManager.LoadGlobalScenes(new SceneLoadData(_gameSceneName) { ReplaceScenes = ReplaceOption.None });
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
            Debug.Log($"[{nameof(GameFlowController)}] Game scene loaded (active scene now '{UnitySceneManager.GetActiveScene().name}'). Spawning ready players.");
            PlayerSpawner.Instance?.SpawnAll();
        }
    }
}
