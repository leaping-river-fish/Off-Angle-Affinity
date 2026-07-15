// =============================================================================
// PlayerController — composition root for the player prefab.
//
// This MonoBehaviour contains ZERO gameplay logic. Its only responsibilities:
//   1. Fetch required components via GetComponent
//   2. Build and populate MovementStateContext
//   3. Call MovementStateMachine.Initialize(ctx)
//
// Any gameplay code placed here is a design error. Route it to a state class.
//
// ─────────────────────────────────────────────────────────────────────────────
// INSPECTOR SETUP CHECKLIST
// ─────────────────────────────────────────────────────────────────────────────
//   [ ] PlayerInputReader._actionAsset  ← drag PlayerInputActions.inputactions
//   [ ] PlayerCameraController is on the Camera child GameObject
//   [ ] PlayerCameraController._playerRoot ← leave null (auto-finds parent)
//   [ ] PlayerCameraController._inputReader ← leave null (auto-finds via parent)
//   [ ] Tune Movement Settings values below in the Inspector
//
// ─────────────────────────────────────────────────────────────────────────────
// CHARACTERCONTROLLER RECOMMENDED SETTINGS
// ─────────────────────────────────────────────────────────────────────────────
//   World scale: 1 Unity unit = 1 meter. See Assets/_Project/Docs/PlayerScaleReference.md
//   for the full set of baseline player metrics.
//
//   Slope Limit      45
//   Step Offset       0.3
//   Skin Width        0.08
//   Min Move Distance 0       ← prevents micro-stutter at low speeds
//   Center           (0, 0.9, 0) for a 1.8 m capsule with the root at the feet
//   Radius            0.5
//   Height            1.8
//
// ─────────────────────────────────────────────────────────────────────────────
// MULTIPLAYER INTEGRATION HOOK
// ─────────────────────────────────────────────────────────────────────────────
//   When adding NGO or Mirror, override OnNetworkSpawn() (or the equivalent)
//   and gate input on ownership:
//
//     public override void OnNetworkSpawn()
//     {
//         _inputReader.enabled = IsOwner;
//         GetComponentInChildren<PlayerCameraController>().enabled = IsOwner;
//     }
//
//   Remote players receive ctx.Velocity and StateId via NetworkVariables
//   and are animated/moved by a separate NetworkPlayerVisual component.
// =============================================================================

using UnityEngine;
using OffAngle.Core;
using OffAngle.Movement;

namespace OffAngle.Player
{
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(PlayerInputReader))]
    [RequireComponent(typeof(MovementStateMachine))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private MovementSettings _movementSettings = new MovementSettings();

        private PlayerInputReader    _inputReader;
        private MovementStateMachine _stateMachine;
        private CharacterController  _characterController;

        private void Awake()
        {
            _inputReader         = GetComponent<PlayerInputReader>();
            _stateMachine        = GetComponent<MovementStateMachine>();
            _characterController = GetComponent<CharacterController>();

            var ctx = new MovementStateContext
            {
                Controller      = _characterController,
                Input           = _inputReader,
                PlayerTransform = transform,
                StateMachine    = _stateMachine,
                Settings        = _movementSettings,
            };

            // Capture the Inspector-authored CharacterController dimensions as
            // "standing" BEFORE any crouch logic can run. This makes the
            // CharacterController's own values the single source of truth for
            // standing height/center - CrouchingState never hardcodes them.
            ctx.StandingHeight = _characterController.height;
            ctx.StandingCenter = _characterController.center;

            _stateMachine.Initialize(ctx);
        }
    }
}
