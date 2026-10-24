using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class MapGenerationTests
{
    public static void Run()
    {
        var m1 = MapGenerator.Generate(12345, 4);
        var m2 = MapGenerator.Generate(12345, 4);
        bool same = m1.Solids.SequenceEqual(m2.Solids) && m1.Spawns.SequenceEqual(m2.Spawns);
        Check(same, "Same seed produces identical map");

        int seeds = 3000;
        foreach (int players in new[] { 2, 3, 4, 5, 6, 7, 8 })
        {
            int fallbacks = 0, totalAttempts = 0, maxAttempts = 0, bad = 0;
            for (int s = 0; s < seeds; s++)
            {
                var m = MapGenerator.Generate(s * 7919 + players, players);
                if (m.UsedFallback) fallbacks++;
                totalAttempts += m.Attempts;
                maxAttempts = Math.Max(maxAttempts, m.Attempts);
                if (m.Spawns.Count != players || !MapGenerator.SpawnsMutuallyReachable(m)) bad++;
                // Spawns must not be inside a solid.
                foreach (var sp in m.Spawns)
                    if (m.Solids.Any(r => r.Contains(sp.X, sp.Y - 8))) bad++;
            }
            Check(bad == 0, $"{players}p maps: valid spawns, mutually reachable", $"{bad} bad");
            Check(fallbacks <= seeds / 200, $"{players}p maps: fallback rate under 0.5%",
                $"{fallbacks}/{seeds} fallbacks, avg attempts {totalAttempts / (float)seeds:F1}, max {maxAttempts}");
        }

        var styles = new HashSet<MapStyle>();
        int pads = 0, badPads = 0;
        for (int s = 0; s < 400; s++)
        {
            var m = MapGenerator.Generate(s * 31 + 5, 4);
            styles.Add(m.Style);
            pads += m.Pads.Count;
            foreach (var p in m.Pads)
            {
                if (!m.Surfaces.Any(sf => sf.Y == p.Y && p.X1 >= sf.X1 && p.X2 <= sf.X2)) badPads++;
                if (m.Spawns.Any(sp => sp.Y == p.Y && sp.X > p.X1 - 24 && sp.X < p.X2 + 24)) badPads++;
            }
        }
        Check(styles.Count == Enum.GetValues<MapStyle>().Length, "Every map style shows up", string.Join(",", styles));
        Check(pads > 100 && badPads == 0, "Jump pads sit on surfaces, away from spawns", $"{pads} pads, {badPads} bad");
        Check(MapLayout.Width == 1000 && MapLayout.Height == 562, "Arena is 1000x562");

        // Cave, tunnel and temple layouts: each builds without falling back, and its signature part is really there
        // and in play (reachable from the spawns), not just scenery.
        {
            var made = new Dictionary<MapStyle, (int maps, int fallbacks, int bad)>();
            var why = new List<string>();
            for (int s = 0; s < 2100; s++)
            {
                int seed = s * 104729 + 11, players = 2 + s % 3;
                var want = MapGenerator.StyleFor(seed);
                if (want is not (MapStyle.Cave or MapStyle.Tunnel or MapStyle.Temple)) continue;
                var m = MapGenerator.Generate(seed, players);
                var st = made.GetValueOrDefault(want);
                st.maps++;
                if (m.UsedFallback) { st.fallbacks++; made[want] = st; continue; }
                int spawn = MapGenerator.SurfaceIndexOf(m, m.Spawns[0]);
                var reach = MapGenerator.ReachableFrom(m, spawn);
                bool Reached(Func<Surface, bool> f) => m.Surfaces.Where((x, i) => reach[i] && f(x)).Any();
                string fail = null;
                var hanging = m.Solids.Where(r => r.Y < MapGenerator.Wall && r.X > 0 && r.Right < MapLayout.Width).ToList();
                var standing = m.Solids.Where(r => r.Bottom == MapGenerator.FloorY && !m.Terrain.Contains(r)).ToList();
                switch (want)
                {
                    case MapStyle.Cave:
                        // A ceiling all the way across, stalactites below it, stalagmites one hop tall.
                        if (hanging.Sum(r => r.W) != MapLayout.Width - 2 * MapGenerator.Wall) fail = "ceiling has holes";
                        else if (!m.Solids.Any(r => r.Y > 40 && r.Bottom < 260 && r.W <= 24)) fail = "no stalactites";
                        else if (standing.Any(r => r.H > MapGenerator.MaxRise)) fail = "stalagmite too tall to hop onto";
                        break;
                    case MapStyle.Tunnel:
                    {
                        // Long slabs (two-tile-thick solids) split the arena; the top of the highest one is reachable.
                        var slabs = m.Solids.Where(r => r.H == 16 && r.Y > MapGenerator.Wall && r.Bottom < MapGenerator.FloorY).GroupBy(r => r.Y)
                            .Where(g => g.Sum(r => r.W) > 300).Select(g => g.Key).ToList();
                        if (slabs.Count is < 1 or > 2) fail = $"{slabs.Count} slabs";
                        else if (!Reached(x => x.Y == slabs.Min())) fail = "top corridor unreachable";
                        else if (!Reached(x => x.Y == MapGenerator.FloorY)) fail = "bottom corridor unreachable";
                        else if (hanging.Count == 0) fail = "no lowered ceiling";
                        else
                        {
                            // Every corridor (floor or slab top up to the next slab's or the ceiling's bottom) is tall enough to jump in.
                            var floors = slabs.Append(MapGenerator.FloorY).OrderBy(y => y).ToList();
                            var roofs = slabs.Select(y => y + 16).Prepend(hanging.Max(r => r.Bottom)).OrderBy(y => y).ToList();
                            if (floors.Zip(roofs).Any(p => p.First - p.Second < 134)) fail = "corridor too low";
                        }
                        break;
                    }
                    case MapStyle.Temple:
                    {
                        // Steps rising to a flat top, which is reachable.
                        int topY = standing.Count == 0 ? MapGenerator.FloorY : standing.Min(r => r.Y);
                        if (standing.Count < 5) fail = $"only {standing.Count} steps";
                        else if (!Reached(x => x.Y == topY)) fail = "temple top unreachable";
                        break;
                    }
                }
                if (fail != null) { st.bad++; if (why.Count < 6) why.Add($"seed {seed} {want}: {fail}"); }
                made[want] = st;
            }
            string counts = string.Join(", ", made.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value.maps} ({p.Value.fallbacks} fallbacks)"));
            Check(made.Count == 3 && made.Values.All(v => v.maps > 150 && v.fallbacks <= v.maps / 200), "Cave, tunnel and temple maps build", counts);
            Check(made.Values.All(v => v.bad == 0), "Cave, tunnel and temple maps have their parts, in reach", string.Join("; ", why));
        }

        // Sky arenas: no floor at all, a row of islands along the bottom tier, and everyone can reach everyone.
        {
            int sky = 0, skyFallbacks = 0, skyBad = 0, kept = 0, others = 0;
            string skyWhy = "";
            for (int s = 0; s < 3000; s++)
            {
                int seed = s * 7919 + 3, players = 2 + s % 7;
                var style = MapGenerator.StyleFor(seed);
                if (style == MapStyle.Drift) continue; // checked below
                if (style != MapStyle.Sky)
                {
                    // Seeds that aren't Sky keep the style they always had (their first roll).
                    others++;
                    if (style == (MapStyle)new Rng((ulong)(uint)seed).Range(0, 6)) kept++;
                    continue;
                }
                sky++;
                var m = MapGenerator.Generate(seed, players);
                if (m.UsedFallback) { skyFallbacks++; continue; }
                string fail = null;
                if (m.Solids.Any(r => r.Bottom == MapLayout.Height && r.X > 0 && r.Right < MapLayout.Width)) fail = "has a floor";
                else if (m.Surfaces.Any(x => x.Y == MapGenerator.FloorY)) fail = "a surface at floor height";
                else if (m.Solids.Count(r => r.Y == MapGenerator.TierY(1)) < 2) fail = "fewer than two bottom islands";
                else if (!MapGenerator.SpawnsMutuallyReachable(m)) fail = "spawns can't reach each other";
                if (fail != null) { skyBad++; if (skyWhy == "") skyWhy = $"seed {seed}: {fail}"; }
            }
            Check(sky > 200 && skyFallbacks <= sky / 200, "Sky maps build", $"{sky} maps, {skyFallbacks} fallbacks");
            Check(skyBad == 0, "Sky maps have no floor, islands along the bottom, and spawns in reach of each other", skyWhy);
            Check(kept == others, "Seeds that aren't Sky or Drift keep their old style", $"{kept}/{others}");
            var empty = MapDesign.Empty(ThemeId.SunsetPeaks, 1, MapStyle.Sky);
            var built = empty.Build(4);
            Check(empty.Floor().Count == 0 && empty.Style == MapStyle.Sky && built.Surfaces.Count == 0
                  && MapCode.Decode(MapCode.Encode(empty))?.Floor().Count == 0, "Design: an empty Sky arena has no floor, also after a map code");
        }

        // Drift arenas: nothing but drifting platforms over a bottomless drop, and everyone can reach everyone.
        {
            int maps = 0, fallbacks = 0, bad = 0, platforms = 0;
            string why = "";
            for (int s = 0; s < 4000; s++)
            {
                int seed = s * 7919 + 5, players = 2 + s % 7;
                if (MapGenerator.StyleFor(seed) != MapStyle.Drift) continue;
                maps++;
                var m = MapGenerator.Generate(seed, players);
                if (m.UsedFallback) { fallbacks++; continue; }
                platforms += m.Drifts.Count;
                string fail = null;
                if (m.Solids.Any(r => r.X > 0 && r.Right < MapLayout.Width && r.Y > 0)) fail = "has a solid besides the walls";
                else if (m.Surfaces.Count != m.Drifts.Count || m.Surfaces.Any(x => m.DriftIndex(x) < 0)) fail = "a surface that isn't a drifting platform";
                else if (m.Props.Count + m.Hazards.Count + m.Saws.Count + m.Movers.Count + m.Pads.Count > 0) fail = "extras on a Drift arena";
                else if (m.Drifts.Any(a => m.Drifts.Any(b => a != b && a.Range.Intersects(b.Range) && Math.Abs(a.Y - b.Y) < MapGenerator.DriftRowSpacing)))
                    fail = "two platforms can be shoved into each other";
                else if (m.Drifts.Any(d => d.Range.X < MapGenerator.Wall || d.Range.Right > MapLayout.Width - MapGenerator.Wall || d.Range.Y < MapGenerator.Wall))
                    fail = "a platform can be shoved into the walls";
                else if (!MapGenerator.SpawnsMutuallyReachable(m)) fail = "spawns can't reach each other";
                if (fail != null) { bad++; if (why == "") why = $"seed {seed}: {fail}"; }
            }
            Check(maps > 150 && fallbacks <= maps / 100, "Drift maps build", $"{maps} maps, {fallbacks} fallbacks, {platforms / Math.Max(1, maps - fallbacks)} platforms each");
            Check(bad == 0, "Drift maps are only drifting platforms that can't be shoved into each other or the walls, with spawns in reach", why);
            var gen = MapDesign.Generate(MapStyle.Drift, ThemeId.DeepReef, 1);
            var back = MapCode.Decode(MapCode.Encode(gen));
            Check(gen.Style == MapStyle.Drift && gen.Blocks.Count == 0 && gen.Drifts.Count > 5 && back != null && back.Style == MapStyle.Drift
                  && back.Drifts.SequenceEqual(gen.Drifts) && back.Problems().Count == 0, "Design: a generated Drift arena survives a map code with no problems",
                  string.Join(" | ", back?.Problems() ?? new List<string>()));
        }

        // A pad launch still clears the planned climb under the softer gravity.
        float padApex = MapGenerator.PadVelocity * MapGenerator.PadVelocity / (2 * MapGenerator.BaseGravity);
        Check(padApex >= MapGenerator.PadRise + 5 && MapGenerator.BaseGravity == StatBlock.Base()[Stat.Gravity],
            "Pad launch apex covers PadRise at base gravity", $"apex {padApex:0}, rise {MapGenerator.PadRise}");
        float jumpApex = StatBlock.Base()[Stat.JumpVelocity] * StatBlock.Base()[Stat.JumpVelocity] / (2 * StatBlock.Base()[Stat.Gravity]);
        Check(jumpApex >= MapGenerator.MaxRise + 12, "Base jump clears MaxRise with margin", $"apex {jumpApex:0}");
    }
}
