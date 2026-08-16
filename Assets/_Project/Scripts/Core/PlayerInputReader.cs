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

        // AimChanged: true = key pressed, false = key released.
        // WeaponAdsController subscribes here to enter/exit ADS.
        public event Action<bool>    AimChanged;

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
        // Category Id string matching WeaponCategory.Id (e.g. "Primary", "Sidearm").
        public event Action<string>  SelectWeaponCategoryEvent;
        public event Action          OpenLoadoutMenuStarted;
        public event Action          PauseToggleStarted;

        // ------------------------------------------------------------------
        // Polled properties — cached for states that need current values
        // every Tick without subscribing to individual events
        // ------------------------------------------------------------------

        public Vector2 MoveInput   { get; private set; }
        public bool    IsSprinting { get; private set; }
        public bool    IsAiming    { get; private set; }

        // ------------------------------------------------------------------
        // Private action references
        // ------------------------------------------------------------------

        private InputAction _move;
        private InputAction _look;
        private InputAction _jump;
        private InputAction _sprint;
        private InputAction _aim;
        private InputAction _fire;
        private InputAction _reload;
        private InputAction _grapple;
        private InputAction _crouchSlide;
        private InputAction _interact;
        private InputAction _switchWeapon;
        private InputAction _primary;
        private InputAction _sidearm;
        private InputAction _openLoadoutMenu;
        private InputAction _pause;

        // UI map actions
        private InputAction _uiCloseMenu;

        private InputActionMap _playerMap;
        private InputActionMap _uiMap;

        // True once Awake has swapped _actionAsset for a runtime clone, so
        // OnDestroy knows it is safe to destroy it. Guards against ever
        // destroying the shared project asset.
        private bool _ownsActionAsset;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            ResolveActionAsset();

            if (_actionAsset == null)
            {
                // ResolveActionAsset() is editor-only, so in a build this means
                // the Player prefab's field was never assigned. Fail loudly here
                // rather than as a bare NullReferenceException on the next line.
                Debug.LogError(
                    $"[{nameof(PlayerInputReader)}] No InputActionAsset assigned. " +
                    "Assign PlayerInputActions on the Player prefab - the editor-only auto-resolve masks this until you build.",
                    this);
                enabled = false;
                return;
            }

            // Per-instance clone. EVERY player object carries one of these -
            // including remote players' avatars on every peer - and they all
            // pointed at the same shared project asset. Any one of them
            // disabling it disabled input for the local player too: on despawn
            // via OnDestroy, and on spawn via NetworkPlayerController turning a
            // non-owner's reader off (which runs OnDisable -> DisableAllMaps).
            // That is what made every remaining peer lose movement, look, fire
            // and pause the moment somebody else left the match.
            //
            // Cloning makes enable/disable purely local, which fixes the whole
            // family of cases rather than the one call site that was noticed.
            _actionAsset = Instantiate(_actionAsset);
            _ownsActionAsset = true;

            _playerMap = _actionAsset.FindActionMap("Player", throwIfNotFound: true);
            _uiMap = _actionAsset.FindActionMap("UI", throwIfNotFound: true);

            var map = _playerMap;

            _move         = map.FindAction("Move",         throwIfNotFound: true);
            _look         = map.FindAction("Look",         throwIfNotFound: true);
            _jump         = map.FindAction("Jump",         throwIfNotFound: true);
            _sprint       = map.FindAction("Sprint",       throwIfNotFound: true);
            _aim          = map.FindAction("Aim",          throwIfNotFound: true);
            _fire         = map.FindAction("Fire",         throwIfNotFound: true);
            _reload       = map.FindAction("Reload",       throwIfNotFound: true);
            _grapple      = map.FindAction("Grapple",      throwIfNotFound: true);
            _crouchSlide  = map.FindAction("CrouchSlide",  throwIfNotFound: true);
            _interact     = map.FindAction("Interact",     throwIfNotFound: true);
            _switchWeapon = map.FindAction("SwitchWeapon", throwIfNotFound: true);
            _primary      = map.FindAction("Primary",      throwIfNotFound: true);
            _sidearm      = map.FindAction("Sidearm",      throwIfNotFound: true);
            _openLoadoutMenu = map.FindAction("WeaponMenu", throwIfNotFound: true);
            _pause           = map.FindAction("Pause",      throwIfNotFound: true);

            // UI map actions
            var uiMapActions = _uiMap;
            // Try to find WeaponMenu action in UI map (P key to close menu)
            _uiCloseMenu = uiMapActions.FindAction("WeaponMenu", throwIfNotFound: false);
            if (_uiCloseMenu == null)
            {
                // Fall back to Cancel (Escape)
                _uiCloseMenu = uiMapActions.FindAction("Cancel", throwIfNotFound: false);
            }
        }

        private void OnEnable()
        {
            // Enable only the Player map by default. PlayerInputStateController
            // manages which map should be active based on the current state
            // (Gameplay vs Menu vs Dead). The UI map is enabled separately when
            // entering Menu state.
            EnablePlayerMap();

            _move.performed         += OnMove;
            _move.canceled          += OnMove;
            _look.performed         += OnLook;
            _jump.performed         += OnJump;
            _sprint.performed       += OnSprintPerformed;
            _sprint.canceled        += OnSprintCanceled;
            _aim.performed          += OnAimPerformed;
            _aim.canceled           += OnAimCanceled;
            _fire.performed         += OnFire;
            _fire.canceled          += OnFireCanceled;
            _reload.performed       += OnReload;
            _grapple.performed      += OnGrapplePerformed;
            _grapple.canceled       += OnGrappleCanceled;
            _crouchSlide.performed  += OnCrouchSlidePerformed;
            _crouchSlide.canceled   += OnCrouchSlideCanceled;
            _interact.performed     += OnInteract;
            _switchWeapon.performed += OnSwitchWeapon;
            _primary.performed      += OnSelectWeaponCategory;
            _sidearm.performed      += OnSelectWeaponCategory;
            _openLoadoutMenu.performed += OnOpenLoadoutMenu;
            _pause.performed        += OnPauseToggle;

            // UI map close menu (if it exists)
            if (_uiCloseMenu != null)
                _uiCloseMenu.performed += OnOpenLoadoutMenu; // Same event - toggle works in both states
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
            _aim.performed          -= OnAimPerformed;
            _aim.canceled           -= OnAimCanceled;
            _fire.performed         -= OnFire;
            _fire.canceled          -= OnFireCanceled;
            _reload.performed       -= OnReload;
            _grapple.performed      -= OnGrapplePerformed;
            _grapple.canceled       -= OnGrappleCanceled;
            _crouchSlide.performed  -= OnCrouchSlidePerformed;
            _crouchSlide.canceled   -= OnCrouchSlideCanceled;
            _interact.performed     -= OnInteract;
            _switchWeapon.performed -= OnSwitchWeapon;
            _primary.performed      -= OnSelectWeaponCategory;
            _sidearm.performed      -= OnSelectWeaponCategory;
            _openLoadoutMenu.performed -= OnOpenLoadoutMenu;
            _pause.performed        -= OnPauseToggle;

            // UI map close menu (if it exists)
            if (_uiCloseMenu != null)
                _uiCloseMenu.performed -= OnOpenLoadoutMenu;

            DisableAllMaps();
        }

        private void OnDestroy()
        {
            // Safe only because Awake cloned the asset. This used to run against
            // the SHARED project asset with no owner check - and OnDestroy runs
            // even on a disabled component - so destroying any player's avatar
            // killed input for whoever was watching it.
            if (_actionAsset != null && _actionAsset)
            {
                _actionAsset.Disable();

                // The clone is a runtime object; without this we leak one
                // InputActionAsset per player spawned.
                if (_ownsActionAsset)
                    Destroy(_actionAsset);
            }
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

        private void OnAimPerformed(InputAction.CallbackContext ctx)
        {
            IsAiming = true;
            AimChanged?.Invoke(true);
        }

        private void OnAimCanceled(InputAction.CallbackContext ctx)
        {
            IsAiming = false;
            AimChanged?.Invoke(false);
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

        private void OnSelectWeaponCategory(InputAction.CallbackContext ctx)
            => SelectWeaponCategoryEvent?.Invoke(ctx.action.name);

        private void OnOpenLoadoutMenu(InputAction.CallbackContext ctx)
            => OpenLoadoutMenuStarted?.Invoke();

        private void OnPauseToggle(InputAction.CallbackContext ctx)
            => PauseToggleStarted?.Invoke();

        // ------------------------------------------------------------------
        // Action map control (called by PlayerInputStateController)
        // ------------------------------------------------------------------

        /// <summary>Enable the Player action map for gameplay input.</summary>
        public void EnablePlayerMap()
        {
            if (_playerMap == null) return;
            _playerMap.Enable();
        }

        /// <summary>Enable the UI action map for menu navigation input.</summary>
        public void EnableUIMap()
        {
            if (_uiMap == null) return;
            _uiMap.Enable();
        }

        /// <summary>
        /// Disable gameplay actions while keeping WeaponMenu enabled so the same
        /// key can open AND close the menu without requiring a duplicate UI binding.
        /// </summary>
        public void DisablePlayerMap()
        {
            if (_playerMap == null) return;

            // Keep the map enabled; selectively disable gameplay actions.
            // Fully disabling the map would also kill WeaponMenu (P), trapping
            // the player in menu state.
            if (!_playerMap.enabled)
                _playerMap.Enable();

            foreach (InputAction action in _playerMap.actions)
            {
                if (action == _openLoadoutMenu || action == _pause)
                    continue;
                action.Disable();
            }

            _openLoadoutMenu?.Enable();
            _pause?.Enable();
        }

        /// <summary>Disable the UI action map (stops menu navigation input events).</summary>
        public void DisableUIMap()
        {
            if (_uiMap != null && _uiMap.enabled)
                _uiMap.Disable();
        }

        /// <summary>Disable all action maps (used for death state or full input lockout).</summary>
        public void DisableAllMaps()
        {
            if (_playerMap != null)
                _playerMap.Disable();
            DisableUIMap();
        }

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
