using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class DraftTests
{
    public static void Run()
    {
        Check(string.Join(",", Draft.Order(new[] { 4, 1, 7 }, 2)) == "4,1,7,7,1,4", "Draft: turns snake back (4,1,7 then 7,1,4)", string.Join(",", Draft.Order(new[] { 4, 1, 7 }, 2)));
        bool fits = true;
        for (int n = 2; n <= 8; n++) fits &= Draft.PoolSize(n) <= 10 && Draft.PoolSize(n) > n * Draft.PicksEach(n);
        Check(fits, "Draft: the pool holds everyone's picks with cards to spare, and never more than 10 (two rows on screen)");
    }
}
