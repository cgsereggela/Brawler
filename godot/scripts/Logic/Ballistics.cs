using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>
/// Arcs under gravity, for the bots (BotBrain): the way to fire a dropping bullet so it lands on a spot, whether its
/// arc is clear of the arena's solids, and whether a running jump comes down on something. Y grows downward.
/// </summary>
public static class Ballistics
{
    /// <summary>
    /// The way to fire (a unit vector) for a bullet of speed <paramref name="v"/> and gravity <paramref name="g"/> to
    /// arc from <paramref name="from"/> to <paramref name="to"/> without touching a solid on the way: the flatter of the
    /// two arcs that reach it if that one is clear, else the high lob. False if neither is clear or it's out of range.
    /// </summary>
    public static bool LobAim(Vector2 from, Vector2 to, float v, float g, IReadOnlyList<RectI> solids, IReadOnlyList<Slope> slopes, out Vector2 dir)
    {
        dir = default;
        float dx = to.X - from.X, ax = MathF.Abs(dx), up = from.Y - to.Y;
        if (g < 1 || ax < 4) return false;
        float v2 = v * v, disc = v2 * v2 - g * (g * ax * ax + 2 * up * v2);
        if (disc < 0) return false;
        float root = MathF.Sqrt(disc);
        for (int k = 0; k < 2; k++)
        {
            float angle = MathF.Atan((v2 + (k == 0 ? -root : root)) / (g * ax));
            var vel = new Vector2(MathF.Sign(dx) * v * MathF.Cos(angle), -v * MathF.Sin(angle));
            if (!ArcClear(from, vel, g, ax / (v * MathF.Cos(angle)), solids, slopes)) continue;
            dir = vel / v;
            return true;
        }
        return false;
    }

    /// <summary>Where a bullet fired at <paramref name="vel"/> under gravity <paramref name="g"/> is <paramref name="t"/> seconds later.</summary>
    public static Vector2 At(Vector2 from, Vector2 vel, float g, float t) => from + vel * t + new Vector2(0, 0.5f * g * t * t);

    /// <summary>True if a bullet fired at <paramref name="vel"/> under gravity <paramref name="g"/> meets none of <paramref name="solids"/> (nor a ramp) for most of <paramref name="time"/> seconds.</summary>
    public static bool ArcClear(Vector2 from, Vector2 vel, float g, float time, IReadOnlyList<RectI> solids, IReadOnlyList<Slope> slopes)
    {
        const int Steps = 12;
        var p = from;
        for (int i = 1; i <= Steps; i++)
        {
            // Stop just short of the end: the target stands on something, which the arc's last bit may graze.
            float t = time * 0.95f * i / Steps;
            var q = At(from, vel, g, t);
            if (Sweep.Cast(p, q - p, 1, solids, slopes) != null) return false;
            p = q;
        }
        return true;
    }

    /// <summary>
    /// True if a jump from <paramref name="feet"/> at (<paramref name="vx"/>, <paramref name="vy"/>) under gravity
    /// <paramref name="g"/> comes down on something (the top of a solid or ramp under its feet, ± <paramref name="hw"/>),
    /// stepped every 1/30 s for up to 3 s. False over a bottomless drop or a gap too wide to clear.
    /// </summary>
    public static bool JumpLands(Vector2 feet, float vx, float vy, float g, float hw, IReadOnlyList<RectI> solids, IReadOnlyList<Slope> slopes)
    {
        var p = feet;
        const float dt = 1f / 30;
        for (int i = 0; i < 90; i++)
        {
            var q = p + new Vector2(vx, vy) * dt;
            vy += g * dt;
            if (q.Y > MapLayout.KillY) return false;
            if (q.Y > p.Y && TopBetween(q.X, hw, p.Y, q.Y, solids, slopes)) return true;
            p = q;
        }
        return false;
    }

    /// <summary>True if the top of a solid or a ramp lies under x (± <paramref name="hw"/>) between heights y1 and y2.</summary>
    public static bool TopBetween(float x, float hw, float y1, float y2, IReadOnlyList<RectI> solids, IReadOnlyList<Slope> slopes)
    {
        foreach (var r in solids)
            if (r.X < x + hw && r.Right > x - hw && r.Y >= y1 - 1 && r.Y <= y2 + 1) return true;
        if (slopes != null)
            foreach (var s in slopes)
                if (x >= s.X1 && x < s.X2 && s.YAt(x) >= y1 - 1 && s.YAt(x) <= y2 + 1) return true;
        return false;
    }

    /// <summary>
    /// Seconds for something moving at <paramref name="vy"/> (down is positive) to fall <paramref name="drop"/> px
    /// under gravity <paramref name="g"/>.
    /// </summary>
    public static float FallTime(float vy, float drop, float g) => (-vy + MathF.Sqrt(vy * vy + 2 * g * MathF.Max(0, drop))) / g;
}
