// =============================================================================
// IJoinCodeProvider — the seam between "type a code" UI/controller flow and
// however that code actually gets turned into connection info.
//
// The only implementation today is LanJoinCodeProvider (LAN-only, resolves
// synchronously). ResolveCode is callback-based specifically so a future
// provider that needs a network round-trip (a relay handshake, or a
// rendezvous server mapping code -> public IP:port) can be swapped in later
// without NetworkMenuController or NetworkMenuUI changing at all.
// =============================================================================

using System;

namespace OffAngle.Networking.JoinCode
{
    public interface IJoinCodeProvider
    {
        bool TryCreateHostCode(ushort hostPort, out string code, out string errorReason);

        void ResolveCode(string code, Action<JoinCodeResolution> onResolved);
    }

    public readonly struct JoinCodeResolution
    {
        public bool Success { get; }
        public string Address { get; }
        public ushort Port { get; }
        public string ErrorReason { get; }

        private JoinCodeResolution(bool success, string address, ushort port, string errorReason)
        {
            Success = success;
            Address = address;
            Port = port;
            ErrorReason = errorReason;
        }

        public static JoinCodeResolution Succeeded(string address, ushort port) =>
            new JoinCodeResolution(true, address, port, null);

        public static JoinCodeResolution Failed(string errorReason) =>
            new JoinCodeResolution(false, null, 0, errorReason);
    }
}
