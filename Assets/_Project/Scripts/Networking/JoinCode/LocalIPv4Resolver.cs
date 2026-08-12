// =============================================================================
// LocalIPv4Resolver — picks the host's best LAN-facing IPv4 address when a
// machine has multiple network adapters (Wi-Fi, Ethernet, VPN, Hyper-V/
// Docker/VMware virtual adapters, etc).
//
// This is a heuristic, not a guarantee: it scores private-range addresses
// highest and penalizes adapters that are typically virtual/tunnel, but the
// caller should surface the resolved address (and ideally the full candidate
// list) so a host can visually confirm the right one got picked.
// =============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OffAngle.Networking.JoinCode
{
    public static class LocalIPv4Resolver
    {
        public readonly struct Candidate
        {
            public readonly IPAddress Address;
            public readonly int Score;

            public Candidate(IPAddress address, int score)
            {
                Address = address;
                Score = score;
            }
        }

        public static bool TryGetBestLocalIPv4(out IPAddress best, out List<Candidate> allCandidates)
        {
            allCandidates = new List<Candidate>();

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    IPAddress ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IPAddress.IsLoopback(ip) || IsLinkLocal(ip))
                        continue;

                    int score = ScoreAddress(ip, nic);
                    allCandidates.Add(new Candidate(ip, score));
                }
            }

            allCandidates = allCandidates.OrderByDescending(c => c.Score).ToList();

            if (allCandidates.Count == 0)
            {
                best = null;
                return false;
            }

            best = allCandidates[0].Address;
            return true;
        }

        private static bool IsLinkLocal(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }

        private static int ScoreAddress(IPAddress ip, NetworkInterface nic)
        {
            byte[] b = ip.GetAddressBytes();
            int score;

            if (b[0] == 192 && b[1] == 168)
                score = 30;
            else if (b[0] == 10)
                score = 20;
            else if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                score = 20;
            else
                score = -50;

            string description = nic.Description?.ToLowerInvariant() ?? "";
            if (description.Contains("virtual") || description.Contains("hyper-v") ||
                description.Contains("vmware") || description.Contains("virtualbox") ||
                description.Contains("docker"))
            {
                score -= 20;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                score -= 40;

            return score;
        }
    }
}
