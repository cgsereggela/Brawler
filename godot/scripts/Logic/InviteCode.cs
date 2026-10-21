using System;
using System.Text;

namespace Brawler.Logic;

/// <summary>
/// Streamer-mode invite codes: an IPv4 address and port scrambled into "XXXX-XXXX-XXXX", so the host can paste it to
/// friends without the address ever appearing on stream. It hides the address from viewers; it is not encryption, so
/// the code itself should still be sent privately. A checksum catches typos. Pure, so the tests can cover it.
/// </summary>
public static class InviteCode
{
    // Crockford base32: no I, L, O or U, so codes read aloud or retyped don't get mixed up.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly byte[] Mask = { 0x5A, 0xC3, 0x1E, 0x97, 0x6B, 0x2D };

    /// <summary>The code for an IPv4 address and port, or "" when the address isn't IPv4.</summary>
    public static string Encode(string ip, int port)
    {
        var parts = (ip ?? "").Split('.');
        if (parts.Length != 4 || port is <= 0 or >= 65536) return "";
        var data = new byte[7];
        for (int i = 0; i < 4; i++)
            if (!byte.TryParse(parts[i], out data[i])) return "";
        data[4] = (byte)(port >> 8);
        data[5] = (byte)port;
        for (int i = 0; i < 6; i++) data[i] ^= Mask[i];
        data[6] = Checksum(data);

        // 56 bits into 12 five-bit digits (the last 4 bits are zero padding).
        ulong bits = 0;
        foreach (byte b in data) bits = bits << 8 | b;
        bits <<= 4;
        var sb = new StringBuilder();
        for (int i = 11; i >= 0; i--)
        {
            sb.Append(Alphabet[(int)(bits >> (i * 5) & 31)]);
            if (i is 8 or 4) sb.Append('-');
        }
        return sb.ToString();
    }

    /// <summary>Reads a code back (dashes, spaces and case don't matter; O/I/L are read as 0/1/1). False for anything else.</summary>
    public static bool TryDecode(string text, out string ip, out int port)
    {
        ip = "";
        port = 0;
        ulong bits = 0;
        int digits = 0;
        foreach (char raw in (text ?? "").ToUpperInvariant())
        {
            if (raw is '-' or ' ') continue;
            char c = raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw };
            int v = Alphabet.IndexOf(c);
            if (v < 0 || ++digits > 12) return false;
            bits = bits << 5 | (uint)v;
        }
        if (digits != 12 || (bits & 15) != 0) return false;
        bits >>= 4;
        var data = new byte[7];
        for (int i = 6; i >= 0; i--, bits >>= 8) data[i] = (byte)bits;
        if (data[6] != Checksum(data)) return false;
        for (int i = 0; i < 6; i++) data[i] ^= Mask[i];
        port = data[4] << 8 | data[5];
        if (port == 0) return false;
        ip = $"{data[0]}.{data[1]}.{data[2]}.{data[3]}";
        return true;
    }

    private static byte Checksum(byte[] data)
    {
        int sum = 0x3B;
        for (int i = 0; i < 6; i++) sum = (sum * 31 + data[i]) & 0xFF;
        return (byte)sum;
    }
}
