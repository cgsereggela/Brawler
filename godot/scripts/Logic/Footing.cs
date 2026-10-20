using System;
using System.Collections.Generic;

namespace Brawler.Logic;

/// <summary>What the ground under a body looks like, for the leg animation.</summary>
public readonly record struct Footing(bool Grounded, float Drop, float Slope);

/// <summary>
/// Geometry probes for the cosmetic leg animation: is a body standing on something, how far below its bottom edge the
/// ground lies under its feet (ramps), and is there a wall beside it. Purely visual and read-only, so it works the
/// same for the host, a predicting client and interpolated remote players, and never feeds back into the simulation.
/// </summary>
public static class FootingProbe
{
    /// <summary>Touching distance: a body this close above a surface counts as standing on it.</summary>
    public const float Touch = 2f;

    /// <summary>
    /// Distance from <paramref name="fromY"/> down to the first rect top or ramp surface at <paramref name="x"/>, or null
    /// if none lies within <paramref name="reach"/>. A surface a hair above <paramref name="fromY"/> (resting contact) counts as 0-ish.
    /// </summary>
    public static float? SurfaceBelow(float x, float fromY, float reach, IReadOnlyList<RectI> solids, IReadOnlyList<Slope> slopes)
    {
        float best = float.MaxValue;
        if (solids != null)
            foreach (var r in solids)
            {
                if (x < r.X || x > r.Right) continue;
                float d = r.Y - fromY;
                if (d >= -1f && d < best) best = d;
            }
        if (slopes != null)
            foreach (var s in slopes)
            {
                if (x < s.X1 || x > s.X2) continue;
                float d = s.YAt(x) - fromY;
                if (d >= -1.5f && d < best) best = d;
            }
        return best <= reach ? Math.Max(0, best) : null;
    }

    /// <summary>
    /// Ground under a body whose bottom edge is centered on (<paramref name="cx"/>, <paramref name="bottom"/>).
    /// Grounded when any point of the bottom edge rests on something. Drop is the gap below the bottom edge at the
    /// center, Slope the ground's dy/dx measured between the two feet (<paramref name="footX"/> either side). A foot
    /// over empty air borrows the other foot's height, so standing at a ledge stays flat.
    /// </summary>
    public static Footing Probe(float cx, float bottom, float halfW, float footX, float reach,
        IReadOnlyList<RectI> solids, IReadOnlyList<Slope> slopes)
    {
        bool grounded = false;
        foreach (float x in new[] { cx - halfW + 0.5f, cx, cx + halfW - 0.5f })
            if (SurfaceBelow(x, bottom, Touch, solids, slopes) is not null) { grounded = true; break; }
        if (!grounded) return new Footing(false, 0, 0);
        float? l = SurfaceBelow(cx - footX, bottom, reach, solids, slopes);
        float? r = SurfaceBelow(cx + footX, bottom, reach, solids, slopes);
        float dl = l ?? r ?? 0, dr = r ?? l ?? 0;
        return new Footing(true, (dl + dr) * 0.5f, footX > 0 ? (dr - dl) / (2 * footX) : 0);
    }

    /// <summary>-1 / +1 if a solid is right against the left / right side of the box (over its middle half), else 0.</summary>
    public static int WallSide(float cx, float cy, float halfW, float halfH, IReadOnlyList<RectI> solids)
    {
        if (solids == null) return 0;
        float y1 = cy - halfH * 0.5f, y2 = cy + halfH * 0.5f;
        foreach (int side in new[] { 1, -1 })
        {
            float x = cx + side * (halfW + 1.2f);
            foreach (var r in solids)
                if (x >= r.X && x <= r.Right && r.Y < y2 && r.Bottom > y1) return side;
        }
        return 0;
    }
}
