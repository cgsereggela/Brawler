using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class ThickFloorBandTests
{
    public static void Run()
    {
        int floorY = MapGenerator.FloorY, depth = MapGenerator.FloorDepth;
        Check(floorY == MapLayout.Height - depth && depth >= 18 + 2 * 6, "Floor band is FloorDepth thick and fits the 18 px card strip with margins",
            $"depth {depth}, top {floorY}");
        Check(MapGenerator.TierY(9) >= MapGenerator.Wall + MapGenerator.MinClearance, "The highest tier a layout asks for still fits under the ceiling",
            $"tier 9 at {MapGenerator.TierY(9)}");

        // Nothing playable ends up inside the band: it is all floor (or pit) from FloorY down.
        int bad = 0;
        var why = new List<string>();
        void Bad(int seed, string what) { bad++; if (why.Count < 5) why.Add($"seed {seed}: {what}"); }
        for (int s = 0; s < 3000; s++)
        {
            int seed = s * 7919 + 3;
            var m = MapGenerator.Generate(seed, 2 + s % 3);
            foreach (var r in m.Solids)
                if (r.Y == floorY && r.Bottom != MapLayout.Height) Bad(seed, $"floor block {r} does not reach the bottom");
                else if (r.Y > floorY) Bad(seed, $"solid {r} inside the floor band");
            foreach (var su in m.Surfaces) if (su.Y > floorY) Bad(seed, $"surface {su}");
            foreach (var sp in m.Spawns) if (sp.Y > floorY) Bad(seed, $"spawn {sp}");
            foreach (var p in m.Pads) if (p.Y > floorY) Bad(seed, $"pad {p}");
            foreach (var p in m.Props) if (p.Y > floorY) Bad(seed, $"prop {p}");
            foreach (var h in m.Hazards) if (h.Y > floorY) Bad(seed, $"pool {h}");
            foreach (var sw in m.Saws) if (Math.Max(sw.Y1, sw.Y2) > floorY) Bad(seed, $"saw {sw}");
            foreach (var mv in m.Movers) if (mv.Lane.Bottom > floorY) Bad(seed, $"mover {mv}");
        }
        Check(bad == 0, "No surface, spawn, pad, prop, pool, saw or mover sits inside the floor band (3,000 maps)", string.Join("; ", why));
    }
}
