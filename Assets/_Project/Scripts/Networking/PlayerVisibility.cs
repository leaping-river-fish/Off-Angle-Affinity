using System.Collections.Generic;
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

        // Renderers registered at runtime (e.g. a third-person weapon model
        // instantiated after this component's Inspector array was already
        // fixed at prefab-edit time - see PlayerWeaponEquipper's third-person
        // sync). Kept separate from _hiddenFromOwner so that serialized array
        // never needs runtime mutation.
        private readonly List<Renderer> _dynamicHiddenFromOwner = new();

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

        /// <summary>
        /// Adds renderers created after startup (e.g. a third-person weapon
        /// model instantiated once loadout sync arrives) to the same
        /// owner-hidden rule as _hiddenFromOwner, applying the CURRENT
        /// visibility state immediately so a model instantiated while this
        /// player is alive starts hidden from its own owner just like every
        /// other third-person renderer. No-op for remote peers - only the
        /// owner ever hides these.
        /// </summary>
        public void RegisterDynamicRenderers(IEnumerable<Renderer> renderers)
        {
            if (renderers == null || !base.IsOwner) return;

            bool visible = !base.IsOwner || _forcedVisibleToOwner;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                renderer.enabled = visible;
                _dynamicHiddenFromOwner.Add(renderer);
            }
        }

        /// <summary>Call before destroying a renderer previously passed to RegisterDynamicRenderers, so the list doesn't accumulate stale entries.</summary>
        public void UnregisterDynamicRenderers(IEnumerable<Renderer> renderers)
        {
            if (renderers == null) return;
            foreach (Renderer renderer in renderers)
                _dynamicHiddenFromOwner.Remove(renderer);
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

            foreach (Renderer renderer in _dynamicHiddenFromOwner)
            {
                if (renderer != null)
                {
                    renderer.enabled = visible;
                }
            }
        }
    }
}