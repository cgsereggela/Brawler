using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class RngTests
{
    public static void Run()
    {
        var a = new Rng(42); var b = new Rng(42);
        bool same = Enumerable.Range(0, 1000).All(_ => a.NextUInt() == b.NextUInt());
        Check(same, "Rng is deterministic for equal seeds");
        var r = new Rng(7);
        bool inRange = Enumerable.Range(0, 10000).All(_ => { int v = r.Range(3, 9); return v >= 3 && v <= 9; });
        Check(inRange, "Rng.Range stays inclusive within bounds");
    }
}
