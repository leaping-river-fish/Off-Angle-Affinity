// =============================================================================
// NetworkPlayerLocomotionSync — visual-only replication of locomotion flags
// that remote third-person animation needs but cannot derive on its own.
//
// AUTHORITY MODEL:
//   Same owner-writable SyncVar pattern as NetworkPlayerCrouch/NetworkPlayerAim:
//     WritePermission.ClientUnsynchronized + ReadPermission.ExcludeOwner.
//   The owner already knows its own state via MovementStateMachine; remotes have
//   that component disabled. The owner sends a value only when it changes.
//
// THIS IS NOT GAMEPLAY:
//   Nothing here feeds back into movement. It only drives the Animator's
//   IsSprinting bool on remote copies (via ISprintStateOverride).
//
// Add further visual-only flags here (slide, wall-run, ...) rather than adding
// another NetworkBehaviour to the player prefab.
// =============================================================================

using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Movement;
using OffAngle.Player;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkPlayerLocomotionSync : NetworkBehaviour, ISprintStateOverride
    {
        private readonly SyncVar<bool> _isSprinting =
            new SyncVar<bool>(new SyncTypeSettings(WritePermission.ClientUnsynchronized, ReadPermission.ExcludeOwner));

        private MovementStateMachine _stateMachine;
        private bool _clientStarted;
        private bool _isOwnerVisual;
        private bool _lastSentSprinting;

        private void Awake()
        {
            _stateMachine = GetComponent<MovementStateMachine>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            _clientStarted = true;
            _isOwnerVisual = base.IsOwner;
            _lastSentSprinting = _isSprinting.Value;
        }

        public override void OnOwnershipClient(FishNet.Connection.NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            _isOwnerVisual = base.IsOwner;
        }

        private void Update()
        {
            if (!_clientStarted || !base.IsOwner || !base.IsSpawned || _stateMachine == null)
                return;

            bool sprinting = _stateMachine.IsSprinting;
            if (sprinting == _lastSentSprinting)
                return;

            _lastSentSprinting = sprinting;
            SetSprinting(sprinting);
        }

        public bool TryGetSprinting(out bool sprinting)
        {
            if (!_clientStarted || _isOwnerVisual)
            {
                sprinting = false;
                return false;
            }

            sprinting = _isSprinting.Value;
            return true;
        }

        [ServerRpc(RunLocally = true)]
        private void SetSprinting(bool value)
        {
            _isSprinting.Value = value;
        }
    }
}
