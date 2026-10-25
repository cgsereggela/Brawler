using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class PortalsTests
{
    public static void Run()
    {
        static bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < 0.01f;
        var right = new PortalRing(200, 400, 0);
        var left = new PortalRing(700, 300, 2);
        var up = new PortalRing(500, 100, 3);

        Check(PortalMath.Enters(right, new Vector2(205, 405), new Vector2(198, 405), out float t) && Math.Abs(t - 5) < 0.01f,
            "Portal: walking into a ring from its front goes in, at the spot along it you crossed");
        Check(!PortalMath.Enters(right, new Vector2(198, 405), new Vector2(205, 405), out _), "Portal: crossing a ring from behind does nothing");
        Check(!PortalMath.Enters(right, new Vector2(205, 420), new Vector2(198, 420), out _), "Portal: passing beside a ring's opening does nothing");

        // Doorway to doorway: in at the same height, out at the same speed, falling the same.
        var (p, v) = PortalMath.Through(right, left, new Vector2(198, 405), new Vector2(-150, 40), 6);
        Check(Near(p, new Vector2(694, 305)) && Near(v, new Vector2(-150, 40)), "Portal: doorway to doorway keeps the speed and the height along it", $"{p} {v}");
        var same = new PortalRing(700, 300, 0);
        (p, v) = PortalMath.Through(right, same, new Vector2(198, 405), new Vector2(-150, 40), 6);
        Check(Near(p, new Vector2(706, 305)) && Near(v, new Vector2(150, 40)), "Portal: out of a ring facing the other way, the speed turns round and the fall stays a fall", $"{p} {v}");
        // Doorway to a hole facing up: walking in sideways comes out going up.
        (p, v) = PortalMath.Through(right, up, new Vector2(198, 400), new Vector2(-200, 0), 10);
        Check(Near(p, new Vector2(500, 90)) && Near(v, new Vector2(0, -200)), "Portal: a doorway into a ring facing up shoots you up at the same speed", $"{p} {v}");
        // Falling into a hole facing up and out of one facing down keeps falling, sideways speed kept.
        var down = new PortalRing(300, 50, 1);
        (p, v) = PortalMath.Through(up, down, new Vector2(503, 100), new Vector2(30, 250), 4);
        Check(Near(p, new Vector2(303, 54)) && Near(v, new Vector2(30, 250)), "Portal: fall into a hole facing up, fall out of one facing down", $"{p} {v}");
        (_, v) = PortalMath.Through(right, left, new Vector2(198, 405), new Vector2(-20, 0), 6, PortalMath.MinBodySpeed);
        Check(Math.Abs(v.X + PortalMath.MinBodySpeed) < 0.01f, "Portal: a slow walk in still carries a body clear of the exit", $"{v}");
        var pairs = new List<PortalPair> { new(right, left) };
        Check(PortalMath.Find(pairs, new Vector2(690, 305), new Vector2(702, 305), out var pair, out bool fromA) && !fromA && PortalMath.Other(pair, fromA) == right,
            "Portal: either ring of a pair leads to the other");
        Check(right.Turned().Dir == 1 && right.Turned().Turned().Turned().Turned() == right, "Portal: turning a ring four times brings it back");

        // Generated pairs: seeded, on the ground, well apart, out of everything's way, and bots can use them.
        int maps = 1500, pairsSeen = 0, bad = 0, botEdges = 0;
        var why = new List<string>();
        void Bad(int seed, string what) { bad++; if (why.Count < 5) why.Add($"seed {seed}: {what}"); }
        for (int s = 0; s < maps; s++)
        {
            int seed = s * 7919 + 3;
            var m = MapGenerator.Generate(seed, 2 + s % 3);
            if (!MapGenerator.Generate(seed, 2 + s % 3).Portals.SequenceEqual(m.Portals)) Bad(seed, "portals differ for the same seed");
            foreach (var pp in m.Portals)
            {
                pairsSeen++;
                foreach (var r in new[] { pp.A, pp.B })
                {
                    var surface = m.Surfaces.FirstOrDefault(su => PortalMath.StandsOn(r, su));
                    if (surface == default) Bad(seed, $"ring {r} isn't standing on the ground");
                    var room = new RectI(r.X - 20, r.Y + PortalRing.Half - 34, 40, 34);
                    if (m.Blocked(room)) Bad(seed, $"ring {r} has no room for a body");
                    if (m.Spawns.Any(sp => Math.Abs(sp.X - r.X) < 50 && Math.Abs(sp.Y - (r.Y + PortalRing.Half)) < 40)) Bad(seed, "ring at a spawn");
                    if (m.Props.Any(pr => pr.Rect.Intersects(room)) || m.Hazards.Any(h => h.Rect.Intersects(room)) || m.Conveyors.Any(c => c.Rect.Intersects(room.Grow(4, 4))))
                        Bad(seed, "ring crowded by a prop, pool or belt");
                }
                if (Math.Abs(pp.A.X - pp.B.X) < 260 && Math.Abs(pp.A.Y - pp.B.Y) < 80) Bad(seed, "rings too close together");
            }
            if (m.Portals.Count > 0)
            {
                var nav = MapGenerator.NavLayout(m, 12, 18);
                var pp = m.Portals[0];
                var sa = nav.Surfaces.FindIndex(su => PortalMath.StandsOn(pp.A, su));
                var sb = nav.Surfaces.FindIndex(su => PortalMath.StandsOn(pp.B, su));
                if (sa >= 0 && sb >= 0 && MapGenerator.PortalTo(nav, nav.Surfaces[sa], nav.Surfaces[sb]) is not null && MapGenerator.CanTraverse(nav, nav.Surfaces[sa], nav.Surfaces[sb])) botEdges++;
            }
        }
        Check(bad == 0, "Portal: generated pairs stand on the ground, apart, with room, clear of spawns and extras", $"{bad} bad. {string.Join("; ", why)}");
        Check(pairsSeen > maps * ArenaFeatures.PortalChance * 0.6f && pairsSeen < maps * ArenaFeatures.PortalChance * 1.2f, "Portal: about one arena in four has a pair", $"{pairsSeen}/{maps}");
        Check(botEdges > pairsSeen * 0.7f, "Portal: bots' jump graph walks through generated portals", $"{botEdges}/{pairsSeen}");

        // A pair on a floor split by a wall too high to jump: bots can still get across, but the arena check never counts on it.
        var walled = new MapLayout { Seed = 1 };
        MapGenerator.AddBorders(walled);
        walled.Solids.Add(new RectI(MapGenerator.Wall, MapGenerator.FloorY, MapLayout.Width - MapGenerator.Wall * 2, 32));
        walled.Solids.Add(new RectI(490, 100, 20, MapGenerator.FloorY - 100));
        var lhs = new Surface(MapGenerator.Wall, 490, MapGenerator.FloorY);
        var rhs = new Surface(510, MapLayout.Width - MapGenerator.Wall, MapGenerator.FloorY);
        walled.Surfaces.Add(lhs);
        walled.Surfaces.Add(rhs);
        walled.Portals.Add(new PortalPair(new PortalRing(200, MapGenerator.FloorY - PortalRing.Half, 0), new PortalRing(800, MapGenerator.FloorY - PortalRing.Half, 2)));
        var walledNav = MapGenerator.NavLayout(walled, 12, 18);
        Check(!MapGenerator.CanTraverse(walled, lhs, rhs) && MapGenerator.CanTraverse(walledNav, walledNav.Surfaces[0], walledNav.Surfaces[1])
              && MapGenerator.NextHop(walledNav, 0, 1) == 1 && MapGenerator.CanTraverse(walledNav, walledNav.Surfaces[1], walledNav.Surfaces[0]),
            "Portal: bots path through a portal both ways; map validation never relies on one");
    }

}
