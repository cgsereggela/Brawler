using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class CrosshairAmmoPipsTests
{
    public static void Run()
    {
        var bad = new List<string>();
        for (int mag = 1; mag <= 40; mag++)
        {
            if (AmmoPips.Continuous(mag) != (mag > AmmoPips.MaxPips)) bad.Add($"mag {mag}: wrong mode");
            if (AmmoPips.Continuous(mag)) continue;
            float w = AmmoPips.Width(mag), h = AmmoPips.Rows(mag) * (AmmoPips.PipH + AmmoPips.GapY) - AmmoPips.GapY;
            for (int i = 0; i < mag; i++)
            {
                var (x, y) = AmmoPips.Pip(i);
                // Inside the block, and never overlapping an earlier pip.
                if (x < AmmoPips.X0 - 1e-4f || y < AmmoPips.Y0 - 1e-4f || x + AmmoPips.PipW > AmmoPips.X0 + w + 1e-4f || y + AmmoPips.PipH > AmmoPips.Y0 + h + 1e-4f)
                    bad.Add($"mag {mag} pip {i} outside the block");
                for (int j = 0; j < i; j++)
                {
                    var (px, py) = AmmoPips.Pip(j);
                    if (Math.Abs(px - x) < AmmoPips.PipW && Math.Abs(py - y) < AmmoPips.PipH) bad.Add($"mag {mag} pips {j},{i} overlap");
                }
            }
        }
        Check(bad.Count == 0, "Ammo pips: one per bullet up to MaxPips, in rows, no overlap", string.Join("; ", bad.Take(5)));
        // The block (plate included) sits in the lower-right quadrant, off the right and bottom ticks (strokes reach ~1.7 off their axis).
        Check(AmmoPips.X0 - 1.6f > 1.8f && AmmoPips.Y0 - 1.6f > 1.8f, "Ammo pips sit at the lower right, off the cross's ticks");
        var maxRows = AmmoPips.Rows(AmmoPips.MaxPips);
        Check(maxRows <= 3 && AmmoPips.Width(AmmoPips.MaxPips) < 26, "Ammo pip block stays small", $"{maxRows} rows, {AmmoPips.Width(AmmoPips.MaxPips):0.0} wide");
        Check(Math.Abs(AmmoPips.Gauge(23, 40) - 23f / 40) < 1e-5f && AmmoPips.Gauge(0, 40) == 0 && AmmoPips.Gauge(99, 40) == 1,
            "Ammo gauge (big magazines) is proportional, clamped to the mag");
        var maxMag = StatBlock.Compute(Enumerable.Repeat(CardRegistry.Get(31), 6).ToList())[Stat.Ammo];
        Check(maxMag <= 40 && AmmoPips.Continuous((int)maxMag), "Stacked Minigun magazines fall back to the gauge", $"{maxMag}");
    }
}
