using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace EggCoop
{
    // 12 chars of Crockford base32 = 60 bits: 4 bits version, 32 bits IPv4, 16 bits port, 8 bits salt.
    // Same idea as sa-coop's invite codes. The salt only makes the signalling topic harder to guess.
    public static class InviteCode
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        const int Version = 1;

        public static string Make(IPEndPoint ep, byte salt)
        {
            byte[] ip = ep.Address.GetAddressBytes();
            ulong v = (ulong)Version << 56 | (ulong)ip[0] << 48 | (ulong)ip[1] << 40 | (ulong)ip[2] << 32
                    | (ulong)ip[3] << 24 | (ulong)(ep.Port & 0xFFFF) << 8 | salt;
            var sb = new StringBuilder();
            for (int i = 11; i >= 0; i--)
            {
                sb.Append(Alphabet[(int)(v >> (i * 5)) & 31]);
                if (i == 8 || i == 4) sb.Append('-');
            }
            return sb.ToString();
        }

        public static bool TryParse(string code, out IPEndPoint ep, out byte salt)
        {
            ep = null; salt = 0;
            ulong v = 0; int n = 0;
            foreach (char raw in code.ToUpperInvariant())
            {
                char c = raw == 'O' ? '0' : raw == 'I' || raw == 'L' ? '1' : raw;
                if (c == '-' || c == ' ') continue;
                int d = Alphabet.IndexOf(c);
                if (d < 0) return false;
                v = v << 5 | (uint)d; n++;
            }
            if (n != 12 || (int)(v >> 56) != Version) return false;
            var ip = new IPAddress(new[] { (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32), (byte)(v >> 24) });
            ep = new IPEndPoint(ip, (int)(v >> 8) & 0xFFFF);
            salt = (byte)v;
            return true;
        }

        // ntfy topic both sides derive from the code.
        public static string Topic(string code)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.ASCII.GetBytes(code.Replace("-", "").ToUpperInvariant() + "/eggcoop"));
                return "eggcoop-" + BitConverter.ToString(h, 0, 10).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    // Minimal STUN (RFC 5389) binding request / response.
    public static class Stun
    {
        public const uint Cookie = 0x2112A442;

        public static void WriteRequest(byte[] buf, int offset, byte[] txid)
        {
            buf[offset] = 0x00; buf[offset + 1] = 0x01;      // Binding Request
            buf[offset + 2] = 0; buf[offset + 3] = 0;        // length 0
            buf[offset + 4] = 0x21; buf[offset + 5] = 0x12; buf[offset + 6] = 0xA4; buf[offset + 7] = 0x42;
            Buffer.BlockCopy(txid, 0, buf, offset + 8, 12);
        }

        public static bool IsStun(byte[] d, int len) =>
            len >= 20 && d[4] == 0x21 && d[5] == 0x12 && d[6] == 0xA4 && d[7] == 0x42 && (d[0] & 0xC0) == 0;

        // Returns the XOR-MAPPED-ADDRESS (or MAPPED-ADDRESS) of a Binding Success Response.
        public static IPEndPoint ParseResponse(byte[] d, int len)
        {
            if (!IsStun(d, len) || d[0] != 0x01 || d[1] != 0x01) return null;
            int end = Math.Min(len, 20 + (d[2] << 8 | d[3]));
            for (int i = 20; i + 4 <= end;)
            {
                int type = d[i] << 8 | d[i + 1], alen = d[i + 2] << 8 | d[i + 3], v = i + 4;
                if ((type == 0x0020 || type == 0x0001) && alen >= 8 && d[v + 1] == 0x01)
                {
                    bool x = type == 0x0020;
                    int port = (d[v + 2] << 8 | d[v + 3]) ^ (x ? 0x2112 : 0);
                    var ip = new byte[4];
                    for (int k = 0; k < 4; k++) ip[k] = (byte)(d[v + 4 + k] ^ (x ? (byte)(Cookie >> (24 - 8 * k)) : 0));
                    return new IPEndPoint(new IPAddress(ip), port);
                }
                i = v + ((alen + 3) & ~3);
            }
            return null;
        }
    }
}
