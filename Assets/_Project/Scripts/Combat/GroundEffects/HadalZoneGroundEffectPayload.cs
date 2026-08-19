// =============================================================================
// HadalZoneGroundEffectPayload — Tide's Hadal Zone ultimate. A pure enter/exit
// debuff (no damage, so OnTick is a no-op): grounds (blocks jumping) and
// nearsights (narrows FOV) anyone standing in the zone, reverted the moment
// they leave - either by walking out or by the zone expiring, since
// GroundEffectZone.ServerEndAllOccupants() forces OnExit on every remaining
// occupant before despawn. Excludes the zone's own creator, same convention
// as FireGroundEffectPayload.
//
// MUFFLED / SOUND: no sound system exists in this project yet, so _muffled is
// carried as an inert flag for forward-compatibility only (same pattern as
// AffinityType being carried before elemental damage types existed) - wire it
// up in PlayerStatusEffects once there is a sound system to mute.
//
// Create instances via: Assets > Create > Off-Angle > Combat > Ground Effects > Hadal Zone
// =============================================================================

using UnityEngine;
using OffAngle.Affinities;

namespace OffAngle.Combat
{
    [CreateAssetMenu(menuName = "Off-Angle/Combat/Ground Effects/Hadal Zone", fileName = "GroundEffect_HadalZone")]
    public class HadalZoneGroundEffectPayload : GroundEffectPayload
    {
        [Header("Grounded")]
        [Tooltip("While inside, the occupant cannot jump, sprint, slide, wall-run, or start a movement ability (e.g. grapple) - see MovementStateContext.GroundedLocked for the full list.")]
        [SerializeField] private bool _grounded = true;

        [Header("Nearsighted")]
        [SerializeField] private bool _nearsighted = true;
        [Tooltip("Field of view (degrees) eased to while inside. Lower = more tunnel-visioned.")]
        [SerializeField, Range(10f, 90f)] private float _nearsightFov = 35f;

        [Header("Muffled (future - no sound system yet)")]
        [SerializeField] private bool _muffled = true;

        public override void OnEnter(AffinityRuntimeContext occupant, GroundEffectZone zone)
        {
            if (occupant?.StatusEffects == null) return;
            if (zone.OwnerNetworkObject != null && occupant.NetworkObject == zone.OwnerNetworkObject) return;

            if (_grounded) occupant.StatusEffects.ServerApplyGrounded();
            if (_nearsighted) occupant.StatusEffects.ServerApplyNearsighted(_nearsightFov);
            // _muffled: no-op until a sound system exists.
        }

        public override void OnTick(AffinityRuntimeContext occupant, GroundEffectZone zone, float deltaTime) { }

        public override void OnExit(AffinityRuntimeContext occupant, GroundEffectZone zone)
        {
            if (occupant?.StatusEffects == null) return;
            if (zone.OwnerNetworkObject != null && occupant.NetworkObject == zone.OwnerNetworkObject) return;

            if (_grounded) occupant.StatusEffects.ServerClearGrounded();
            if (_nearsighted) occupant.StatusEffects.ServerClearNearsighted();
        }
    }
}
