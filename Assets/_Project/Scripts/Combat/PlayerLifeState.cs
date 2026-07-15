// =============================================================================
// PlayerLifeState — the player's high-level lifecycle state.
//
// Orthogonal to MovementStateId: movement states describe *how* an alive
// player is moving (Grounded, Airborne, ...); PlayerLifeState describes
// *whether* the player is playable at all. A player is always exactly one
// PlayerLifeState, independent of whatever MovementStateId it was in when it
// died (that movement state is simply paused, not exited, for the duration).
//
// Reserved for future milestones (not implemented yet): Spectating, Reviving.
// Add new values here, not by repurposing Alive/Dead, so existing switches
// stay exhaustive and obviously incomplete when a new state is introduced.
// =============================================================================

namespace OffAngle.Combat
{
    public enum PlayerLifeState
    {
        Alive,
        Dead,
    }
}
