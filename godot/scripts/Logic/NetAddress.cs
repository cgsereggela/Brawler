namespace Brawler.Logic;

/// <summary>Address parsing for the join box and the lobby's internet check. Pure, so the tests can cover it.</summary>
public static class NetAddress
{
    /// <summary>
    /// Splits what someone typed in the join box: "1.2.3.4", "1.2.3.4:5000", "name.example.com:5000" or "[::1]:5000".
    /// A bare IPv6 address (several colons, no brackets) is kept whole.
    /// </summary>
    public static (string Host, int Port) Parse(string text, int fallbackPort)
    {
        text = (text ?? "").Trim();
        int port = fallbackPort;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close > 0)
            {
                string rest = text[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out int p6) && p6 is > 0 and < 65536) port = p6;
                return (text[1..close], port);
            }
        }
        int colon = text.IndexOf(':');
        if (colon > 0 && colon == text.LastIndexOf(':'))
        {
            if (int.TryParse(text[(colon + 1)..], out int p) && p is > 0 and < 65536) port = p;
            text = text[..colon];
        }
        return (text, port);
    }

    /// <summary>Private (RFC 1918) or carrier-grade (100.64.0.0/10) IPv4: not reachable from the internet.</summary>
    public static bool IsShared(string ip)
    {
        var parts = (ip ?? "").Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) return false;
        return a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168) || (a == 100 && b >= 64 && b <= 127);
    }
}
