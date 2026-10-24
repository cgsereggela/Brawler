using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class JoinAddressesTests
{
    public static void Run()
    {
        var cases = new (string Text, string Host, int Port)[]
        {
            ("1.2.3.4", "1.2.3.4", 24565), (" 1.2.3.4:5000 ", "1.2.3.4", 5000), ("game.example.com:7000", "game.example.com", 7000),
            ("[::1]:6000", "::1", 6000), ("fe80::1", "fe80::1", 24565), ("1.2.3.4:abc", "1.2.3.4", 24565), ("1.2.3.4:70000", "1.2.3.4", 24565),
        };
        var wrong = cases.Where(c => Brawler.Logic.NetAddress.Parse(c.Text, 24565) != (c.Host, c.Port)).Select(c => c.Text).ToList();
        Check(wrong.Count == 0, "Join box parses ip, ip:port, host:port and [ipv6]:port", string.Join(", ", wrong));
        bool shared = new[] { "10.0.0.1", "172.16.5.5", "172.31.0.1", "192.168.1.1", "100.64.0.1", "100.127.255.255" }.All(Brawler.Logic.NetAddress.IsShared)
            && !new[] { "8.8.8.8", "172.32.0.1", "100.128.0.1", "203.0.113.5", "", "nonsense" }.Any(Brawler.Logic.NetAddress.IsShared);
        Check(shared, "Private and carrier-grade NAT addresses are recognised");
    }
}
