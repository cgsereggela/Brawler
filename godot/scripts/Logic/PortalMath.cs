using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>
/// One ring of a <see cref="PortalPair"/>: an opening <see cref="Half"/> * 2 long centred on (X, Y), facing
/// <see cref="Dir"/> (0 right, 1 down, 2 left, 3 up). Its front is the side it faces: whatever crosses it from the
/// front comes out of the other ring. Crossing from behind does nothing, so a ring is open air seen from the back.
/// </summary>
public readonly record struct PortalRing(int X, int Y, int Dir)
{
    /// <summary>Half the opening's length: a side ring standing on the ground (Y = ground - Half) is just taller than a body.</summary>
    public const int Half = 14;
    public Vector2 Center => new(X, Y);
    public Vector2 Normal => Dir switch { 0 => new(1, 0), 1 => new(0, 1), 2 => new(-1, 0), _ => new(0, -1) };
    /// <summary>Along the opening: down for a ring in a wall, right for one in a floor or ceiling.</summary>
    public Vector2 Tangent => Side ? new(0, 1) : new(1, 0);
    /// <summary>True for a ring facing sideways (a doorway); false for one facing up or down (a hole in a floor or ceiling).</summary>
    public bool Side => Dir is 0 or 2;
    /// <summary>The same ring turned a quarter clockwise.</summary>
    public PortalRing Turned() => this with { Dir = (Dir + 1) & 3 };
    /// <summary>The ring's footprint, for the editor and for keeping other extras off it.</summary>
    public RectI Box => Side ? new(X - 4, Y - Half, 8, Half * 2) : new(X - Half, Y - 4, Half * 2, 8);
}

/// <summary>Two linked rings: going into either one brings you out of the other.</summary>
public readonly record struct PortalPair(PortalRing A, PortalRing B);

/// <summary>
/// The maths of going through a portal, shared by players, bullets and bots. Pure, so every peer agrees on where a
/// bullet comes out, and the tests cover it.
/// </summary>
public static class PortalMath
{
    /// <summary>Seconds after coming out of a ring before a body can go through one again.</summary>
    public const float Cooldown = 0.25f;
    /// <summary>The least speed a body comes out at (px/s), so walking in slowly still carries you clear of the exit.</summary>
    public const float MinBodySpeed = 140;

    /// <summary>
    /// True if the move from <paramref name="p0"/> to <paramref name="p1"/> goes into <paramref name="r"/> from its
    /// front; <paramref name="t"/> is where along the opening it crossed (-Half..Half).
    /// </summary>
    public static bool Enters(PortalRing r, Vector2 p0, Vector2 p1, out float t)
    {
        t = 0;
        var n = r.Normal;
        float d0 = Vector2.Dot(p0 - r.Center, n), d1 = Vector2.Dot(p1 - r.Center, n);
        if (d0 <= 0 || d1 > 0) return false;
        var hit = Vector2.Lerp(p0, p1, d0 / (d0 - d1));
        t = Vector2.Dot(hit - r.Center, r.Tangent);
        return MathF.Abs(t) <= PortalRing.Half;
    }

    /// <summary>The first ring of <paramref name="pairs"/> the move goes into; <paramref name="fromA"/> says which end.</summary>
    public static bool Find(IReadOnlyList<PortalPair> pairs, Vector2 p0, Vector2 p1, out PortalPair pair, out bool fromA)
    {
        pair = default;
        fromA = false;
        if (pairs == null) return false;
        foreach (var p in pairs)
        {
            if (Enters(p.A, p0, p1, out _)) { pair = p; fromA = true; return true; }
            if (Enters(p.B, p0, p1, out _)) { pair = p; fromA = false; return true; }
        }
        return false;
    }

    /// <summary>
    /// A direction relative to <paramref name="entry"/> turned to match <paramref name="exit"/>: straight in becomes
    /// straight out. Between two rings of the same kind (two doorways, or two holes) the part along the opening is
    /// kept as it is in the world, so a body falling as it walks in still falls as it walks out; between a doorway
    /// and a hole the whole thing turns by the quarter that lines the two up.
    /// </summary>
    public static Vector2 Turn(PortalRing entry, PortalRing exit, Vector2 v)
    {
        float inward = -Vector2.Dot(v, entry.Normal);
        float along = Vector2.Dot(v, entry.Tangent);
        var outAlong = entry.Side == exit.Side ? exit.Tangent : Rotate(entry.Tangent, entry, exit);
        return exit.Normal * inward + outAlong * along;
    }

    /// <summary>The quarter (or half) turn taking -entry.Normal to exit.Normal, applied to <paramref name="v"/>.</summary>
    private static Vector2 Rotate(Vector2 v, PortalRing entry, PortalRing exit)
    {
        var a = -entry.Normal;
        var b = exit.Normal;
        float cos = Vector2.Dot(a, b), sin = a.X * b.Y - a.Y * b.X;
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    /// <summary>
    /// Where something at <paramref name="at"/> moving at <paramref name="vel"/> comes out after going into
    /// <paramref name="entry"/>: at the same spot along <paramref name="exit"/>'s opening, <paramref name="clear"/>
    /// px in front of it, moving out at the same speed (at least <paramref name="minSpeed"/> straight out).
    /// </summary>
    public static (Vector2 pos, Vector2 vel) Through(PortalRing entry, PortalRing exit, Vector2 at, Vector2 vel, float clear, float minSpeed = 0)
    {
        float t = Math.Clamp(Vector2.Dot(at - entry.Center, entry.Tangent), -PortalRing.Half, PortalRing.Half);
        var offset = Turn(entry, exit, entry.Tangent * t);
        var v = Turn(entry, exit, vel);
        float outward = Vector2.Dot(v, exit.Normal);
        if (outward < minSpeed) v += exit.Normal * (minSpeed - outward);
        return (exit.Center + offset + exit.Normal * clear, v);
    }

    /// <summary>The ring at the other end of the pair.</summary>
    public static PortalRing Other(PortalPair p, bool fromA) => fromA ? p.B : p.A;

    /// <summary>The ring at the end you go in.</summary>
    public static PortalRing Entry(PortalPair p, bool fromA) => fromA ? p.A : p.B;

    /// <summary>A doorway standing on surface <paramref name="s"/>: its foot level with the ground, within the span.</summary>
    public static bool StandsOn(PortalRing r, Surface s) => r.Side && r.Y + PortalRing.Half == s.Y && r.X >= s.X1 - 2 && r.X <= s.X2 + 2;
}
