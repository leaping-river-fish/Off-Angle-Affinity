// =============================================================================
// LanJoinCodeProvider — the LAN-only IJoinCodeProvider implementation.
// Combines LocalIPv4Resolver (pick the host's LAN IP) and LanJoinCodec
// (encode/decode that IP + port as a short code). Resolves synchronously.
// =============================================================================

using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace OffAngle.Networking.JoinCode
{
    public class LanJoinCodeProvider : IJoinCodeProvider
    {
        public HostCodeResult CreateHostCode(ushort hostPort, string manualIpOverride)
        {
            IPAddress localIp;

            if (!string.IsNullOrWhiteSpace(manualIpOverride))
            {
                if (!IPAddress.TryParse(manualIpOverride.Trim(), out localIp) ||
                    localIp.AddressFamily != AddressFamily.InterNetwork)
                {
                    return HostCodeResult.Failed($"'{manualIpOverride.Trim()}' is not a valid IPv4 address");
                }

                Debug.Log($"[LanJoinCodeProvider] Using manual IP override: {localIp}:{hostPort}");
            }
            else
            {
                if (!LocalIPv4Resolver.TryGetBestLocalIPv4(out localIp, out var candidates))
                    return HostCodeResult.Failed("Could not determine a LAN IP - check this machine is on a network");

                // Only the winner is surfaced in the UI; the full list goes to the
                // console, which is what you actually want open when the winner
                // looks wrong and you need to see what it beat.
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[LanJoinCodeProvider] Chosen host IP: {localIp}:{hostPort}. All candidates (highest score wins):");
                foreach (var c in candidates)
                    sb.AppendLine($"  {c.Address} (score {c.Score})");
                Debug.Log(sb.ToString());
            }

            if (!LanJoinCodec.TryEncode(localIp, hostPort, out string code))
                return HostCodeResult.Failed($"Failed to encode a join code for {localIp}:{hostPort}");

            return HostCodeResult.Succeeded(code, localIp.ToString());
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
