// =============================================================================
// LanJoinCodec — encodes/decodes an IPv4 address + port into a short,
// human-typeable join code (and back).
//
// Format: 10 characters drawn from Crockford's Base32 alphabet, displayed as
// two groups of 5 separated by a hyphen (e.g. "5H2K9-QRT8M"). The hyphen is
// cosmetic only and stripped on decode. Crockford's alphabet excludes
// I/L/O/U to avoid characters that are easy to mistype or misread; decoding
// still tolerates the common substitutions (O->0, I/L->1).
// =============================================================================

using System;
using System.Net;
using System.Text;

namespace OffAngle.Networking.JoinCode
{
    public static class LanJoinCodec
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int CodeLength = 10;

        public static bool TryEncode(IPAddress ip, ushort port, out string code)
        {
            code = null;

            if (ip == null || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;

            byte[] ipBytes = ip.GetAddressBytes();

            ulong value48 =
                ((ulong)ipBytes[0] << 40) |
                ((ulong)ipBytes[1] << 32) |
                ((ulong)ipBytes[2] << 24) |
                ((ulong)ipBytes[3] << 16) |
                ((ulong)(port >> 8) << 8) |
                (ulong)(port & 0xFF);

            ulong value50 = value48 << 2;

            var sb = new StringBuilder(CodeLength + 1);
            for (int i = 0; i < CodeLength; i++)
            {
                int shift = 45 - (i * 5);
                int groupValue = (int)((value50 >> shift) & 0x1F);
                sb.Append(Alphabet[groupValue]);

                if (i == 4)
                    sb.Append('-');
            }

            code = sb.ToString();
            return true;
        }

        public static bool TryDecode(string code, out string address, out ushort port)
        {
            address = null;
            port = 0;

            if (string.IsNullOrWhiteSpace(code))
                return false;

            var cleaned = new StringBuilder(CodeLength);
            foreach (char rawChar in code)
            {
                if (rawChar == '-' || char.IsWhiteSpace(rawChar))
                    continue;

                char c = char.ToUpperInvariant(rawChar);
                switch (c)
                {
                    case 'O':
                        c = '0';
                        break;
                    case 'I':
                    case 'L':
                        c = '1';
                        break;
                }

                cleaned.Append(c);
            }

            if (cleaned.Length != CodeLength)
                return false;

            ulong value50 = 0;
            foreach (char c in cleaned.ToString())
            {
                int groupValue = Alphabet.IndexOf(c);
                if (groupValue < 0)
                    return false;

                value50 = (value50 << 5) | (uint)groupValue;
            }

            ulong value48 = value50 >> 2;

            byte b0 = (byte)((value48 >> 40) & 0xFF);
            byte b1 = (byte)((value48 >> 32) & 0xFF);
            byte b2 = (byte)((value48 >> 24) & 0xFF);
            byte b3 = (byte)((value48 >> 16) & 0xFF);
            ushort decodedPort = (ushort)(value48 & 0xFFFF);

            if (decodedPort == 0 || (b0 == 0 && b1 == 0 && b2 == 0 && b3 == 0))
                return false;

            address = $"{b0}.{b1}.{b2}.{b3}";
            port = decodedPort;
            return true;
        }
    }
}
