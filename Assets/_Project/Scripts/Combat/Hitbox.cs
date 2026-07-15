// =============================================================================
// Hitbox — marker component declaring which body zone a collider represents.
//
// PlayerWeaponController reads this straight off the raycast hit collider to
// decide how much damage to apply. A collider with no Hitbox (or a Hitbox set
// to Body) is treated as a normal hit. Adding a new zone later (arms, legs,
// weak points, etc.) is just a new HitZone value plus a new collider tagged
// with it - no changes to the detection code in PlayerWeaponController.
// =============================================================================

using UnityEngine;

namespace OffAngle.Combat
{
    public enum HitZone
    {
        Body = 0,
        Head,
    }

    [DisallowMultipleComponent]
    public class Hitbox : MonoBehaviour
    {
        [SerializeField] private HitZone _zone = HitZone.Body;

        public HitZone Zone => _zone;
    }
}
