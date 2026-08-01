// =============================================================================
// SpeedHeightHUD — screen-space UI showing the local player's current speed
// and height above ground level.
//
// Purely cosmetic, exactly like AmmoHUD/HealthHUD. Speed reads directly off
// CharacterController.velocity (no coupling to MovementStateContext needed -
// every movement state ultimately drives the player via Controller.Move(),
// so this is already the single source of truth). Height is measured with a
// downward raycast each refresh, against a configurable ground LayerMask, so
// level designers can decide what counts as "ground" without touching code.
// =============================================================================
using TMPro;
using UnityEngine;

namespace OffAngle.UI.Movement
{
    public class SpeedHeightHUD : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private CharacterController _controller;
        [SerializeField] private TMP_Text _speedLabel;
        [SerializeField] private TMP_Text _heightLabel;

        [Header("Speed")]
        [Tooltip("If true, vertical velocity (jumping/falling) counts toward the displayed speed. If false, only horizontal (XZ) speed is shown - usually the more useful readout.")]
        [SerializeField] private bool _includeVerticalSpeed = false;
        [SerializeField] private string _speedFormat = "{0:0.0} m/s";

        [Header("Height Above Ground")]
        [Tooltip("Layers considered \"ground\" for the height raycast. Should match whatever the level's walkable geometry sits on.")]
        [SerializeField] private LayerMask _groundLayers = ~0;
        [Tooltip("Longest distance (m) the raycast checks before giving up and reporting MaxHeightCheckDistance as the reading.")]
        [SerializeField] private float _maxHeightCheckDistance = 200f;
        [SerializeField] private string _heightFormat = "{0:0.0} m";

        [Header("Refresh Rate")]
        [Tooltip("Seconds between label updates. 0 = update every frame. A small interval avoids needless text-mesh regeneration for a value that changes imperceptibly frame-to-frame.")]
        [SerializeField] private float _refreshInterval = 0.1f;

        private float _refreshTimer;

        private void Start()
        {
            if (_controller == null)
                _controller = GetComponentInParent<CharacterController>();

            if (_controller == null)
                Debug.LogWarning($"[{nameof(SpeedHeightHUD)}] No CharacterController assigned or found in parents for '{name}'.", this);
        }

        private void Update()
        {
            if (_controller == null) return;

            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer > 0f) return;
            _refreshTimer = _refreshInterval;

            UpdateSpeedLabel();
            UpdateHeightLabel();
        }

        private void UpdateSpeedLabel()
        {
            if (_speedLabel == null) return;

            Vector3 velocity = _controller.velocity;
            float speed = _includeVerticalSpeed
                ? velocity.magnitude
                : new Vector3(velocity.x, 0f, velocity.z).magnitude;

            _speedLabel.text = string.Format(_speedFormat, speed);
        }

        private void UpdateHeightLabel()
        {
            if (_heightLabel == null) return;

            _heightLabel.text = string.Format(_heightFormat, GetHeightAboveGround());
        }

        /// <summary>
        /// Raycasts straight down from the controller's feet to the nearest
        /// surface on _groundLayers. Origin is nudged up slightly so a player
        /// standing exactly on the ground doesn't miss due to starting the
        /// ray inside the collider. Returns _maxHeightCheckDistance (not
        /// infinity) when nothing is hit, so the label always shows a finite,
        /// readable number.
        ///
        /// Reports a hard 0 whenever Controller.isGrounded is true instead of
        /// trusting the raycast: CharacterController deliberately never lets
        /// its capsule touch a surface exactly - it always rests skinWidth
        /// (0.08m on this project's recommended settings, see
        /// PlayerController.cs) above whatever it's standing on, which the
        /// raycast would otherwise faithfully report as a non-zero height
        /// (rounds to "0.1 m" at one decimal place). isGrounded is exactly
        /// the same flag GroundedState already treats as "on the ground",
        /// so this keeps the HUD's definition of "grounded" consistent with
        /// movement's.
        /// </summary>
        private float GetHeightAboveGround()
        {
            if (_controller.isGrounded)
                return 0f;

            const float originSkin = 0.05f;
            Vector3 origin = _controller.transform.position + Vector3.up * originSkin;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, _maxHeightCheckDistance + originSkin, _groundLayers))
                return Mathf.Max(0f, hit.distance - originSkin);

            return _maxHeightCheckDistance;
        }
    }
}
