// =============================================================================
// NetworkPlayerController — ownership gate for the player prefab.
//
// ARCHITECTURE:
//   This is the ONLY networking-aware component on the player. Its sole job is
//   to decide, when the prefab spawns on a given peer, whether this instance
//   represents the local player (IsOwner == true) or a remote player.
//
//   - Local owner   → enable input, camera, and movement simulation.
//   - Remote player → leave all the above OFF. NetworkTransform drives the
//                     remote's transform; the local simulation must not run
//                     because it would fight the network and burn CPU.
//
//   Gameplay code (PlayerController, MovementStateMachine, PlayerInputReader,
//   PlayerCameraController) is untouched. They do not import FishNet and do
//   not know they are being networked. This honours the "networking code
//   separated from gameplay logic" rule.
//
// ─────────────────────────────────────────────────────────────────────────────
// REQUIRED PREFAB DEFAULTS (set ONCE when authoring the player prefab):
// ─────────────────────────────────────────────────────────────────────────────
//   On the prefab root:
//     - PlayerInputReader.enabled      = FALSE
//     - MovementStateMachine.enabled   = FALSE
//   On the camera CHILD GameObject:
//     - GameObject.activeSelf          = FALSE  (entire camera subtree off)
//
//   The PlayerController component itself stays ENABLED so its Awake() can
//   build the MovementStateContext and call StateMachine.Initialize(). The
//   subscriptions Initialize() makes to disabled-input-reader events are
//   inert and free; cleaner than refactoring PlayerController for ownership.
//
//   WHY these specific defaults instead of toggling .enabled in OnStartClient
//   for remotes:
//   Because PlayerCameraController.OnDisable() unlocks the cursor (see
//   PlayerCameraController.cs lines 83-90). If a remote player's camera
//   controller ever runs OnDisable, every local player's cursor unlocks. By
//   keeping remote components in their never-enabled state from prefab
//   instantiation onward, OnDisable never fires on remotes. The local owner
//   gets a single clean OnEnable when SetActive(true) flips below.
//
// ─────────────────────────────────────────────────────────────────────────────
// LIFECYCLE ORDER (do not change without testing the cursor lock carefully):
// ─────────────────────────────────────────────────────────────────────────────
//   1. Prefab instantiated by PlayerSpawner on server.
//   2. NetworkObject syncs to clients; instance appears on every peer.
//   3. PlayerController.Awake() runs on every peer (gameplay-agnostic init).
//   4. NetworkBehaviour.OnStartClient() runs on every client (this script).
//   5. For IsOwner: input reader enabled, state machine enabled, camera
//      subtree activated. Their OnEnables fire in that order.
//   6. For !IsOwner: nothing happens here; everything stays in the off
//      defaults baked into the prefab.
// =============================================================================

using FishNet.Object;
using UnityEngine;
using OffAngle.Core;
using OffAngle.Movement;
using OffAngle.Player;

namespace OffAngle.Networking
{
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Owner-only components (must be DISABLED in the prefab)")]
        [SerializeField] private PlayerInputReader      _inputReader;
        [SerializeField] private MovementStateMachine   _stateMachine;

        [Header("Camera subtree (must be SetActive(false) in the prefab)")]
        [Tooltip("The root GameObject of the player's camera. Activated for the local owner; left inactive for remote players to avoid extra cameras / audio listeners.")]
        [SerializeField] private GameObject             _cameraRoot;

        // ------------------------------------------------------------------
        // FishNet lifecycle
        // ------------------------------------------------------------------

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Remote players: bail out. Their components stay in the prefab's
            // disabled defaults, so OnEnable/OnDisable never fires for them.
            if (!base.IsOwner)
                return;

            // Local owner: light up the gameplay stack in a deterministic order.
            // PlayerInputReader.OnEnable must run BEFORE PlayerCameraController.OnEnable
            // (which is inside _cameraRoot.SetActive) so the camera's seed read of
            // _inputReader.LookEvent attaches to a fully-initialised reader.
            if (_inputReader != null)  _inputReader.enabled  = true;
            if (_stateMachine != null) _stateMachine.enabled = true;
            if (_cameraRoot != null)   _cameraRoot.SetActive(true);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            // Only the local owner had components enabled; only the local owner
            // needs to tear them down. This runs when the player despawns
            // (e.g. disconnect) so the cursor unlocks cleanly on shutdown.
            if (!base.IsOwner)
                return;

            if (_cameraRoot != null)   _cameraRoot.SetActive(false);
            if (_stateMachine != null) _stateMachine.enabled = false;
            if (_inputReader != null)  _inputReader.enabled  = false;
        }

        // ------------------------------------------------------------------
        // Editor sanity check
        // ------------------------------------------------------------------

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Warn (but do not auto-fix) if prefab defaults are wrong. Auto-fix
            // would mask user mistakes; a console warning is enough to catch
            // them before the smoke test fails confusingly.
            if (Application.isPlaying)
                return;

            if (_inputReader != null && _inputReader.enabled)
                Debug.LogWarning($"[{nameof(NetworkPlayerController)}] PlayerInputReader on '{name}' should be DISABLED in the prefab. See script header.", this);

            if (_stateMachine != null && _stateMachine.enabled)
                Debug.LogWarning($"[{nameof(NetworkPlayerController)}] MovementStateMachine on '{name}' should be DISABLED in the prefab. See script header.", this);

            if (_cameraRoot != null && _cameraRoot.activeSelf)
                Debug.LogWarning($"[{nameof(NetworkPlayerController)}] Camera root on '{name}' should be INACTIVE in the prefab. See script header.", this);
        }
#endif
    }
}
