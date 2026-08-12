// =============================================================================
// LocalIPv4Resolver — picks the host's best LAN-facing IPv4 address when a
// machine has multiple network adapters (Wi-Fi, Ethernet, VPN, Hyper-V/
// Docker/VMware virtual adapters, etc).
//
// STRATEGY, in order:
//   1. Ask the OS routing table. Connect()ing a UDP socket to an off-subnet
//      address sends no packets, but it makes the OS choose the source address
//      it would actually use — i.e. the live address on the adapter currently
//      carrying traffic. Windows can leave a stale address behind on an adapter
//      after switching networks (e.g. an old 10.x lease still visible next to a
//      live 172.20.10.x hotspot address); enumerating adapters cannot tell the
//      two apart, but the routing table can. This wins in virtually every case.
//   2. Fall back to scoring every adapter's addresses, for when there is no
//      route at all — an isolated switch, a direct Ethernet link, no gateway.
//
// The caller should surface the resolved address (and ideally the full
// candidate list) so a host can visually confirm the right one got picked.
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
        // Any off-subnet address works — nothing is sent to it and it never has
        // to be reachable, so this resolves fine on a LAN with no internet.
        private static readonly IPAddress RouteProbeAddress = IPAddress.Parse("8.8.8.8");
        private const int RouteProbePort = 65530;

        // Large enough that the routed address always outranks every heuristic
        // score, without having to special-case it in the ordering.
        private const int RoutedBonus = 100;

        // A real, currently-in-use connection almost always has a default
        // gateway; Windows' virtual/pseudo adapters (Mobile Hotspot, Wi-Fi
        // Direct) often don't. Worth a nudge, but NOT a hard filter — a valid
        // gateway-less LAN (unmanaged switch, direct Ethernet link) is a real
        // setup, and filtering it out leaves the host with no address at all.
        private const int NoGatewayPenalty = -25;

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
            bool haveRouted = TryGetRoutedLocalIPv4(out IPAddress routed);

            allCandidates = new List<Candidate>();

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                IPInterfaceProperties properties = nic.GetIPProperties();

                bool hasIPv4Gateway = properties.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    IPAddress ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IPAddress.IsLoopback(ip) || IsLinkLocal(ip))
                        continue;

                    int score = ScoreAddress(ip, nic, hasIPv4Gateway);

                    // The OS's own answer beats every heuristic we could apply.
                    if (haveRouted && ip.Equals(routed))
                        score += RoutedBonus;

                    allCandidates.Add(new Candidate(ip, score));
                }
            }

            // The adapter walk can miss the routed address (adapters reporting
            // inconsistent state is exactly the situation this guards against),
            // and it is trustworthy on its own, so always represent it.
            if (haveRouted && !allCandidates.Any(c => c.Address.Equals(routed)))
                allCandidates.Add(new Candidate(routed, RoutedBonus));

            allCandidates = allCandidates.OrderByDescending(c => c.Score).ToList();

            if (allCandidates.Count == 0)
            {
                best = null;
                return false;
            }

            best = allCandidates[0].Address;
            return true;
        }

        /// <summary>
        /// Asks the OS which local address it would use to reach an off-subnet
        /// destination. Connect() on a UDP socket is a pure routing-table
        /// lookup — no packets are sent and the destination never has to be
        /// reachable — so this works offline, and unlike enumerating adapters
        /// it cannot return a stale address from a network we have since left.
        /// Returns false when there is no route at all, which is a legitimate
        /// LAN setup; the caller falls back to scoring.
        /// </summary>
        private static bool TryGetRoutedLocalIPv4(out IPAddress address)
        {
            address = null;

            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(RouteProbeAddress, RouteProbePort));

                    if (!(socket.LocalEndPoint is IPEndPoint endPoint))
                        return false;

                    if (IPAddress.IsLoopback(endPoint.Address) || IsLinkLocal(endPoint.Address))
                        return false;

                    address = endPoint.Address;
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static bool IsLinkLocal(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }

        private static int ScoreAddress(IPAddress ip, NetworkInterface nic, bool hasIPv4Gateway)
        {
            byte[] b = ip.GetAddressBytes();
            int score;

            // Distinct scores per private range so two addresses on different
            // ranges can never tie — a tie makes the winner depend on adapter
            // enumeration order, which is not stable across runs.
            if (b[0] == 192 && b[1] == 168)
                score = 30;
            else if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                score = 25;
            else if (b[0] == 10)
                score = 20;
            else
                score = -50;

            if (!hasIPv4Gateway)
                score += NoGatewayPenalty;

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
