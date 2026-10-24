// =============================================================================
// NetworkPlayerLocomotionSync — visual-only replication of persistent
// locomotion state that remote third-person animation needs but cannot derive
// on its own.
//
// AUTHORITY MODEL:
//   Same owner-writable SyncVar pattern as NetworkPlayerCrouch/NetworkPlayerAim:
//     WritePermission.ClientUnsynchronized + ReadPermission.ExcludeOwner.
//   The owner already knows its own state via MovementStateMachine; remotes have
//   that component disabled. The owner composes one PlayerAnimFlags byte each
//   frame and sends it only when it changes.
//
// THIS IS NOT GAMEPLAY:
//   Nothing here feeds back into movement. It only carries presentation flags.
//   Consumers read it through IPlayerAnimStateSource (owner and remotes alike).
//
// Add further persistent visual bits to PlayerAnimFlags (widen to ushort past
// 8) rather than adding another NetworkBehaviour to the player prefab.
// =============================================================================

using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Movement;
using OffAngle.Player;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkPlayerLocomotionSync : NetworkBehaviour, IPlayerAnimStateSource
    {
        // CrouchAmount at or above this counts as crouched for presentation.
        private const float CrouchedThreshold = 0.5f;

        private readonly SyncVar<byte> _flags =
            new SyncVar<byte>(new SyncTypeSettings(WritePermission.ClientUnsynchronized, ReadPermission.ExcludeOwner));

        // Owner: composed locally each frame. Remote: mirrors the received value.
        // Serialized only so it can be watched in the Inspector.
        [Header("Current flags (read-only)")]
        [SerializeField] private PlayerAnimFlags _currentFlags;

        /// <summary>
        /// Current flags on any peer: composed locally on the owner, received on
        /// remotes. Remotes read the SyncVar directly (not gated on
        /// _clientStarted) so late-join seeding works regardless of component
        /// start order.
        /// </summary>
        public PlayerAnimFlags Flags =>
            base.IsOwner ? _currentFlags : (PlayerAnimFlags)_flags.Value;

        private MovementStateMachine _stateMachine;
        private bool _clientStarted;
        private byte _lastSent;

        private void Awake()
        {
            _stateMachine = GetComponent<MovementStateMachine>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            _clientStarted = true;
            _lastSent = _flags.Value;
        }

        private void Update()
        {
            if (!_clientStarted)
                return;

            if (!base.IsOwner)
            {
                _currentFlags = (PlayerAnimFlags)_flags.Value;
                return;
            }

            if (!base.IsSpawned || _stateMachine == null)
                return;

            PlayerAnimFlags flags = ComposeOwnerFlags();
            _currentFlags = flags;

            byte value = (byte)flags;
            if (value == _lastSent)
                return;

            _lastSent = value;
            SetFlags(value);
        }

        private PlayerAnimFlags ComposeOwnerFlags()
        {
            PlayerAnimFlags flags = PlayerAnimFlags.None;
            if (_stateMachine.CrouchAmount >= CrouchedThreshold) flags |= PlayerAnimFlags.Crouched;
            if (!_stateMachine.IsOnGround)                      flags |= PlayerAnimFlags.Airborne;
            if (_stateMachine.IsSliding)                        flags |= PlayerAnimFlags.Sliding;
            if (_stateMachine.IsWallRunning)                    flags |= PlayerAnimFlags.WallRunning;
            if (_stateMachine.IsInAbilityMovement)              flags |= PlayerAnimFlags.AbilityMoving;
            return flags;
        }

        public bool TryGetFlags(out PlayerAnimFlags flags)
        {
            if (!_clientStarted)
            {
                flags = PlayerAnimFlags.None;
                return false;
            }

            flags = Flags;
            return true;
        }

        [ServerRpc(RunLocally = true)]
        private void SetFlags(byte value)
        {
            _flags.Value = value;
        }
    }
}
