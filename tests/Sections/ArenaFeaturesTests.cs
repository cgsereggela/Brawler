using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class ArenaFeaturesTests
{
    public static void Run()
    {
        var a1 = MapGenerator.Generate(4242, 4);
        var a2 = MapGenerator.Generate(4242, 4);
        Check(a1.Props.SequenceEqual(a2.Props) && a1.Hazards.SequenceEqual(a2.Hazards) && a1.Saws.SequenceEqual(a2.Saws) && a1.Movers.SequenceEqual(a2.Movers) && a1.Conveyors.SequenceEqual(a2.Conveyors),
            "Same seed produces identical arena features");

        int maps = 2000, crates = 0, barrels = 0, glass = 0, pools = 0, saws = 0, movers = 0, drifts = 0, hangers = 0, belts = 0, bad = 0;
        var why = new List<string>();
        void Bad(int seed, string what) { bad++; if (why.Count < 5) why.Add($"seed {seed}: {what}"); }
        for (int s = 0; s < maps; s++)
        {
            int seed = s * 104729 + 11;
            var m = MapGenerator.Generate(seed, 2 + s % 3);
            foreach (var p in m.Props)
            {
                if (p.Kind == PropKind.Crate) crates++; else if (p.Kind == PropKind.Barrel) barrels++; else glass++;
                if (m.Solids.Any(r => r.Intersects(p.Rect))) Bad(seed, $"{p.Kind} inside a solid");
                if (m.Props.Any(o => o != p && o.Rect.Intersects(p.Rect))) Bad(seed, $"{p.Kind} overlaps another prop");
                if (m.Spawns.Any(sp => Math.Abs(sp.X - (p.X + p.W / 2)) < 26 + p.W / 2 && sp.Y - 40 < p.Y + p.H && p.Y < sp.Y + 6))
                    Bad(seed, $"{p.Kind} on a spawn");
                if (m.Movers.Any(mv => mv.Lane.Grow(0, 20).Intersects(p.Rect))) Bad(seed, $"{p.Kind} in a mover's lane");
            }
            foreach (var h in m.Hazards)
            {
                pools++;
                if (!m.Surfaces.Any(sf => sf.Y == h.Y && h.X1 >= sf.X1 && h.X2 <= sf.X2)) Bad(seed, "pool not on a surface");
                if (m.Spawns.Any(sp => sp.Y == h.Y && sp.X > h.X1 - 26 && sp.X < h.X2 + 26)) Bad(seed, "pool at a spawn");
                if (m.Pads.Any(pd => pd.Y == h.Y && pd.X2 > h.X1 - 10 && pd.X1 < h.X2 + 10)) Bad(seed, "pool on a jump pad");
            }
            foreach (var sw in m.Saws)
            {
                saws++;
                if (!m.Surfaces.Any(sf => Math.Abs(sf.Y - 2 - sw.Y1) < 0.01f && sw.X1 >= sf.X1 && sw.X2 <= sf.X2)) Bad(seed, "saw off its surface");
                // The whole blade, end to end of its run, stays over ground with a little to spare.
                int top = (int)MathF.Round(sw.Y1 + 2);
                for (int x = (int)MathF.Floor(sw.X1 - sw.Radius - 4); x < (int)MathF.Ceiling(sw.X2 + sw.Radius + 4); x++)
                    if (!m.Solids.Any(r => r.Y == top && x >= r.X && x < r.Right)) { Bad(seed, $"saw overhangs its platform at x={x} ({sw})"); break; }
                if (m.Spawns.Any(sp => sp.X > sw.X1 - 40 && sp.X < sw.X2 + 40 && Math.Abs(sp.Y - sw.Y1) < 30)) Bad(seed, "saw near a spawn");
            }
            foreach (var mv in m.Movers)
            {
                movers++;
                var room = new RectI(mv.Lane.X, mv.Y - 30, mv.Lane.W, mv.H + 30 + 20);
                if (m.Solids.Any(r => r.Intersects(room))) Bad(seed, "mover lane blocked");
                if (mv.Period <= 0 || mv.X2 <= mv.X1) Bad(seed, "mover goes nowhere");
            }
            foreach (var dr in m.Drifts)
            {
                drifts++;
                if (m.Solids.Any(r => r.Intersects(dr.Range))) Bad(seed, "drift can be shoved into a solid");
                if (m.Props.Any(p => p.Rect.Intersects(dr.Range)) || m.Movers.Any(mv => mv.Lane.Intersects(dr.Range))) Bad(seed, "drift range crowded");
                // (On a Drift arena the platforms are the arena, so spawns stand on them.)
                if (m.Style != MapStyle.Drift && m.Spawns.Any(sp => dr.Range.Grow(0, 40).Contains(sp.X, sp.Y))) Bad(seed, "drift over a spawn");
                if (!m.Surfaces.Contains(dr.Top)) Bad(seed, "drift top not a surface");
            }
            foreach (var c in m.Conveyors)
            {
                belts++;
                if (!Conveyor.OnBlock(m, c) || c.X % 8 != 0 || c.W % 8 != 0 || c.Dir is not (1 or -1)) Bad(seed, $"belt {c} not along a block top on the grid");
                var zone = new RectI(c.X, c.Y - 30, c.W, 30);
                if (m.Spawns.Any(sp => sp.Y == c.Y && sp.X > c.X - 26 && sp.X < c.X + c.W + 26)) Bad(seed, "belt at a spawn");
                if (m.Pads.Any(p => p.Y == c.Y && p.X2 > c.X - 10 && p.X1 < c.X + c.W + 10) || m.Hazards.Any(z => z.Y == c.Y && z.X2 > c.X && z.X1 < c.X + c.W))
                    Bad(seed, "belt under a pad or pool");
                if (m.Props.Any(p => p.Rect.Intersects(zone)) || m.Slopes.Any(sl => sl.Intersects(zone.Grow(8, 2))) || m.Conveyors.Any(o => o != c && o.Rect.Intersects(c.Rect.Grow(8, 0))))
                    Bad(seed, "belt crowded by a prop, ramp or belt");
            }
            foreach (var h in m.Hangers)
            {
                hangers++;
                var anchors = HangerMath.Anchors(m, h);
                for (int k = 0; k < anchors.Length; k++)
                {
                    var chain = new RectI(h.ChainXs[k] - 2, anchors[k], 4, h.Y - anchors[k]);
                    if (anchors[k] < MapGenerator.Wall || m.Solids.Any(r => r.Intersects(chain))) Bad(seed, "hanger chain through a solid");
                    if (m.Props.Any(p => p.Rect.Intersects(chain)) || m.Drifts.Any(dr => dr.Range.Intersects(chain))) Bad(seed, "hanger chain through an extra");
                    int cx = h.ChainXs[k], at = anchors[k];
                    if (!m.Solids.Any(r => r.Bottom == at && r.X <= cx - HangerMath.AnchorMargin && r.Right >= cx + HangerMath.AnchorMargin))
                        Bad(seed, "hanger chain bolted too near the edge of what it hangs from");
                }
                if (m.Solids.Any(r => r.Intersects(h.Rect.Grow(16, MapGenerator.MinClearance)))) Bad(seed, "hanger boxed in");
                if (!HangerMath.LandsFlat(m, h)) Bad(seed, "hanger lands on a slope");
                if (HangerMath.LandY(m, h) is { } ly)
                {
                    var landed = new RectI(h.X, ly, h.W, h.H);
                    if (m.Spawns.Any(sp => sp.Y == ly + h.H && sp.X > h.X - 26 && sp.X < h.X + h.W + 26) || m.Pads.Any(p => p.Y == ly + h.H && p.X2 > h.X && p.X1 < h.X + h.W)
                        || m.Hazards.Any(z => z.Rect.Intersects(landed.Grow(4, 0))))
                        Bad(seed, "hanger lands on a spawn, pad or pool");
                }
            }
        }
        Check(bad == 0, "Arena features stay clear of solids, spawns, pads and each other", $"{bad} bad. {string.Join("; ", why)}");
        Check(crates > maps && barrels > maps / 4 && glass > maps / 2 && pools > maps / 3 && saws > maps / 4 && movers > maps / 5 && drifts > maps / 5 && hangers > maps / 4 && belts > maps / 4,
            "Every arena feature shows up often", $"per {maps} maps: {crates} crates, {barrels} barrels, {glass} glass, {pools} pools, {saws} saws, {movers} movers, {drifts} drifts, {hangers} hangers, {belts} belts");

        var belt = new Conveyor(200, 400, 80, -1);
        var beltList = new List<Conveyor> { belt };
        Check(Conveyor.Under(beltList, 240, 400.5f) == -Conveyor.Speed && Conveyor.Under(beltList, 290, 400) == 0 && Conveyor.Under(beltList, 240, 380) == 0
              && Conveyor.Under(null, 240, 400) == 0,
              "A belt carries whoever stands on it, its way, and nobody beside or above it");
        Check(Conveyor.Speed < StatBlock.Base()[Stat.MoveSpeed] * 0.6f, "Walking against a belt is slow but always gets somewhere");

        var track = new SawTrack(100, 50, 200, 50, 9, 4, 0);
        Check(Vector2.Distance(track.At(0), new Vector2(100, 50)) < 0.01f && Vector2.Distance(track.At(2), new Vector2(200, 50)) < 0.01f
            && Vector2.Distance(track.At(4), new Vector2(100, 50)) < 0.01f, "Saws shuttle end to end and back each period");
        var mover = new Mover(40, 120, 300, 40, 8, 3, 0.25f);
        Check(Math.Abs(mover.XAt(0) - 80) < 0.01f && Enumerable.Range(0, 100).All(i => mover.XAt(i * 0.07f) is >= 40 and <= 120),
            "Movers stay inside their lane", $"{mover.XAt(0)}");
        {
            // A drifting platform shoved hard stays within its reach and eases back home.
            var drift = new Drift(300, 200, 56, 8);
            var (off, vel) = (Vector2.Zero, new Vector2(900, -400));
            bool inside = true;
            for (int i = 0; i < 60 * 6; i++)
            {
                off = DriftMotion.Step(drift, off, ref vel, Vector2.Zero, 1f / 60);
                inside &= Math.Abs(off.X) <= Drift.ReachX && Math.Abs(off.Y) <= Drift.ReachY;
            }
            Check(inside && off.Length() < 0.5f, "Drifting platforms stay within reach and settle back home", $"{off}");
            var wall = new Drift(MapGenerator.Wall + 4, 200, 56, 8);
            var (wo, wv) = (Vector2.Zero, new Vector2(-900, 0));
            float least = 0;
            for (int i = 0; i < 60; i++) { wo = DriftMotion.Step(wall, wo, ref wv, Vector2.Zero, 1f / 60); least = Math.Min(least, wo.X); }
            Check(wall.X + least >= MapGenerator.Wall, "Drifting platforms can't be shoved into the arena wall", $"{least}");
        }
        {
            // Hanging platforms: chains bolted to the block above (or the ceiling), landing on the floor or out through a pit.
            var hd = MapDesign.Empty(ThemeId.SunsetPeaks, 1);
            hd.Blocks.Add(new RectI(100, 120, 200, 16));
            hd.Pits.Add((600, 700));
            var hm = hd.Build(2);
            var under = new Hanger(120, 240, 40, 8);
            var wide = new Hanger(260, 240, 120, 8);
            var overPit = new Hanger(610, 240, 64, 8);
            Check(HangerMath.Anchors(hm, under).SequenceEqual(new[] { 136 }) && under.ChainXs.Length == 1, "A short hanger hangs on one chain from the block above it",
                string.Join(",", HangerMath.Anchors(hm, under)));
            Check(wide.ChainXs.SequenceEqual(new[] { 268, 372 }) && HangerMath.Anchors(hm, wide).SequenceEqual(new[] { 136, MapGenerator.Wall }),
                "A long hanger hangs on two chains, each from whatever is above it", string.Join(",", HangerMath.Anchors(hm, wide)));
            var onEdge = new Hanger(276, 240, 40, 8); // its chain 4 px in from the block's end: the bracket would stick out
            Check(HangerMath.AnchorsHidden(hm, under) && HangerMath.AnchorsHidden(hm, wide) && !HangerMath.AnchorsHidden(hm, onEdge),
                "A chain bolted near the end of a block shows, one under the middle or the ceiling doesn't");
            var edgeDesign = hd.Copy();
            edgeDesign.Hangers.Add(onEdge);
            Check(edgeDesign.Problems().Exists(p => p.Contains("off the end of a block")) && !hd.Problems().Exists(p => p.Contains("off the end")),
                "The map editor warns about a chain hanging off the end of a block", string.Join(" / ", edgeDesign.Problems()));
            Check(HangerMath.LandY(hm, under) == MapGenerator.FloorY - 8 && HangerMath.LandsFlat(hm, under), "A cut hanger lands flat on the floor");
            Check(HangerMath.LandY(hm, overPit) == null && HangerMath.LandsFlat(hm, overPit), "A hanger over a pit falls out of the arena");
            float tLand = MathF.Sqrt(2 * (MapGenerator.FloorY - 8 - 240) / Hanger.Gravity);
            Check(Hanger.Drop(0) == 0 && Hanger.Drop(-1) == 0 && Math.Abs(Hanger.Drop(tLand) - (MapGenerator.FloorY - 8 - 240)) < 0.01f && tLand < 0.8f,
                "A hanger drops faster and faster and reaches the floor in under a second", $"{tLand:0.00}s");
            // One chain of two cut: it swings on the other, the swing pulling that chain along, and settles hanging under it.
            var swing = new HangerSwing(wide, 268, MapGenerator.Wall);
            var wideCenter = new Vector2(320, 244);
            swing.At(0, out var c0, out float r0, out _, out _);
            float Energy(float t) { swing.At(t, out var c, out _, out var v, out float w); return 0.5f * (v.LengthSquared() + (120 * 120 + 64) / 12f * w * w) - Hanger.Gravity * c.Y; }
            float e0 = Energy(0), worst = float.MinValue, chainOut = 0;
            for (float t = 0; t < HangerSwing.Seconds; t += 0.05f)
            {
                worst = Math.Max(worst, Energy(t) - e0);
                chainOut = Math.Max(chainOut, Math.Abs(swing.ChainEndAt(t).X - 268));
            }
            swing.At(0.4f, out var cEarly, out float rEarly, out _, out _);
            swing.At(HangerSwing.Seconds, out var cEnd, out float rEnd, out var vEnd, out float wEnd);
            Check(Vector2.Distance(c0, wideCenter) < 0.01f && r0 == 0, "Swing: it starts where it hung, level", $"{c0} {r0}");
            Check(cEarly.X < 319 && cEarly.Y > 250 && rEarly > 0.2f, "Swing: the cut end drops and the platform swings in under its last chain",
                $"{cEarly} {rEarly:0.00}");
            Check(chainOut > 5, "Swing: the platform's swing is passed into the chain, which swings too", $"{chainOut:0.0}px");
            Check(worst < 50, "Swing: it never gains energy", $"+{worst:0.0}");
            Check(Math.Abs(cEnd.X - 268) < 3 && vEnd.Length() < 20 && Math.Abs(wEnd) < 0.05f, "Swing: it settles hanging still, straight under its chain",
                $"{cEnd} v{vEnd.Length():0.0} w{wEnd:0.00} r{rEnd:0.00}");
            var again = new HangerSwing(wide, 268, MapGenerator.Wall);
            again.At(2.345f, out var sw1, out float ar1, out _, out _);
            swing.At(2.345f, out var sw2, out float ar2, out _, out _);
            Check(sw1 == sw2 && ar1 == ar2, "Swing: the same cut swings the same on every peer");

            // The last chain goes: from rest it lands where it always did; mid-swing it carries the swing's speed and lands level.
            var still = new HangerFall(hm, wide, wideCenter, Vector2.Zero, 0, 0);
            Check(still.LandTop == HangerMath.LandY(hm, wide) && Math.Abs(still.LandX - 320) < 0.01f, "Fall: from rest it lands straight under where it hung",
                $"{still.LandTop} {still.LandX}");
            Check(new HangerFall(hm, overPit, new Vector2(642, 244), Vector2.Zero, 0, 0).LandTop == null, "Fall: over a pit it falls out of the arena");
            swing.At(0.6f, out var cFrom, out float rFrom, out var vFrom, out float wFrom);
            var flung = new HangerFall(hm, wide, cFrom, vFrom, rFrom, wFrom);
            bool landed = flung.At(flung.LandT + 0.01f, out var cLand, out float rLand);
            flung.At(0, out var cStart, out float rStart);
            Check(landed && flung.LandTop == MapGenerator.FloorY - 8 && rLand == 0 && Math.Abs(cLand.X - (cFrom.X + vFrom.X * flung.LandT)) < 1
                && Vector2.Distance(cStart, cFrom) < 0.01f && rStart == rFrom,
                "Fall: cut mid-swing, it carries on from there with the swing's speed and lands level", $"{cFrom} -> {cLand} t{flung.LandT:0.00}");

            hd.Hangers.Add(new Hanger(150, 116, 40, 8));
            Check(hd.Problems().Any(p => p.Contains("hanging platform")), "Design: a hanger inside a block is a problem", string.Join(" | ", hd.Problems()));
        }

        // Reachability primitive.
        var floor = new Surface(8, 632, 344);
        Check(MapGenerator.CanTraverse(floor, new Surface(100, 160, 304)), "Can jump up 40px onto a nearby platform");
        Check(!MapGenerator.CanTraverse(floor, new Surface(100, 160, 260)), "Cannot jump up 84px");
        Check(MapGenerator.CanTraverse(new Surface(100, 160, 260), floor), "Can always drop down");

        var towerMap = new MapLayout();
        towerMap.Solids.Add(new RectI(300, 314, 32, 120));
        var leftFloor = new Surface(8, 300, 434);
        var rightFloor = new Surface(332, 792, 434);
        Check(MapGenerator.CanTraverse(leftFloor, rightFloor) && !MapGenerator.CanTraverse(towerMap, leftFloor, rightFloor),
            "A tower between two floor segments blocks walking across");
        var bridge = new Surface(240, 290, 394);
        var pathMap = new MapLayout();
        pathMap.Surfaces.AddRange(new[] { leftFloor, bridge, new Surface(300, 332, 354), rightFloor });
        pathMap.Solids.Add(new RectI(300, 354, 32, 80));
        Check(MapGenerator.NextHop(pathMap, 0, 3) == 1, "Path finding climbs the ledge to get over a tower",
            $"next hop {MapGenerator.NextHop(pathMap, 0, 3)}");

        // Size-aware graph: a shelf 32px over the floor is fine for a normal body but splits it for a Giant.
        var lowMap = new MapLayout();
        lowMap.Surfaces.Add(new Surface(8, 792, 434));
        lowMap.Solids.Add(new RectI(8, 434, 784, 16));
        lowMap.Solids.Add(new RectI(400, 394, 32, 8));
        Check(MapGenerator.NavLayout(lowMap, 14, 18).Surfaces.Count == 1, "A normal body walks under a 32px shelf");
        var giantNav = MapGenerator.NavLayout(lowMap, 25, 33);
        Check(giantNav.Surfaces.Count == 2 && MapGenerator.NextHop(giantNav, 0, 1) == -1,
            "A Giant can't walk under a low shelf", $"{giantNav.Surfaces.Count} surfaces");
        // The generator splits the floor under a bar 24px up (less than its 26px headroom); a normal body still fits.
        var barMap = new MapLayout();
        barMap.Surfaces.AddRange(new[] { new Surface(8, 300, 434), new Surface(380, 792, 434) });
        barMap.Solids.Add(new RectI(8, 434, 784, 16));
        barMap.Solids.Add(new RectI(300, 394, 80, 16));
        Check(MapGenerator.NextHop(barMap, 0, 1) == -1 && MapGenerator.NextHop(MapGenerator.NavLayout(barMap, 14, 18), 0, 1) == 1,
            "Bots know a normal body fits under a 24px bar");
        // A floor pocket between two 160px towers is a dead end, unless a jump pad in it gets you onto a tower top.
        var pocket = new MapLayout();
        pocket.Surfaces.AddRange(new[] { new Surface(648, 720, 434), new Surface(608, 648, 274) });
        pocket.Solids.AddRange(new[] { new RectI(8, 434, 784, 16), new RectI(608, 274, 40, 160), new RectI(720, 274, 24, 160) });
        pocket.Pads.Add(new JumpPad(664, 688, 434));
        Check(MapGenerator.NextHop(pocket, 0, 1) == -1 && MapGenerator.NextHop(MapGenerator.NavLayout(pocket, 14, 18), 0, 1) == 1,
            "Bots plan with jump pads (map validation doesn't)");
    }
}
