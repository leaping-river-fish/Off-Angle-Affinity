// =============================================================================
// PlayerStatusEffects — server-side entry point for debuffs that must reach
// systems only the OWNER's client actually runs (movement, camera), the same
// authority problem SolarAscensionEffect solves for its own ultimate. Always
// present on the Player prefab, completely inert until a payload calls one
// of the ServerApply.../ServerClear... methods - same "always present,
// usually idle" shape as SolarAscensionEffect/PlayerGrapple.
//
// WHY THIS EXISTS:
//   GroundEffectPayload.OnEnter/OnExit run server-only and receive an
//   AffinityRuntimeContext for whichever player entered/left the zone -
//   which may be any player, not just the zone's owner. A plain
//   ScriptableObject payload cannot send an RPC itself (that requires a
//   NetworkBehaviour), and MovementStateMachine/PlayerCameraController only
//   do anything meaningful on the occupant's OWN client (see their headers -
//   movement is owner-authoritative, PlayerCameraController is disabled on
//   remote instances). This component is the seam: a payload calls a
//   Server... method here, and this reaches the occupant's own client via
//   TargetRpc, exactly like SolarAscensionEffect.TargetRpcBeginAscensionMovement.
//
// STACKING:
//   Counted, not booleaned, so a player standing in two overlapping zones
//   (e.g. two Hadal Zone throws) doesn't get un-grounded the instant they
//   leave the first one while still standing in the second.
//
// MUFFLED / SOUND:
//   No sound system exists in this project yet, so there is nothing to hook
//   here for "muffled" - see HadalZoneGroundEffectPayload's own note. Add a
//   ServerApplyMuffled/ServerClearMuffled pair here (mirroring the two
//   below) once a sound system exists.
//
// MANUAL SETUP:
//   Add to the Player prefab root, alongside PlayerAffinity/PlayerUltimate.
//   Every reference auto-resolves - _cameraController via
//   GetComponentInChildren since PlayerCameraController lives on the camera
//   child, not the player root.
// =============================================================================

using FishNet.Connection;
using FishNet.Object;
using OffAngle.Movement;
using OffAngle.Player;
using UnityEngine;

namespace OffAngle.Combat
{
    public class PlayerStatusEffects : NetworkBehaviour
    {
        [Header("References (leave null to auto-resolve)")]
        [SerializeField] private MovementStateMachine _movement;
        [Tooltip("Lives on the camera child GameObject, not this root - auto-resolved via GetComponentInChildren.")]
        [SerializeField] private PlayerCameraController _cameraController;

        // Server-only. Ref-counted so overlapping zone stays don't clear the
        // debuff early - see this file's STACKING note.
        private int _groundedStacks;
        private int _nearsightedStacks;

        private void Awake()
        {
            if (_movement == null) _movement = GetComponent<MovementStateMachine>();

            // includeInactive: true is required here - the camera subtree
            // starts SetActive(false) in the prefab and is only activated
            // later, for the local owner, by NetworkPlayerController.
            // ActivateOwnerComponents() (during OnStartClient, which runs
            // AFTER this Awake). Without it, this always resolves to null
            // since PlayerStatusEffects lives on the always-active root and
            // therefore runs its own Awake before the camera ever turns on.
            if (_cameraController == null) _cameraController = GetComponentInChildren<PlayerCameraController>(true);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            // Runs inside FishNet's despawn broadcast - contain any exception
            // rather than letting it escape into the transport. Nothing to
            // revert client-side (the connection is going away with the
            // object), this just resets server bookkeeping defensively.
            try
            {
                _groundedStacks = 0;
                _nearsightedStacks = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        // ------------------------------------------------------------------
        // Grounded (anti-air)
        // ------------------------------------------------------------------

        /// <summary>Server-only. Applies the full "grounded" debuff on this player's own client - see MovementStateContext.GroundedLocked for the exact list of what it blocks. Pair with ServerClearGrounded.</summary>
        public void ServerApplyGrounded()
        {
            if (!IsServerInitialized) return;

            _groundedStacks++;
            if (_groundedStacks == 1)
                TargetRpcSetGrounded(base.Owner, true);
        }

        /// <summary>Server-only. Reverses one ServerApplyGrounded call.</summary>
        public void ServerClearGrounded()
        {
            if (!IsServerInitialized || _groundedStacks <= 0) return;

            _groundedStacks--;
            if (_groundedStacks == 0)
                TargetRpcSetGrounded(base.Owner, false);
        }

        [TargetRpc]
        private void TargetRpcSetGrounded(NetworkConnection conn, bool locked)
        {
            _movement?.SetGroundedLocked(locked);
        }

        // ------------------------------------------------------------------
        // Nearsighted (narrowed FOV)
        // ------------------------------------------------------------------

        /// <summary>Server-only. Eases this player's own FOV down to <paramref name="fov"/> degrees. Pair with ServerClearNearsighted.</summary>
        public void ServerApplyNearsighted(float fov)
        {
            if (!IsServerInitialized) return;

            _nearsightedStacks++;
            if (_nearsightedStacks == 1)
                TargetRpcSetNearsighted(base.Owner, true, fov);
        }

        /// <summary>Server-only. Reverses one ServerApplyNearsighted call.</summary>
        public void ServerClearNearsighted()
        {
            if (!IsServerInitialized || _nearsightedStacks <= 0) return;

            _nearsightedStacks--;
            if (_nearsightedStacks == 0)
                TargetRpcSetNearsighted(base.Owner, false, 0f);
        }

        [TargetRpc]
        private void TargetRpcSetNearsighted(NetworkConnection conn, bool active, float fov)
        {
            if (_cameraController == null) return;
            _cameraController.SetFovTarget(active ? fov : _cameraController.DefaultFov);
        }
    }
}
