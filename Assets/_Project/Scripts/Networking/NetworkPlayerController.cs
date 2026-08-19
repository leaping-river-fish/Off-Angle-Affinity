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
//   On the HUD Canvas CHILD GameObject:
//     - GameObject.activeSelf          = FALSE  (entire HUD subtree off)
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
//   Similarly, HUD elements (including menus) must start inactive to avoid
//   remote players' UI appearing on the local player's screen.
//
// ─────────────────────────────────────────────────────────────────────────────
// LIFECYCLE ORDER (do not change without testing the cursor lock carefully):
// ─────────────────────────────────────────────────────────────────────────────
//   1. Prefab instantiated by PlayerSpawner on server.
//   2. NetworkObject syncs to clients; instance appears on every peer.
//   3. PlayerController.Awake() runs on every peer (gameplay-agnostic init).
//   4. NetworkBehaviour.OnStartClient() runs on every client (this script).
//   5. For IsOwner: input reader enabled, state machine enabled, camera
//      subtree activated, HUD subtree activated. Their OnEnables fire in order.
//   6. For !IsOwner: nothing happens here; everything stays in the off
//      defaults baked into the prefab.
// =============================================================================

using System;
using System.Collections;
using FishNet.Connection;
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

        [Tooltip("Leave null to auto-resolve via GetComponent. See OnStartClient's owner branch for why this needs an explicit re-enable.")]
        [SerializeField] private CharacterController    _characterController;

        [Header("Camera subtree (must be SetActive(false) in the prefab)")]
        [Tooltip("The root GameObject of the player's camera. Activated for the local owner; left inactive for remote players to avoid extra cameras / audio listeners.")]
        [SerializeField] private GameObject             _cameraRoot;

        [Header("HUD subtree (must be SetActive(false) in the prefab)")]
        [Tooltip("The root GameObject of the player's HUD (health, ammo, crosshair, menus). Activated for the local owner; left inactive for remote players.")]
        [SerializeField] private GameObject             _hudRoot;

        private void Awake()
        {
            if (_characterController == null)
                _characterController = GetComponent<CharacterController>();
        }

        // ------------------------------------------------------------------
        // FishNet lifecycle
        // ------------------------------------------------------------------

        // Guards ActivateOwnerComponents() against running twice - once from
        // OnStartClient, and again from OnOwnershipClient if ownership
        // resolves to this connection only after OnStartClient already ran
        // (see OnOwnershipClient below).
        private bool _ownerComponentsActivated;

        private Coroutine _ensureControllerEnabledCoroutine;

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Remote players: bail out. Their components stay in the prefab's
            // disabled defaults, so OnEnable/OnDisable never fires for them.
            if (!base.IsOwner)
                return;

            ActivateOwnerComponents();
        }

        // NetworkTransform's own CharacterController-mode setup (see the
        // player prefab's NetworkTransform, _componentConfiguration) re-runs
        // ConfigureComponents() on its own OnOwnershipClient, not just
        // OnStartClient - a signal that FishNet does not guarantee ownership
        // is fully settled for this connection by the time OnStartClient
        // runs. Mirror that here: if IsOwner only becomes true once this
        // fires (rather than already being true during OnStartClient above),
        // this is the first and only other chance to light up the owner
        // stack - there is no other hook that would ever revisit it.
        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);

            if (base.IsOwner)
                ActivateOwnerComponents();
        }

        private void ActivateOwnerComponents()
        {
            if (_ownerComponentsActivated) return;
            _ownerComponentsActivated = true;

            // Local owner: light up the gameplay stack in a deterministic order.
            // PlayerInputReader.OnEnable must run BEFORE PlayerCameraController.OnEnable
            // (which is inside _cameraRoot.SetActive) so the camera's seed read of
            // _inputReader.LookEvent attaches to a fully-initialised reader.
            //
            // Each step is isolated: an exception thrown while enabling the input
            // reader or state machine (e.g. from a downstream OnEnable) used to
            // abort this method entirely, silently skipping the camera/HUD lines
            // below it and leaving the local player spawned with no active camera
            // and no way to move - "no cameras rendering" with nothing in the
            // Console to explain why. Logging and continuing means the worst case
            // is a broken input feature, never a player stuck unable to see or act.
            TryStep(() => { if (_inputReader != null) _inputReader.enabled = true; }, nameof(_inputReader));
            TryStep(() => { if (_stateMachine != null) _stateMachine.enabled = true; }, nameof(_stateMachine));
            TryStep(() => { if (_cameraRoot != null) _cameraRoot.SetActive(true); }, nameof(_cameraRoot));
            TryStep(() => { if (_hudRoot != null) _hudRoot.SetActive(true); }, nameof(_hudRoot));

            Debug.Log($"[{nameof(NetworkPlayerController)}] {name} owner components activated. cameraRootActive={_cameraRoot != null && _cameraRoot.activeSelf}, stateMachineEnabled={_stateMachine != null && _stateMachine.enabled}");

            if (_ensureControllerEnabledCoroutine == null)
                _ensureControllerEnabledCoroutine = StartCoroutine(EnsureControllerEnabled());
        }

        private void TryStep(Action step, string stepName)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Debug.LogError($"[{nameof(NetworkPlayerController)}] {name} threw while activating '{stepName}' - continuing with the remaining owner components rather than leaving them off.", this);
                Debug.LogException(e, this);
            }
        }

        // Repeatedly (not just once, one frame later) forces the controller
        // back on for a short settling window after spawn. A single deferred
        // frame assumed the ownership race above resolves within exactly one
        // frame, which does not hold for a genuine network connection with
        // real latency - NetworkTransform can call ConfigureComponents() again
        // after our one-shot fix already ran (e.g. its own OnOwnershipClient
        // firing later) and re-disable the controller with nothing left to
        // recover it. Bailing out once _stateMachine.enabled is false stops
        // this from fighting PlayerLifecycleController.SetOwnerGameplayLocked
        // if the player dies while still inside this settling window.
        private IEnumerator EnsureControllerEnabled()
        {
            float deadline = Time.time + 3f;

            while (Time.time < deadline)
            {
                if (_stateMachine != null && !_stateMachine.enabled)
                    break;

                if (_characterController != null)
                    _characterController.enabled = true;

                yield return null;
            }

            _ensureControllerEnabledCoroutine = null;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            // Only the local owner had components enabled; only the local owner
            // needs to tear them down. This runs when the player despawns
            // (e.g. disconnect) so the cursor unlocks cleanly on shutdown.
            if (!base.IsOwner)
                return;

            // Every peer's copy of this player's teardown runs synchronously
            // as part of FishNet's despawn broadcast - an uncaught exception
            // here would propagate back into the network transport instead of
            // just this component, so it's caught and logged rather than left
            // to escape.
            try
            {
                CleanupOwnerComponents();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        private void OnDestroy()
        {
            // Additional cleanup during despawn to prevent null references
            if (base.IsOwner)
            {
                try
                {
                    CleanupOwnerComponents();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }
        }

        private void CleanupOwnerComponents()
        {
            if (_hudRoot != null && _hudRoot)      _hudRoot.SetActive(false);
            if (_cameraRoot != null && _cameraRoot)   _cameraRoot.SetActive(false);
            if (_stateMachine != null && _stateMachine) _stateMachine.enabled = false;
            if (_inputReader != null && _inputReader)  _inputReader.enabled  = false;
        }

        // ------------------------------------------------------------------
        // Temporary diagnostic — remote observers reportedly never see a
        // player's Y position rise during wall-run/grapple. Logs this
        // instance's own transform.position.y every ~0.5s, tagged with
        // whether THIS machine owns it, so a repro's two consoles (the
        // grappling/wall-running player's own log vs. an observer's log for
        // that same NetworkObject) can be compared directly: if the owner's
        // own Y climbs but the observer's copy never does, the sync itself
        // is broken; if neither climbs, the movement code itself isn't
        // producing the Y change on this build. Safe to remove once the
        // root cause is confirmed.
        // ------------------------------------------------------------------
        private float _nextPositionLogTime;

        private void Update()
        {
            if (Time.time < _nextPositionLogTime) return;
            _nextPositionLogTime = Time.time + 0.5f;
            Debug.Log($"[{nameof(NetworkPlayerController)}] {name} owner={base.IsOwner} Y={transform.position.y:F2}");
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
