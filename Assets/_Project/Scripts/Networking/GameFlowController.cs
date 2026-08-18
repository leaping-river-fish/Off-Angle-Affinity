// =============================================================================
// GameFlowController — centralizes Bootstrap -> MainMenu -> Lobby ->
// AffinitySelect -> Game scene sequencing.
//
// TWO STEPS, TWO LATCHES:
//   The Lobby's Start button no longer loads Game directly. It loads the
//   AffinitySelect scene (RequestStartMatch), and AffinitySelectCoordinator
//   loads Game once its countdown expires or everyone is ready
//   (RequestEnterGame). Each transition has its own latch so a second match can
//   still run - a single shared flag would let one step's latch block the
//   other's.
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
        /// expires or every player is ready. Loads the Game scene and spawns.
        /// </summary>
        public void RequestEnterGame()
        {
            if (!InstanceFinder.IsServerStarted || _hasEnteredGame)
                return;
            _hasEnteredGame = true;

            InstanceFinder.SceneManager.OnLoadEnd += HandleGameSceneLoaded;
            InstanceFinder.SceneManager.LoadGlobalScenes(new SceneLoadData(_gameSceneName) { ReplaceScenes = ReplaceOption.All });
        }

        private void HandleGameSceneLoaded(SceneLoadEndEventArgs args)
        {
            if (!args.QueueData.AsServer)
                return;
            InstanceFinder.SceneManager.OnLoadEnd -= HandleGameSceneLoaded;

            PlayerSpawner.Instance?.ResolveSpawnPoints();
            PlayerSpawner.Instance?.SpawnAll();
        }
    }
}
