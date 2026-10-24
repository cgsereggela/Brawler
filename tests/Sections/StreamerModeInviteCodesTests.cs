using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class StreamerModeInviteCodesTests
{
    public static void Run()
    {
        var addrs = new (string Ip, int Port)[] { ("192.168.1.20", 24565), ("0.0.0.0", 1), ("255.255.255.255", 65535), ("100.101.102.103", 7777), ("8.8.4.4", 24566) };
        var bad = addrs.Where(a => !Brawler.Logic.InviteCode.TryDecode(Brawler.Logic.InviteCode.Encode(a.Ip, a.Port), out var ip, out var p) || (ip, p) != a)
            .Select(a => a.Ip).ToList();
        Check(bad.Count == 0, "Invite codes round-trip IPv4 and port", string.Join(", ", bad));
        string code = Brawler.Logic.InviteCode.Encode("203.0.113.5", 24565);
        Check(code.Length == 14 && !code.Contains("203") && !code.Contains("113"), "Invite codes look like XXXX-XXXX-XXXX and hide the address", code);
        bool loose = Brawler.Logic.InviteCode.TryDecode(" " + code.ToLowerInvariant().Replace("-", " ").Replace('0', 'o') + " ", out var lip, out var lport) && lip == "203.0.113.5" && lport == 24565;
        Check(loose, "Invite codes ignore case, spacing and O/0 mix-ups", code);
        int typos = 0, caught = 0;
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '-') continue;
            char swap = code[i] == 'A' ? 'B' : 'A';
            typos++;
            if (!Brawler.Logic.InviteCode.TryDecode(code[..i] + swap + code[(i + 1)..], out _, out _)) caught++;
        }
        Check(caught >= typos - 1, "Invite code checksum catches single-character typos", $"{caught}/{typos}");
        bool rejects = !new[] { "", "1.2.3.4", "1.2.3.4:5000", "ABCD-EFGH", "localhost", "ABCD-EFGH-JKMN-PQRS" }.Any(t => Brawler.Logic.InviteCode.TryDecode(t, out _, out _))
            && Brawler.Logic.InviteCode.Encode("::1", 24565) == "" && Brawler.Logic.InviteCode.Encode("1.2.3.4", 0) == "";
        Check(rejects, "Addresses and junk are not mistaken for invite codes");
    }
}
