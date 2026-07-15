using FishNet.Object;
using UnityEngine;

namespace OffAngle.Networking
{
    // Hides third-person renderers from the owning client only. Colliders on
    // the same objects are never touched here, so hit-detection keeps
    // working for every peer regardless of what the owner can see.
    public class PlayerVisibility : NetworkBehaviour
    {
        [Tooltip("Renderers to hide for the owning client only.")]
        [SerializeField] private Renderer[] _hiddenFromOwner;

        // True while PlayerLifecycleController is overriding the normal
        // owner-hidden rule (death camera needs the owner to see their own corpse).
        private bool _forcedVisibleToOwner;

        public override void OnStartClient()
        {
            base.OnStartClient();
            SetVisible(!base.IsOwner || _forcedVisibleToOwner);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            SetVisible(true);
        }

        /// <summary>
        /// Owner-only override called by PlayerLifecycleController. While dead,
        /// the owner needs to see their own ragdoll from the death camera, which
        /// the default "hide third-person body from owner" rule would otherwise
        /// block. Pass false on respawn to restore the normal rule. No-op for
        /// remote peers (their view of this player never hides these renderers).
        /// </summary>
        public void ForceVisibleToOwner(bool force)
        {
            if (!base.IsOwner) return;

            _forcedVisibleToOwner = force;
            SetVisible(_forcedVisibleToOwner);
        }

        private void SetVisible(bool visible)
        {
            foreach (Renderer renderer in _hiddenFromOwner)
            {
                if (renderer != null)
                {
                    renderer.enabled = visible;
                }
            }
        }
    }
}