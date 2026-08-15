// =============================================================================
// GameFlowController — centralizes MainMenu -> Lobby -> Game scene sequencing.
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
// =============================================================================

using FishNet;
using FishNet.Managing.Scened;
using UnityEngine;

namespace OffAngle.Networking
{
    public class GameFlowController : MonoBehaviour
    {
        public static GameFlowController Instance { get; private set; }

        [Tooltip("Scene loaded once the server starts.")]
        [SerializeField] private string _lobbySceneName = "Lobby";

        [Tooltip("Scene loaded once the host clicks Start Game.")]
        [SerializeField] private string _gameSceneName = "Game";

        [Tooltip("Leave null to auto-resolve via GetComponent (same GameObject).")]
        [SerializeField] private NetworkMenuController _networkMenuController;

        private bool _hasGameStarted;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[{nameof(GameFlowController)}] Duplicate instance detected; keeping the first.", this);
                return;
            }
            Instance = this;

            if (_networkMenuController == null)
                _networkMenuController = GetComponent<NetworkMenuController>();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void OnEnable() => _networkMenuController.ServerStarted += HandleServerStarted;
        private void OnDisable() => _networkMenuController.ServerStarted -= HandleServerStarted;

        private void HandleServerStarted()
            => InstanceFinder.SceneManager.LoadGlobalScenes(new SceneLoadData(_lobbySceneName) { ReplaceScenes = ReplaceOption.All });

        /// <summary>Called by LobbyMenuUI's Start Game button (host-only).</summary>
        public void RequestStartGame()
        {
            if (!InstanceFinder.IsServerStarted || _hasGameStarted)
                return;
            _hasGameStarted = true;

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
