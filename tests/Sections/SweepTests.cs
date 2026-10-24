using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class SweepTests
{
    public static void Run()
    {
        var rects = new List<RectI> { new(100, 0, 20, 100) };
        var hit = Sweep.Cast(new Vector2(50, 50), new Vector2(100, 0), 2, rects);
        Check(hit is { } h && Math.Abs(h.T - 0.48f) < 0.001f && h.Normal == new Vector2(-1, 0),
            "Sweep hits wall with correct time and normal", hit?.ToString() ?? "miss");
        var miss = Sweep.Cast(new Vector2(50, 150), new Vector2(100, 0), 2, rects);
        Check(miss == null, "Sweep misses when passing below");
        var floorHit = Sweep.Cast(new Vector2(110, -20), new Vector2(0, 40), 1, rects);
        Check(floorHit is { } fh && fh.Normal == new Vector2(0, -1), "Sweep detects top-face hit");
        var refl = Sweep.Reflect(new Vector2(3, 4), new Vector2(-1, 0));
        Check(refl == new Vector2(-3, 4), "Reflect flips the normal component");
        Check(Sweep.SegmentTouchesRect(new Vector2(0, 5), new Vector2(20, 5), 1, 10, 0, 4, 10), "Segment crosses player box");
        var two = new List<RectI> { new(300, 0, 10, 100), new(100, 0, 20, 100) };
        Check(Sweep.Cast(new Vector2(50, 50), new Vector2(400, 0), 2, two) is { Index: 1 }, "Sweep reports which rect it hit first");
    }
}
