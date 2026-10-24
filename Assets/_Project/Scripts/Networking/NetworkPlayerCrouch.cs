// =============================================================================
// NetworkPlayerCrouch — drives the third-person crouch visual and the
// non-owner capsule so every peer sees the same crouching player consistently.
//
// NO NETWORKING OF ITS OWN:
//   Crouch state is the Crouched bit of PlayerAnimFlags, carried by
//   NetworkPlayerLocomotionSync (owner composes it from
//   MovementStateMachine.CrouchAmount >= 0.5, remotes receive it). This
//   component only reads that flag, so owner and remotes agree on crouch-jump
//   and stand-up. It owns visuals only: Third Person Body scale and the
//   non-owner CharacterController height.
//
// VISUAL / HITBOX:
//   "Third Person Body" (parent of the visual Capsule mesh+collider and the
//   Head hitbox SphereCollider) is hidden from the owner's own camera by
//   PlayerVisibility, but its colliders are always live for hit detection on
//   every peer. Scaling ONLY that parent transform's Y shrinks the capsule
//   collider height AND moves the head hitbox down proportionally with zero
//   per-child math - "the capsule just goes down," no animation required.
//   (SphereCollider radius is driven by the MAX absolute scale axis, so the
//   Head hitbox's radius is unaffected by a Y-only scale.)
//
// ONE CODE PATH FOR ALL PEERS:
//   Owner, remotes, and the server all run the same Update() loop that lerps
//   toward the Crouched flag's target scale.
//
// THE CHARACTERCONTROLLER GHOST-HITBOX PROBLEM:
//   NetworkPlayerController only enables MovementStateMachine for the owner
//   (see its header) - every other peer, including the SERVER, never runs
//   CrouchingState/ApplyCrouchHeight for this player at all. Left alone, that
//   means the CharacterController on every non-owner copy of this GameObject
//   sits frozen at its prefab-authored standing height/center forever, no
//   matter what the player is actually doing. Since PlayerWeaponController's
//   damage-resolving raycast runs on the SERVER, this created a full-height
//   invisible "ghost" hitbox hovering over every crouched player - shots
//   could land where the visible (correctly shrunk) body clearly isn't.
//   Fix: this component also drives the CharacterController's height/center
//   directly, using the exact same Crouched flag - but ONLY when !IsOwner, so
//   it never fights the owner's own precise, headroom-gated local simulation.
// =============================================================================

using FishNet.Object;
using OffAngle.Player;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkPlayerCrouch : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Source of the Crouched flag. Leave null to auto-find on this GameObject.")]
        [SerializeField] private NetworkPlayerLocomotionSync _locomotionSync;
        [Tooltip("The \"Third Person Body\" transform - parent of the visual Capsule and Head hitbox.")]
        [SerializeField] private Transform _thirdPersonBody;
        [Tooltip("CharacterController on this GameObject. Only resized here when !IsOwner - see the CHARACTERCONTROLLER GHOST-HITBOX PROBLEM note above.")]
        [SerializeField] private CharacterController _characterController;

        [Header("Tuning")]
        [Tooltip("Seconds to fully lerp the third-person capsule scale. Keep equal to MovementSettings.CrouchTransitionDuration so the owner's local capsule and the third-person capsule move in step.")]
        [SerializeField] private float _transitionDuration = 0.15f;
        [Tooltip("Y scale applied to Third Person Body while fully crouched. Should equal MovementSettings.CrouchHeight / standing CharacterController height.")]
        [SerializeField, Range(0.1f, 1f)] private float _crouchScaleY = 0.56f;
        [Tooltip("CharacterController height while fully crouched on non-owner peers. Must equal MovementSettings.CrouchHeight so the server's hitbox matches the owner's real one.")]
        [SerializeField] private float _crouchControllerHeight = 1f;

        private float _currentScaleY = 1f;

        // Set by SolarAscensionEffect while its own scale-up owns this same
        // transform. Crouch's lerp state keeps advancing underneath so scale
        // resumes smoothly (no pop) once the override is released, but
        // ApplyScale() itself is skipped so it can't stomp the ascension
        // scale every frame - see the bug this was written to fix: ascension
        // set the scale via RPC and this component's next Update() silently
        // overwrote it back to ~1 before anyone saw it.
        private bool _ascensionOverrideActive;

        // Captured once at Awake, before anything can modify the controller -
        // mirrors PlayerController.Awake()'s StandingHeight/StandingCenter
        // capture so both stay in agreement about what "standing" means.
        private float   _standingControllerHeight;
        private float   _currentControllerHeight;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_locomotionSync == null)
                _locomotionSync = GetComponent<NetworkPlayerLocomotionSync>();
            if (_characterController == null)
                _characterController = GetComponent<CharacterController>();

            if (_characterController != null)
            {
                _standingControllerHeight = _characterController.height;
                _currentControllerHeight  = _standingControllerHeight;
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Seed the visual immediately at the current synced value so a
            // late-joining observer does not see a standing player pop into
            // a crouch (or vice versa) on the first frame.
            bool crouched = IsCrouched();
            _currentScaleY = crouched ? _crouchScaleY : 1f;
            ApplyScale();

            _currentControllerHeight = crouched ? _crouchControllerHeight : _standingControllerHeight;
            if (!base.IsOwner)
                ApplyControllerHeight();
        }

        private bool IsCrouched()
        {
            return _locomotionSync != null
                && (_locomotionSync.Flags & PlayerAnimFlags.Crouched) != 0;
        }

        private void Update()
        {
            bool crouched = IsCrouched();
            float rate = 1f / Mathf.Max(0.01f, _transitionDuration);

            float scaleTarget = crouched ? _crouchScaleY : 1f;
            _currentScaleY = Mathf.MoveTowards(_currentScaleY, scaleTarget, rate * Time.deltaTime);
            if (!_ascensionOverrideActive)
                ApplyScale();

            // The owner's own MovementStateMachine already drives its real
            // CharacterController precisely (and headroom-gated) every Tick -
            // applying our own lerp on top of that would fight it and could
            // desync it from the (correctly gated) CrouchAmount. Every other
            // peer has no such simulation running at all, so without this
            // their CharacterController would sit frozen at standing height
            // forever - see the CHARACTERCONTROLLER GHOST-HITBOX PROBLEM note.
            if (!base.IsOwner)
            {
                float heightTarget = crouched ? _crouchControllerHeight : _standingControllerHeight;
                _currentControllerHeight = Mathf.MoveTowards(_currentControllerHeight, heightTarget, rate * Time.deltaTime);
                ApplyControllerHeight();
            }
        }

        // temporary method to apply the scale to the third person body
        private void ApplyScale()
        {
            if (_thirdPersonBody == null) return;
            _thirdPersonBody.localScale = new Vector3(1f, _currentScaleY, 1f);
        }

        /// <summary>
        /// Called by SolarAscensionEffect (on every peer that applies the
        /// ascension scale) to claim or release exclusive control of
        /// _thirdPersonBody's scale. Runs identically on owner, remotes, and
        /// the server, so it never leaves crouch and ascension disagreeing
        /// about which one owns the transform on a given peer.
        /// </summary>
        public void SetScaleOverrideActive(bool active) => _ascensionOverrideActive = active;

        private void ApplyControllerHeight()
        {
            if (_characterController == null) return;
            _characterController.height = _currentControllerHeight;
            _characterController.center = new Vector3(0f, _currentControllerHeight * 0.5f, 0f);
        }
    }
}
