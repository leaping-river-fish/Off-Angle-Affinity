// =============================================================================
// WorldKillPlane — per-map absolute-Y cutoff for falling out of the world.
//
// Unity has no "void" state. Empty space is just no collider, so this is a
// designer-authored floor under the lowest playable / wall-run geometry.
// Players whose world Y drops below this plane are killed by FallOffMapKill
// (server-side). High jumps and off-map wall runs above this Y stay legal.
//
// WHY TRANSFORM Y, NOT A SERIALIZED NUMBER:
//   Place an empty in the scene and drag it vertically. The world Y of this
//   object IS the kill plane, so tuning is a gizmo you can see rather than a
//   float you have to imagine.
//
// LIFETIME:
//   Scene-local MonoBehaviour, not DontDestroyOnLoad. A map without one
//   leaves Instance null and FallOffMapKill no-ops (menus / Test Scene).
//
// MANUAL SETUP:
//   1. Create an empty in the Game scene (e.g. "WorldKillPlane").
//   2. Add this component. Position Y below the lowest walkable surface
//      (around -50 on the current arena near Y=0; tune in play mode).
//   3. Do not parent it to a moving object.
// =============================================================================

using UnityEngine;

namespace OffAngle.Combat
{
    public class WorldKillPlane : MonoBehaviour
    {
        public static WorldKillPlane Instance { get; private set; }

        [Tooltip("Half-extent of the editor gizmo (world units). Gameplay uses this object's world Y only.")]
        [SerializeField, Min(1f)] private float _gizmoHalfExtent = 200f;

        /// <summary>World-space Y. Falling strictly below this kills.</summary>
        public float KillY => transform.position.y;

        private void OnEnable()
        {
            Instance = this;
        }

        private void OnDisable()
        {
            if (Instance == this)
                Instance = null;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            float y = transform.position.y;
            Vector3 center = new Vector3(transform.position.x, y, transform.position.z);
            Vector3 size = new Vector3(_gizmoHalfExtent * 2f, 0.05f, _gizmoHalfExtent * 2f);

            Gizmos.color = new Color(1f, 0.2f, 0.15f, 0.35f);
            Gizmos.DrawCube(center, size);
            Gizmos.color = new Color(1f, 0.2f, 0.15f, 0.9f);
            Gizmos.DrawWireCube(center, size);
        }
#endif
    }
}
