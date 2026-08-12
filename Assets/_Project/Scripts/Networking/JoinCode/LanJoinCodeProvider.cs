// =============================================================================
// LanJoinCodeProvider — the LAN-only IJoinCodeProvider implementation.
// Combines LocalIPv4Resolver (pick the host's LAN IP) and LanJoinCodec
// (encode/decode that IP + port as a short code). Resolves synchronously.
// =============================================================================

using System;
using System.Net;

namespace OffAngle.Networking.JoinCode
{
    public class LanJoinCodeProvider : IJoinCodeProvider
    {
        public bool TryCreateHostCode(ushort hostPort, out string code, out string errorReason)
        {
            code = null;

            if (!LocalIPv4Resolver.TryGetBestLocalIPv4(out IPAddress localIp, out _))
            {
                errorReason = "Could not determine a LAN IP address";
                return false;
            }

            if (!LanJoinCodec.TryEncode(localIp, hostPort, out code))
            {
                errorReason = "Failed to encode join code";
                return false;
            }

            errorReason = null;
            return true;
        }

        public void ResolveCode(string code, Action<JoinCodeResolution> onResolved)
        {
            if (LanJoinCodec.TryDecode(code, out string address, out ushort port))
                onResolved(JoinCodeResolution.Succeeded(address, port));
            else
                onResolved(JoinCodeResolution.Failed("Invalid join code"));
        }
    }
}
