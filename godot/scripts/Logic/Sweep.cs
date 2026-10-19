using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>A sweep hit: how far along the move (0..1), the surface normal, and which rect in the list.</summary>
public readonly record struct SweepHit(float T, Vector2 Normal, int Index = -1);

/// <summary>Swept circle vs axis-aligned rectangles (via Minkowski-expanded slab test).</summary>
public static class Sweep
{
    /// <summary>
    /// Earliest hit of a circle of <paramref name="radius"/> moving from <paramref name="from"/> by <paramref name="delta"/>.
    /// Returns null when nothing is hit within the step. Rects the circle already overlaps at t=0 are ignored.
    /// </summary>
    public static SweepHit? Cast(Vector2 from, Vector2 delta, float radius, IReadOnlyList<RectI> rects)
    {
        SweepHit? best = null;
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            float minX = r.X - radius, maxX = r.Right + radius;
            float minY = r.Y - radius, maxY = r.Bottom + radius;
            if (from.X > minX && from.X < maxX && from.Y > minY && from.Y < maxY) continue;
            var hit = SegmentVsBox(from, delta, minX, minY, maxX, maxY);
            if (hit is { } h && (best == null || h.T < best.Value.T)) best = h with { Index = i };
        }
        return best;
    }

    public static SweepHit? SegmentVsBox(Vector2 p, Vector2 d, float minX, float minY, float maxX, float maxY)
    {
        float tEnter = 0f, tExit = 1f;
        Vector2 normal = Vector2.Zero;

        if (!Axis(p.X, d.X, minX, maxX, ref tEnter, ref tExit, out float nx, out bool enteredX)) return null;
        if (enteredX) normal = new Vector2(nx, 0);
        if (!Axis(p.Y, d.Y, minY, maxY, ref tEnter, ref tExit, out float ny, out bool enteredY)) return null;
        if (enteredY) normal = new Vector2(0, ny);

        if (tEnter > tExit || normal == Vector2.Zero) return null;
        return new SweepHit(tEnter, normal);
    }

    private static bool Axis(float p, float d, float min, float max, ref float tEnter, ref float tExit, out float n, out bool entered)
    {
        n = 0;
        entered = false;
        if (MathF.Abs(d) < 1e-6f) return p >= min && p <= max;
        float t1 = (min - p) / d, t2 = (max - p) / d;
        float nEnter = d > 0 ? -1 : 1;
        if (t1 > t2) (t1, t2) = (t2, t1);
        if (t1 > tEnter)
        {
            tEnter = t1;
            n = nEnter;
            entered = true;
        }
        tExit = MathF.Min(tExit, t2);
        return tEnter <= tExit;
    }

    public static Vector2 Reflect(Vector2 v, Vector2 n) => v - 2f * Vector2.Dot(v, n) * n;

    /// <summary>Does the segment from <paramref name="a"/> to <paramref name="b"/> touch the rect grown by radius?</summary>
    public static bool SegmentTouchesRect(Vector2 a, Vector2 b, float radius, float rx, float ry, float rw, float rh)
    {
        float minX = rx - radius, maxX = rx + rw + radius, minY = ry - radius, maxY = ry + rh + radius;
        if (a.X >= minX && a.X <= maxX && a.Y >= minY && a.Y <= maxY) return true;
        return SegmentVsBox(a, b - a, minX, minY, maxX, maxY) != null;
    }
}
