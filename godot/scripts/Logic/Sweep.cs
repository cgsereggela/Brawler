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

    /// <summary>
    /// <see cref="Cast(Vector2, Vector2, float, IReadOnlyList{RectI})"/> against the rects, then the sloped ground. A slope
    /// hit reports Index -2 - (its index in <paramref name="slopes"/>), so it never reads as a rect.
    /// </summary>
    public static SweepHit? Cast(Vector2 from, Vector2 delta, float radius, IReadOnlyList<RectI> rects, IReadOnlyList<Slope> slopes)
    {
        var best = Cast(from, delta, radius, rects);
        if (slopes == null) return best;
        for (int i = 0; i < slopes.Count; i++)
        {
            var hit = CastSlope(from, delta, radius, slopes[i]);
            if (hit is { } h && (best == null || h.T < best.Value.T)) best = h with { Index = -2 - i };
        }
        return best;
    }

    /// <summary>
    /// Swept circle vs a slope's outline: the convex polygon grown by the radius (edges pushed out along their normals,
    /// corners rounded), so a bullet glancing off a ramp bounces off the ramp's real normal. A slope the circle already
    /// touches at t=0 is ignored, like an overlapped rect.
    /// </summary>
    public static SweepHit? CastSlope(Vector2 p, Vector2 d, float radius, Slope s)
    {
        float minX = MathF.Min(p.X, p.X + d.X) - radius, maxX = MathF.Max(p.X, p.X + d.X) + radius;
        float minY = MathF.Min(p.Y, p.Y + d.Y) - radius, maxY = MathF.Max(p.Y, p.Y + d.Y) + radius;
        if (maxX < s.X1 || minX > s.X2 || maxY < s.Top || minY > s.Bottom) return null;
        var pts = s.Outline();
        if (DistanceToConvex(p, pts) < radius) return null;
        float bestT = float.MaxValue;
        var bestN = Vector2.Zero;
        for (int i = 0; i < pts.Length; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % pts.Length], e = b - a;
            float len = e.Length();
            var n = new Vector2(e.Y, -e.X) / len; // outward, for a clockwise (on screen) outline
            float dn = Vector2.Dot(d, n);
            if (dn < -1e-6f)
            {
                var a2 = a + n * radius;
                float t = Vector2.Dot(a2 - p, n) / dn;
                float u = Vector2.Dot(p + d * t - a2, e) / len;
                if (t >= 0 && t < bestT && u >= 0 && u <= len) { bestT = t; bestN = n; }
            }
            if (RayCircle(p, d, a, radius) is float tc && tc < bestT)
            {
                bestT = tc;
                bestN = Vector2.Normalize(p + d * tc - a);
            }
        }
        return bestT <= 1 ? new SweepHit(bestT, bestN) : null;
    }

    /// <summary>First t in [0, 1] where p + t·d reaches distance r from c, moving inward; null if never.</summary>
    private static float? RayCircle(Vector2 p, Vector2 d, Vector2 c, float r)
    {
        var f = p - c;
        float A = Vector2.Dot(d, d), B = 2 * Vector2.Dot(f, d), C = Vector2.Dot(f, f) - r * r;
        if (A < 1e-9f || B >= 0) return null;
        float disc = B * B - 4 * A * C;
        if (disc < 0) return null;
        float t = (-B - MathF.Sqrt(disc)) / (2 * A);
        return t >= 0 && t <= 1 ? t : null;
    }

    /// <summary>Distance from a point to a convex polygon (clockwise on screen); 0 inside.</summary>
    public static float DistanceToConvex(Vector2 p, Vector2[] pts)
    {
        bool inside = true;
        float best = float.MaxValue;
        for (int i = 0; i < pts.Length; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % pts.Length], e = b - a;
            if (e.Y * (p.X - a.X) - e.X * (p.Y - a.Y) > 0) inside = false;
            float k = Math.Clamp(Vector2.Dot(p - a, e) / Vector2.Dot(e, e), 0, 1);
            best = MathF.Min(best, Vector2.Distance(p, a + e * k));
        }
        return inside ? 0 : best;
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
