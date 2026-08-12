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
        /// <summary>
        /// manualIpOverride: if non-empty and a valid IPv4, used directly instead
        /// of auto-detecting the host's LAN IP. Escape hatch for when the
        /// auto-detection heuristic picks the wrong network adapter.
        /// </summary>
        HostCodeResult CreateHostCode(ushort hostPort, string manualIpOverride);

        void ResolveCode(string code, Action<JoinCodeResolution> onResolved);
    }

    public readonly struct HostCodeResult
    {
        public bool Success { get; }
        public string Code { get; }

        /// <summary>
        /// The IPv4 baked into <see cref="Code"/>. Shown alongside the code so a
        /// host can confirm at a glance that the right adapter was picked --
        /// a wrong-but-plausible IP here is otherwise indistinguishable from a
        /// blocked port, and both present as "Connection failed" on the joiner.
        /// </summary>
        public string HostAddress { get; }

        public string ErrorReason { get; }

        private HostCodeResult(bool success, string code, string hostAddress, string errorReason)
        {
            Success = success;
            Code = code;
            HostAddress = hostAddress;
            ErrorReason = errorReason;
        }

        public static HostCodeResult Succeeded(string code, string hostAddress) =>
            new HostCodeResult(true, code, hostAddress, null);

        public static HostCodeResult Failed(string errorReason) =>
            new HostCodeResult(false, null, null, errorReason);
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
