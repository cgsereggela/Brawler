using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class TerrainTests
{
    public static void Run()
    {
        var t1 = MapGenerator.Generate(777, 4);
        var t2 = MapGenerator.Generate(777, 4);
        int tseed = Enumerable.Range(0, 200).First(s => MapGenerator.Generate(s, 4).Slopes.Count > 0);
        var a = MapGenerator.Generate(tseed, 4);
        var b = MapGenerator.Generate(tseed, 4);
        Check(a.Slopes.SequenceEqual(b.Slopes) && a.Terrain.SequenceEqual(b.Terrain) && a.Solids.SequenceEqual(b.Solids)
            && a.Ground.Count == b.Ground.Count && a.Ground.Zip(b.Ground).All(p => p.First.SequenceEqual(p.Second))
            && a.Surfaces.SequenceEqual(b.Surfaces) && a.Spawns.SequenceEqual(b.Spawns) && t1.Slopes.SequenceEqual(t2.Slopes),
            "Same seed produces identical terrain", $"seed {tseed}");

        int maps = 3000, withTerrain = 0, slopes = 0, plateaus = 0, bad = 0, unreached = 0, steepest = 0;
        float maxAngle = 0;
        var byStyle = new Dictionary<MapStyle, (int maps, int terrain)>();
        var why = new List<string>();
        void Bad(int seed, string what) { bad++; if (why.Count < 6) why.Add($"seed {seed}: {what}"); }
        for (int s = 0; s < maps; s++)
        {
            int seed = s * 6151 + 3;
            var m = MapGenerator.Generate(seed, 2 + s % 3);
            var st = byStyle.GetValueOrDefault(m.Style);
            bool has = m.Slopes.Count > 0 || m.Terrain.Count > 0;
            byStyle[m.Style] = (st.maps + 1, st.terrain + (has ? 1 : 0));
            if (!has) continue;
            withTerrain++;
            slopes += m.Slopes.Count;
            plateaus += m.Terrain.Count;
            foreach (var sl in m.Slopes)
            {
                maxAngle = Math.Max(maxAngle, sl.Angle);
                if (sl.Angle > TerrainGen.MaxAngle) steepest++;
                if (sl.X2 <= sl.X1 || sl.Y1 == sl.Y2 || sl.Bottom != MapGenerator.FloorY) Bad(seed, $"degenerate slope {sl}");
                // Each end meets a surface at its own height, so it's walkable onto both.
                bool lowEnd = m.Surfaces.Any(f => f.Y == sl.Y1 && f.X2 == sl.X1), highEnd = m.Surfaces.Any(f => f.Y == sl.Y2 && f.X1 == sl.X2);
                if (!lowEnd || !highEnd) Bad(seed, $"slope {sl} has no surface at an end");
            }
            // Spawns on flat ground: on a surface, not on or inside any slope.
            foreach (var sp in m.Spawns)
                if (m.Slopes.Any(sl => sl.Overlaps(sp.X - 7, sp.Y - 18, sp.X + 7, sp.Y - 0.5f)) || MapGenerator.SurfaceIndexOf(m, sp) < 0)
                    Bad(seed, $"spawn {sp} not on flat ground");
            // Nothing that should sit on flat ground or float in clear air touches the terrain.
            foreach (var sl in m.Slopes)
            {
                if (m.Solids.Any(r => sl.Intersects(r))) Bad(seed, $"slope {sl} cuts into a solid");
                foreach (var p in m.Props) if (sl.Intersects(p.Rect)) Bad(seed, $"{p.Kind} on a slope");
                foreach (var h in m.Hazards) if (sl.Intersects(h.Rect)) Bad(seed, "pool on a slope");
                foreach (var pd in m.Pads) if (sl.Intersects(new RectI(pd.X1, pd.Y - MapGenerator.PadAir, pd.X2 - pd.X1, MapGenerator.PadAir))) Bad(seed, "pad on or under a slope");
                foreach (var mv in m.Movers) if (sl.Intersects(mv.Lane.Grow(0, 20))) Bad(seed, "mover lane through a slope");
                foreach (var sw in m.Saws) if (sl.Overlaps(sw.X1 - sw.Radius, sw.Y1 - sw.Radius, sw.X2 + sw.Radius, sw.Y1 + sw.Radius - 3)) Bad(seed, "saw in a slope");
            }
            foreach (var r in m.Terrain)
            {
                // Platforms keep clear air above terrain (towers stand away from it).
                if (m.Solids.Any(o => o != r && !m.Terrain.Contains(o) && o.Y < MapGenerator.FloorY && o.Bottom > r.Y - TerrainGen.Clearance && o.X < r.Right && o.Right > r.X))
                    Bad(seed, $"platform low over terrain {r}");
                if (m.Hazards.Any(h => h.Y != r.Y && h.Rect.Intersects(r))) Bad(seed, "pool cut into terrain");
            }
            // Every surface the spawns can reach is reachable from every spawn (validation still holds).
            if (!MapGenerator.SpawnsMutuallyReachable(m)) unreached++;
        }
        Check(steepest == 0 && maxAngle <= TerrainGen.MaxAngle, "Slopes stay walkable", $"steepest {maxAngle:0.0} deg, {steepest} too steep");
        Check(bad == 0, "Terrain: slopes meet surfaces, spawns stay flat, nothing intrudes", $"{bad} bad. {string.Join("; ", why)}");
        Check(unreached == 0, "Terrain maps keep every spawn reachable", $"{unreached} unreachable");
        string shares = string.Join(", ", byStyle.OrderBy(p => p.Key).Select(p => $"{p.Key} {100f * p.Value.terrain / p.Value.maps:0}%"));
        Check(withTerrain > maps / 4 && withTerrain < maps * 3 / 4 && byStyle.Where(p => TerrainGen.ChanceFor(p.Key) > 0).All(p => p.Value.terrain > p.Value.maps / 6),
            "A good share of maps (every style with a floor) have terrain", $"{100f * withTerrain / maps:0}% of {maps}: {shares}; {slopes} slopes, {plateaus} raised stretches");

        // Mirror arenas get mirrored terrain.
        int asym = 0;
        for (int s = 0; s < 600; s++)
        {
            var m = MapGenerator.Generate(s * 37 + 1, 2);
            if (m.Style != MapStyle.Mirror) continue;
            foreach (var sl in m.Slopes)
                if (!m.Slopes.Contains(new Slope(MapLayout.Width - sl.X2, sl.Y2, MapLayout.Width - sl.X1, sl.Y1, sl.Bottom))) asym++;
        }
        Check(asym == 0, "Mirror arenas have symmetric terrain", $"{asym} unmatched slopes");

        // The jump graph walks a ramp both ways; bots plan over it.
        var hill = new MapLayout();
        hill.Solids.Add(new RectI(8, 434, 784, 16));
        hill.Solids.Add(new RectI(240, 402, 64, 32));
        hill.Terrain.Add(new RectI(240, 402, 64, 32));
        hill.Slopes.Add(new Slope(192, 434, 240, 402, 434));
        hill.Slopes.Add(new Slope(304, 402, 352, 434, 434));
        hill.Surfaces.AddRange(new[] { new Surface(8, 192, 434), new Surface(240, 304, 402), new Surface(352, 792, 434) });
        Check(MapGenerator.SlopeLinks(hill, hill.Surfaces[0], hill.Surfaces[1]) && MapGenerator.SlopeLinks(hill, hill.Surfaces[1], hill.Surfaces[0])
            && MapGenerator.SlopeLinks(hill, hill.Surfaces[2], hill.Surfaces[1]) && !MapGenerator.SlopeLinks(hill, hill.Surfaces[0], hill.Surfaces[2]),
            "Ramps link the flat ground at their two ends");
        Check(MapGenerator.NextHop(hill, 0, 2) == 1 && MapGenerator.NextHop(MapGenerator.NavLayout(hill, 14, 18), 2, 0) == 1, "Paths go over a hill");
        Check(MapGenerator.SlopeSurface(hill, 200, 429) == 0 && MapGenerator.SlopeSurface(hill, 235, 405.5f) == 1 && MapGenerator.SlopeSurface(hill, 100, 434) == -1,
            "Standing on a ramp plans from its nearer end");
        Check(hill.Blocked(new RectI(210, 424, 4, 4)) && !hill.Blocked(new RectI(200, 400, 4, 4)), "Blocked() sees slopes exactly");

        // Bullets: hit points and bounce normals off a 33.7 degree ramp (rise 32 over run 48).
        var ramp = new Slope(100, 100, 148, 68, 100);
        var ramps = new List<Slope> { ramp };
        var none = new List<RectI>();
        var down = Sweep.Cast(new Vector2(124, 0), new Vector2(0, 200), 2, none, ramps);
        float topAt124 = ramp.YAt(124); // 84
        var nExpect = Vector2.Normalize(new Vector2(-32, -48));
        Check(down is { } dh && Vector2.Distance(dh.Normal, nExpect) < 1e-3f && Vector2.Distance(ramp.Normal, nExpect) < 1e-3f
            && Math.Abs(dh.T * 200 - (topAt124 - 2 / nExpect.Y * -1)) < 0.05f && dh.Index == -2,
            "Sweep lands on a ramp at the right point with its normal", down?.ToString() ?? "miss");
        var refl = Sweep.Reflect(new Vector2(0, 300), nExpect);
        Check(refl.X < 0 && refl.Y < 0 && Math.Abs(refl.Length() - 300) < 0.01f, "Bounce off an up-right ramp goes up and left", refl.ToString());
        var side = Sweep.Cast(new Vector2(200, 90), new Vector2(-100, 0), 1, none, ramps);
        Check(side is { } sh && sh.Normal == new Vector2(1, 0) && Math.Abs(200 - sh.T * 100 - 149) < 0.01f, "Sweep hits a ramp's tall side flat", side?.ToString() ?? "miss");
        var crest = Sweep.Cast(new Vector2(160, 40), new Vector2(-24, 56), 3, none, ramps);
        Check(crest is { } ch && Math.Abs(Vector2.Distance(new Vector2(160, 40) + new Vector2(-24, 56) * ch.T, new Vector2(148, 68)) - 3) < 0.01f,
            "Sweep rounds a ramp's corner", crest?.ToString() ?? "miss");
        Check(Sweep.Cast(new Vector2(110, 70), new Vector2(0, -60), 2, none, ramps) == null
            && Sweep.Cast(new Vector2(110, 99), new Vector2(0, 50), 2, none, ramps) == null, "Sweep misses above a ramp and ignores one it starts in");
        var both = Sweep.Cast(new Vector2(124, 0), new Vector2(0, 200), 2, new List<RectI> { new(120, 90, 10, 10) }, ramps);
        Check(both is { Index: -2 }, "Sweep picks the ramp when it's hit before a rect");
    }
}
