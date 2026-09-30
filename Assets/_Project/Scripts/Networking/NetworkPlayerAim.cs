// =============================================================================
// NetworkPlayerAim — visual-only third-person AimPitch replication.
//
// AUTHORITY MODEL:
//   Same owner-writable SyncVar pattern as NetworkPlayerCrouch:
//     WritePermission.ClientUnsynchronized + ReadPermission.ExcludeOwner.
//   The owner already has live camera pitch via ThirdPersonAimAnimator; remotes
//   cannot read that camera because the camera subtree stays inactive.
//   Owner writes quantized pitch through [ServerRpc(RunLocally = true)].
//
// THIS IS NOT GAMEPLAY AIM:
//   Hitscan/projectiles still use PlayerWeaponController.GetAimRay() from the
//   owner's camera. This SyncVar only drives remote upper-body IK.
// =============================================================================

using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Player;
using UnityEngine;

namespace OffAngle.Networking
{
    public class NetworkPlayerAim : NetworkBehaviour, IAimPitchOverride
    {
        [Header("References")]
        [Tooltip("Owner-only camera. Read, never rotated. Remotes must not activate this.")]
        [SerializeField] private PlayerCameraController _cameraController;

        [Header("Tuning")]
        [SerializeField] private float _maxPitch = 50f;
        [Tooltip("Network sends per second. Clamped to 10–20.")]
        [SerializeField] private float _sendHz = 15f;
        [Tooltip("Seconds for remotes to MoveTowards the latest received pitch.")]
        [SerializeField] private float _lerpDuration = 0.1f;

        private readonly SyncVar<float> _aimPitch =
            new SyncVar<float>(new SyncTypeSettings(WritePermission.ClientUnsynchronized, ReadPermission.ExcludeOwner));

        private bool  _clientStarted;
        private bool  _isOwnerVisual;
        private float _displayPitch;
        private float _lastSent = float.NaN;
        private float _nextSendTime;

        private void Awake()
        {
            if (_cameraController == null)
                _cameraController = GetComponentInChildren<PlayerCameraController>(true);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            _clientStarted = true;
            _isOwnerVisual = base.IsOwner;
            _displayPitch = Mathf.Clamp(_aimPitch.Value, -_maxPitch, _maxPitch);
            _lastSent = Mathf.Round(_displayPitch);
        }

        private void Update()
        {
            if (!_clientStarted)
                return;

            if (base.IsOwner)
                MaybeSendPitch();
            else
                InterpolatePitch();
        }

        public bool TryGetPitch(out float pitch)
        {
            // Before OnStartClient (and if this NetworkBehaviour never starts),
            // do not claim pitch 0 — that blocked the owner's live camera.
            // False → animator uses camera only when activeInHierarchy (owner),
            // otherwise 0 (remote, inactive camera subtree).
            if (!_clientStarted || _isOwnerVisual)
            {
                pitch = 0f;
                return false;
            }

            pitch = Mathf.Clamp(_displayPitch, -_maxPitch, _maxPitch);
            return true;
        }

        private void MaybeSendPitch()
        {
            if (!base.IsSpawned)
                return;
            if (Time.time < _nextSendTime)
                return;

            float pitch = SampleOwnerPitch();
            float quantized = Mathf.Round(pitch);
            if (!float.IsNaN(_lastSent) && Mathf.Approximately(quantized, _lastSent))
                return;

            _lastSent = quantized;
            float hz = Mathf.Clamp(_sendHz, 10f, 20f);
            _nextSendTime = Time.time + 1f / hz;
            SetAimPitch(quantized);
        }

        private float SampleOwnerPitch()
        {
            if (_cameraController == null)
                return 0f;

            Transform aim = _cameraController.CameraTransform;
            if (aim == null || !aim.gameObject.activeInHierarchy)
                return 0f;

            return ThirdPersonAimAnimator.PitchFromAimForward(aim.forward, _maxPitch);
        }

        private void InterpolatePitch()
        {
            float target = Mathf.Clamp(_aimPitch.Value, -_maxPitch, _maxPitch);
            // Crouch uses 1/duration as units/sec because its range is ~0-1.
            // Pitch is degrees: 1/0.1 = 10°/s would take ~5s to look fully up.
            float maxDegreesPerSecond = _maxPitch / Mathf.Max(0.01f, _lerpDuration);
            _displayPitch = Mathf.MoveTowards(_displayPitch, target, maxDegreesPerSecond * Time.deltaTime);
        }

        [ServerRpc(RunLocally = true)]
        private void SetAimPitch(float value)
        {
            _aimPitch.Value = Mathf.Clamp(value, -_maxPitch, _maxPitch);
        }
    }
}
