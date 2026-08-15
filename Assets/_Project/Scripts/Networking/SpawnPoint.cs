// =============================================================================
// SpawnPoint — marker component for player spawn locations in the Game scene.
//
// PlayerSpawner resolves these at runtime via FindObjectsByType once the Game
// scene finishes loading, since it lives on a persistent GameObject created in
// MainMenu and can no longer hold a design-time Inspector reference to
// Transforms that only exist once the Game scene is loaded.
// =============================================================================

using UnityEngine;

namespace OffAngle.Networking
{
    public class SpawnPoint : MonoBehaviour
    {
    }
}
