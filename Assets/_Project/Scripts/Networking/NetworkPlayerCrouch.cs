// =============================================================================
// NetworkPlayerCrouch — replicates crouch state and drives the third-person
// capsule so every peer sees the same crouching player consistently.
//
// AUTHORITY MODEL:
//   Movement on this prefab is already client-authoritative (NetworkTransform
//   with _clientAuthoritative: 1, no Prediction V2 - see NetworkPlayerController
//   and MovementStateMachine headers). Crouch sync follows the same model
//   rather than introducing a server round-trip: _isCrouching is an
//   owner-writable SyncVar (WritePermission.ClientUnsynchronized), replicated
//   to everyone except the owner (ReadPermission.ExcludeOwner - the owner
//   already knows their own state via MovementStateMachine.IsCrouching).
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
//   toward the synced bool's target scale. The owner also independently polls
//   MovementStateMachine.IsCrouching to know when to push a new value - but
//   the resulting visual scale is applied identically everywhere.
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
//   directly, using the exact same synced bool - but ONLY when !IsOwner, so
//   it never fights the owner's own precise, headroom-gated local simulation.
// =============================================================================

using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Movement;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkPlayerCrouch : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private MovementStateMachine _stateMachine;
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

        private readonly SyncVar<bool> _isCrouching =
            new SyncVar<bool>(new SyncTypeSettings(WritePermission.ClientUnsynchronized, ReadPermission.ExcludeOwner));

        private bool  _lastSentValue;
        private float _currentScaleY = 1f;

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
            if (_stateMachine == null)
                _stateMachine = GetComponent<MovementStateMachine>();
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
            _currentScaleY = _isCrouching.Value ? _crouchScaleY : 1f;
            ApplyScale();

            _currentControllerHeight = _isCrouching.Value ? _crouchControllerHeight : _standingControllerHeight;
            if (!base.IsOwner)
                ApplyControllerHeight();

            _lastSentValue = _isCrouching.Value;
        }

        private void Update()
        {
            if (base.IsOwner && _stateMachine != null)
            {
                bool crouching = _stateMachine.IsCrouching;
                if (crouching != _lastSentValue)
                {
                    _lastSentValue = crouching;
                    SetCrouching(crouching);
                }
            }

            float rate = 1f / Mathf.Max(0.01f, _transitionDuration);

            float scaleTarget = _isCrouching.Value ? _crouchScaleY : 1f;
            _currentScaleY = Mathf.MoveTowards(_currentScaleY, scaleTarget, rate * Time.deltaTime);
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
                float heightTarget = _isCrouching.Value ? _crouchControllerHeight : _standingControllerHeight;
                _currentControllerHeight = Mathf.MoveTowards(_currentControllerHeight, heightTarget, rate * Time.deltaTime);
                ApplyControllerHeight();
            }
        }

        private void ApplyScale()
        {
            if (_thirdPersonBody == null) return;
            _thirdPersonBody.localScale = new Vector3(1f, _currentScaleY, 1f);
        }

        private void ApplyControllerHeight()
        {
            if (_characterController == null) return;
            _characterController.height = _currentControllerHeight;
            _characterController.center = new Vector3(0f, _currentControllerHeight * 0.5f, 0f);
        }

        // ------------------------------------------------------------------
        // Owner → everyone
        // ------------------------------------------------------------------

        [ServerRpc(RunLocally = true)]
        private void SetCrouching(bool value)
        {
            _isCrouching.Value = value;
        }
    }
}
