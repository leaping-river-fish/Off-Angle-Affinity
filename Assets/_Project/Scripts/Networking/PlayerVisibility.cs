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

        public override void OnStartClient()
        {
            base.OnStartClient();
            SetVisible(!base.IsOwner);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            SetVisible(true);
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