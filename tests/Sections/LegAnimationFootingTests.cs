using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class LegAnimationFootingTests
{
    public static void Run()
    {
        var floor = new List<RectI> { new(0, 100, 200, 20) };
        var none = new List<Slope>();
        // A 14x18 body resting on the floor: grounded, flat.
        var f = FootingProbe.Probe(50, 100, 7, 4, 7, floor, none);
        Check(f.Grounded && Math.Abs(f.Drop) < 0.01f && Math.Abs(f.Slope) < 0.01f, "Footing: standing on a flat floor", $"{f}");
        // Two pixels up from it (a frame after jumping) still counts as touching, ten pixels up does not.
        Check(FootingProbe.Probe(50, 98.5f, 7, 4, 7, floor, none).Grounded, "Footing: resting contact within Touch counts");
        Check(!FootingProbe.Probe(50, 90, 7, 4, 7, floor, none).Grounded, "Footing: airborne above the floor");
        // At a ledge with one foot over the drop, the missing foot borrows the other's height (stays flat).
        var edge = FootingProbe.Probe(203, 100, 7, 4, 7, floor, none);
        Check(edge.Grounded && Math.Abs(edge.Slope) < 0.01f && Math.Abs(edge.Drop) < 0.01f, "Footing: ledge edge stays flat", $"{edge}");

        // A ramp rising to the right (y 134 at x 100 up to 102 at x 148): the body rests on its uphill corner, the
        // ground lies below the bottom edge at the center, and the slope is negative (ascending to the right).
        var ramp = new List<Slope> { new(100, 134, 148, 102, 134) };
        float cx = 124, slopeK = -32f / 48f;
        float bottom = ramp[0].YAt(cx + 7);
        var r = FootingProbe.Probe(cx, bottom, 7, 4, 12, new List<RectI>(), ramp);
        Check(r.Grounded, "Footing: grounded on a ramp by the uphill corner");
        Check(Math.Abs(r.Slope - slopeK) < 0.02f, "Footing: ramp slope measured between the feet", $"{r.Slope} vs {slopeK}");
        Check(Math.Abs(r.Drop - 7 * -slopeK) < 0.05f, "Footing: ground drop under the center of a ramp", $"{r.Drop}");

        // Walls: right beside the box on either side, but not a floor under it or a wall a few pixels away.
        var walls = new List<RectI> { new(57, 0, 10, 200), new(20, 0, 10, 200) };
        Check(FootingProbe.WallSide(50, 100, 7, 9, walls.Take(1).ToList()) == 1, "Footing: wall on the right");
        Check(FootingProbe.WallSide(37, 100, 7, 9, walls.Skip(1).ToList()) == -1, "Footing: wall on the left");
        Check(FootingProbe.WallSide(50, 91, 7, 9, floor) == 0, "Footing: the floor is not a wall");
        Check(FootingProbe.WallSide(46, 100, 7, 9, walls.Take(1).ToList()) == 0, "Footing: no wall 4 px away");
    }
}
