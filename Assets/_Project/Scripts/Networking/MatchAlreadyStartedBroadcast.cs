// =============================================================================
// MatchAlreadyStartedBroadcast — sent server -> one connection right before
// PlayerSpawner kicks it for arriving after the match has already started.
//
// WHY:
//   FishNet's Kick() never sends the KickReason to the client - it only
//   raises a server-local OnClientKick event and disconnects. Without this,
//   a rejected late joiner sees the exact same "Disconnected" message as any
//   other mid-session drop, with no way to tell the two apart. Sending this
//   first gives NetworkMenuController a specific reason to show instead.
// =============================================================================

using FishNet.Broadcast;

namespace OffAngle.Networking
{
    public struct MatchAlreadyStartedBroadcast : IBroadcast
    {
    }
}
