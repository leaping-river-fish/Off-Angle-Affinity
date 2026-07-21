// =============================================================================
// PlayerInputReader — the single point of contact with UnityEngine.InputSystem.
//
// ARCHITECTURE NOTE:
// No other script in this project should import UnityEngine.InputSystem.
// All input flows through the C# events exposed here. This means:
//   - Switching input backends (e.g. rebinding UI, cloud gaming) only ever
//     touches this one file.
//   - For multiplayer (NGO/Mirror): gate OnEnable/OnDisable on IsOwner.
//     Remote players call _inputReader.enabled = false so their input does
//     not drive local simulation.
//
// MOVEMENT PHILOSOPHY (read before adding states):
// This system is built around chained momentum. Every movement ability either
// generates momentum, transforms existing momentum into a new form, or
// intentionally consumes it. The input reader fires raw events; the active
// MovementState decides what to do with them. A single input (CrouchSlide)
// can produce different state transitions depending on current velocity —
// the state machine decides, not the input layer.
// =============================================================================

using System;
using UnityEngine;
using UnityEngine.InputSystem;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace OffAngle.Core
{
    public class PlayerInputReader : MonoBehaviour
    {
        [SerializeField] private InputActionAsset _actionAsset;

        // ------------------------------------------------------------------
        // Public events
        // All downstream systems subscribe here. Nothing outside this class
        // should ever read from an InputAction directly.
        // ------------------------------------------------------------------

        public event Action<Vector2> MoveEvent;
        public event Action<Vector2> LookEvent;
        public event Action          JumpStarted;

        // SprintChanged: true = key pressed, false = key released.
        // States that need to preserve sprint momentum across transitions
        // should poll IsSprinting rather than subscribing to SprintChanged.
        public event Action<bool>    SprintChanged;

        public event Action FireStarted;
        public event Action FireCanceled;
        public event Action ReloadStarted;

        // Grapple is hold-to-use: GrappleStarted fires on press,
        // GrappleCanceled fires on release. GrapplingState uses both.
        public event Action GrappleStarted;
        public event Action GrappleCanceled;

        // CrouchSlide fires a single event on press. The active GroundedState
        // resolves whether to enter CrouchingState or SlidingState based on
        // ctx.Velocity.magnitude vs Settings.SlideEntrySpeedThreshold.
        // The input layer is intentionally kept ignorant of this distinction.
        public event Action CrouchSlideStarted;
        public event Action CrouchSlideCanceled;

        public event Action          InteractStarted;
        public event Action<float>   SwitchWeaponEvent;
        public event Action          OpenLoadoutMenuStarted;

        // ------------------------------------------------------------------
        // Polled properties — cached for states that need current values
        // every Tick without subscribing to individual events
        // ------------------------------------------------------------------

        public Vector2 MoveInput   { get; private set; }
        public bool    IsSprinting { get; private set; }

        // ------------------------------------------------------------------
        // Private action references
        // ------------------------------------------------------------------

        private InputAction _move;
        private InputAction _look;
        private InputAction _jump;
        private InputAction _sprint;
        private InputAction _fire;
        private InputAction _reload;
        private InputAction _grapple;
        private InputAction _crouchSlide;
        private InputAction _interact;
        private InputAction _switchWeapon;
        private InputAction _openLoadoutMenu;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            ResolveActionAsset();

            var map = _actionAsset.FindActionMap("Player", throwIfNotFound: true);

            _move         = map.FindAction("Move",         throwIfNotFound: true);
            _look         = map.FindAction("Look",         throwIfNotFound: true);
            _jump         = map.FindAction("Jump",         throwIfNotFound: true);
            _sprint       = map.FindAction("Sprint",       throwIfNotFound: true);
            _fire         = map.FindAction("Fire",         throwIfNotFound: true);
            _reload       = map.FindAction("Reload",       throwIfNotFound: true);
            _grapple      = map.FindAction("Grapple",      throwIfNotFound: true);
            _crouchSlide  = map.FindAction("CrouchSlide",  throwIfNotFound: true);
            _interact     = map.FindAction("Interact",     throwIfNotFound: true);
            _switchWeapon = map.FindAction("SwitchWeapon", throwIfNotFound: true);
            _openLoadoutMenu = map.FindAction("WeaponMenu", throwIfNotFound: true);
        }

        private void OnEnable()
        {
            _actionAsset.Enable();

            _move.performed         += OnMove;
            _move.canceled          += OnMove;
            _look.performed         += OnLook;
            _jump.performed         += OnJump;
            _sprint.performed       += OnSprintPerformed;
            _sprint.canceled        += OnSprintCanceled;
            _fire.performed         += OnFire;
            _fire.canceled          += OnFireCanceled;
            _reload.performed       += OnReload;
            _grapple.performed      += OnGrapplePerformed;
            _grapple.canceled       += OnGrappleCanceled;
            _crouchSlide.performed  += OnCrouchSlidePerformed;
            _crouchSlide.canceled   += OnCrouchSlideCanceled;
            _interact.performed     += OnInteract;
            _switchWeapon.performed += OnSwitchWeapon;
            _openLoadoutMenu.performed += OnOpenLoadoutMenu;
        }

        private void OnDisable()
        {
            // Awake resolves every action together, so a null _move means
            // the others are also null (component disabled before Awake ran).
            if (_move == null) return;

            _move.performed         -= OnMove;
            _move.canceled          -= OnMove;
            _look.performed         -= OnLook;
            _jump.performed         -= OnJump;
            _sprint.performed       -= OnSprintPerformed;
            _sprint.canceled        -= OnSprintCanceled;
            _fire.performed         -= OnFire;
            _fire.canceled          -= OnFireCanceled;
            _reload.performed       -= OnReload;
            _grapple.performed      -= OnGrapplePerformed;
            _grapple.canceled       -= OnGrappleCanceled;
            _crouchSlide.performed  -= OnCrouchSlidePerformed;
            _crouchSlide.canceled   -= OnCrouchSlideCanceled;
            _interact.performed     -= OnInteract;
            _switchWeapon.performed -= OnSwitchWeapon;
            _openLoadoutMenu.performed -= OnOpenLoadoutMenu;

            _actionAsset?.Disable();
        }

        // ------------------------------------------------------------------
        // Callbacks
        // ------------------------------------------------------------------

        private void OnMove(InputAction.CallbackContext ctx)
        {
            MoveInput = ctx.ReadValue<Vector2>();
            MoveEvent?.Invoke(MoveInput);
        }

        private void OnLook(InputAction.CallbackContext ctx)
            => LookEvent?.Invoke(ctx.ReadValue<Vector2>());

        private void OnJump(InputAction.CallbackContext ctx)
            => JumpStarted?.Invoke();

        private void OnSprintPerformed(InputAction.CallbackContext ctx)
        {
            IsSprinting = true;
            SprintChanged?.Invoke(true);
        }

        private void OnSprintCanceled(InputAction.CallbackContext ctx)
        {
            IsSprinting = false;
            SprintChanged?.Invoke(false);
        }

        private void OnFire(InputAction.CallbackContext ctx)
            => FireStarted?.Invoke();

        private void OnFireCanceled(InputAction.CallbackContext ctx)
            => FireCanceled?.Invoke();

        private void OnReload(InputAction.CallbackContext ctx)
            => ReloadStarted?.Invoke();

        private void OnGrapplePerformed(InputAction.CallbackContext ctx)
            => GrappleStarted?.Invoke();

        private void OnGrappleCanceled(InputAction.CallbackContext ctx)
            => GrappleCanceled?.Invoke();

        private void OnCrouchSlidePerformed(InputAction.CallbackContext ctx)
            => CrouchSlideStarted?.Invoke();

        private void OnCrouchSlideCanceled(InputAction.CallbackContext ctx)
            => CrouchSlideCanceled?.Invoke();

        private void OnInteract(InputAction.CallbackContext ctx)
            => InteractStarted?.Invoke();

        private void OnSwitchWeapon(InputAction.CallbackContext ctx)
            => SwitchWeaponEvent?.Invoke(ctx.ReadValue<float>());

        private void OnOpenLoadoutMenu(InputAction.CallbackContext ctx)
            => OpenLoadoutMenuStarted?.Invoke();

        private void ResolveActionAsset()
        {
#if UNITY_EDITOR
            if (_actionAsset != null && _actionAsset.name == "PlayerInputActions")
                return;

            var loaded = AssetDatabase.LoadAssetAtPath<InputActionAsset>(
                "Assets/_Project/Input/PlayerInputActions.inputactions");
            if (loaded == null)
                return;

            _actionAsset = loaded;
#endif
        }
    }
}
