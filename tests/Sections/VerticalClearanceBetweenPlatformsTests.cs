using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class VerticalClearanceBetweenPlatformsTests
{
    public static void Run()
    {
        int c = MapGenerator.MinClearance;
        float giantH = Body.H * StatBlock.Compute(new[] { CardRegistry.Get(43) })[Stat.Size];
        Check(c > giantH + 4 && c - Body.H >= Body.H, "MinClearance fits a Giant, and a normal body gets a body height of hop",
            $"clearance {c}, body {Body.H}, Giant {giantH:0.0}");
        Check(MapGenerator.FloorY - MapGenerator.TierY(1) <= MapGenerator.MaxRise && MapGenerator.TierSpacing + MapGenerator.Tile <= MapGenerator.MaxRise,
            "Lowest tier and each tier above it (raised or not) stay within MaxRise");

        // Everything solid a body can stand on or bonk into: solids, mover lanes (their whole travel) and glass.
        int seeds = 2000, fallbacks = 0, pairs = 0, bad = 0, minGap = int.MaxValue;
        var why = new List<string>();
        foreach (int players in new[] { 2, 3, 4, 5, 6, 7, 8 })
            for (int s = 0; s < seeds; s++)
            {
                int seed = s * 6151 + players * 17;
                var m = MapGenerator.Generate(seed, players);
                if (m.UsedFallback) fallbacks++;
                var blocks = new List<RectI>(m.Solids);
                blocks.AddRange(m.Movers.Select(mv => mv.Lane));
                blocks.AddRange(m.Props.Where(p => p.Kind == PropKind.Glass).Select(p => p.Rect));
                for (int i = 0; i < blocks.Count; i++)
                    for (int j = i + 1; j < blocks.Count; j++)
                    {
                        RectI a = blocks[i], b = blocks[j];
                        if (a.Right <= b.X || b.Right <= a.X) continue; // no horizontal overlap
                        int gap = a.Bottom <= b.Y ? b.Y - a.Bottom : b.Bottom <= a.Y ? a.Y - b.Bottom : -1;
                        if (gap <= 0) continue; // touching (a tower on the floor, glass tiles in a row) or overlapping
                        // Only open air counts: rock filling the gap (a cave's ceiling around a stalactite's root) joins them.
                        int ox1 = Math.Max(a.X, b.X), ox2 = Math.Min(a.Right, b.Right);
                        var air = new RectI(ox1, a.Bottom <= b.Y ? a.Bottom : b.Bottom, ox2 - ox1, gap);
                        if (blocks.Any(o => o != a && o != b && o.Intersects(air))) continue;
                        pairs++;
                        minGap = Math.Min(minGap, gap);
                        if (gap < c) { bad++; if (why.Count < 5) why.Add($"seed {seed} {m.Style}: {a} / {b} gap {gap}"); }
                    }
            }
        Check(bad == 0, "No two horizontally overlapping solids, movers or glass are closer than MinClearance",
            $"{pairs} stacked pairs, smallest gap {minGap}, {bad} bad. {string.Join("; ", why)}");
        Check(fallbacks <= seeds * 3 / 200, "Fallback rate stays under 0.5% with the clearance", $"{fallbacks}/{seeds * 3}");
    }
}
