using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>
/// A sloped stretch of ground: solid under the line from (X1, Y1) to (X2, Y2) down to <paramref name="Bottom"/>
/// (X1 &lt; X2). Both ends meet flat ground at the same height, and the jump graph treats the slope as a walk between
/// those two surfaces (<see cref="MapGenerator.SlopeLinks"/>). Slopes are never surfaces themselves, so spawns, pads,
/// pools, saws and crates (all placed on surfaces) stay on flat ground.
/// </summary>
public readonly record struct Slope(int X1, int Y1, int X2, int Y2, int Bottom)
{
    public int Top => Math.Min(Y1, Y2);
    public int Width => X2 - X1;
    public RectI Bounds => new(X1, Top, X2 - X1, Bottom - Top);
    /// <summary>Steepness in degrees from horizontal.</summary>
    public float Angle => MathF.Atan2(Math.Abs(Y2 - Y1), X2 - X1) * 180 / MathF.PI;
    /// <summary>Height of the sloped top at <paramref name="x"/> (clamped to the ends).</summary>
    public float YAt(float x) => Y1 + (Y2 - Y1) * Math.Clamp((x - X1) / (X2 - X1), 0f, 1f);
    /// <summary>Unit normal of the sloped top, pointing up out of the ground.</summary>
    public Vector2 Normal => Vector2.Normalize(new Vector2(Y2 - Y1, X1 - X2));

    /// <summary>Exact overlap with the box [x1, x2] x [y1, y2] (touching doesn't count, as with <see cref="RectI.Intersects"/>).</summary>
    public bool Overlaps(float x1, float y1, float x2, float y2)
    {
        float a = MathF.Max(x1, X1), b = MathF.Min(x2, X2);
        if (a >= b || y1 >= Bottom) return false;
        return y2 > MathF.Min(YAt(a), YAt(b));
    }

    public bool Intersects(RectI r) => Overlaps(r.X, r.Y, r.Right, r.Bottom);

    /// <summary>
    /// The outline as a convex polygon, clockwise on screen: along the top, then down and back along the bottom.
    /// The low end usually sits on the bottom, which leaves a triangle (no repeated corner).
    /// </summary>
    public Vector2[] Outline()
    {
        var pts = new List<Vector2> { new(X1, Y1), new(X2, Y2) };
        if (Y2 < Bottom) pts.Add(new Vector2(X2, Bottom));
        if (Y1 < Bottom) pts.Add(new Vector2(X1, Bottom));
        return pts.ToArray();
    }
}

/// <summary>A corner of a floor segment's top outline (see <see cref="MapLayout.Ground"/>).</summary>
public readonly record struct GroundPoint(int X, int Y);

/// <summary>
/// Rolling floor terrain: hills, stepped hills, mesas (a ramp up one side, a short cliff down the other), raised shelves
/// against the side walls, and the valleys left between them. Each feature stands on a floor segment: flat raised
/// stretches become ordinary rects in <see cref="MapLayout.Solids"/> (and <see cref="MapLayout.Terrain"/>), whose tops
/// are surfaces like any platform's; ramps between levels become <see cref="Slope"/>s. Terrain only goes where no
/// platform hangs low over it, keeps clear of pit edges, and every hill foot keeps a stretch of flat floor, so the
/// jump graph, spawns and the arena extras work on it unchanged. Mirror arenas get mirrored terrain.
/// </summary>
public static class TerrainGen
{
    /// <summary>Tallest hill or shelf (MinHeight..MaxHeight, whole tiles).</summary>
    public const int MinHeight = 24, MaxHeight = 64;
    /// <summary>
    /// Steepest ramp: rise over run of 1/1.2 (39.8 degrees), kept under the avatar's 45 degree floor limit so every
    /// ramp stays walkable. Ramps are rounded up to whole tiles, so most are a little gentler.
    /// </summary>
    public const float MaxAngle = 40f;
    /// <summary>Clear air kept between terrain and any platform above it (the same as between platforms).</summary>
    public const int Clearance = MapGenerator.MinClearance;
    /// <summary>Flat floor kept at each hill's feet, between hills, and at pit edges.</summary>
    public const int FootRoom = 40, PitMargin = 32;

    private const int Tile = MapGenerator.Tile, FloorY = MapGenerator.FloorY, Wall = MapGenerator.Wall;
    private const int Width = MapLayout.Width;

    public static float ChanceFor(MapStyle style) => style switch
    {
        MapStyle.Tiers => 0.6f,
        MapStyle.Mirror => 0.55f,
        MapStyle.Islands => 0.5f,
        _ => 0.45f,
    };

    /// <summary>Maybe adds terrain to the floor segments of a map whose platforms are already placed.</summary>
    public static void Add(MapLayout map, Rng rng, List<(int x1, int x2)> floor, List<RectI> placed)
    {
        if (!rng.Chance(ChanceFor(map.Style))) return;
        // Each feature is a list of (x, height above the floor) corners, from one foot to the other.
        var features = new List<List<(int x, int h)>>();
        bool mirror = map.Style == MapStyle.Mirror;
        int want = rng.Range(1, 4);

        if (mirror && rng.Chance(0.35f))
        {
            // A centerpiece hill straddling the middle, symmetric by construction.
            int h = rng.Range(MinHeight / Tile, MaxHeight / Tile) * Tile, run = Run(rng, h), top = rng.Range(2, 6) * Tile;
            int x = Width / 2 - top - run;
            var hill = new List<(int x, int h)> { (x, 0), (x + run, h), (Width / 2 + top, h), (Width / 2 + top + run, 0) };
            if (Fits(floor, placed, features, hill)) features.Add(hill);
        }

        for (int tries = 0; tries < want * 12 && features.Count < want * (mirror ? 2 : 1); tries++)
        {
            var f = rng.Chance(0.25f) ? Shelf(rng, rng.Chance(0.5f) || mirror) : Bump(rng, rng.Range(Wall + Tile, Width - Wall - 200) / Tile * Tile);
            if (f == null || !Fits(floor, placed, features, f)) continue;
            if (mirror)
            {
                // Left half only, plus its mirror image on the right.
                if (f[^1].x > Width / 2 - FootRoom) continue;
                var m = new List<(int x, int h)>();
                for (int i = f.Count - 1; i >= 0; i--) m.Add((Width - f[i].x, f[i].h));
                if (!Fits(floor, placed, features, m)) continue;
                features.Add(f);
                features.Add(m);
            }
            else features.Add(f);
        }
        if (features.Count == 0) return;
        features.Sort((a, b) => a[0].x.CompareTo(b[0].x));

        foreach (var f in features)
            for (int i = 0; i + 1 < f.Count; i++)
            {
                var (ax, ah) = f[i];
                var (bx, bh) = f[i + 1];
                if (ah == bh && ah > 0)
                {
                    var r = new RectI(ax, FloorY - ah, bx - ax, ah);
                    map.Solids.Add(r);
                    map.Terrain.Add(r);
                }
                else if (ax != bx && ah != bh) map.Slopes.Add(new Slope(ax, FloorY - ah, bx, FloorY - bh, FloorY));
            }

        // The outline of each floor segment that got terrain, for drawing it as one piece of ground.
        foreach (var (s1, s2) in floor)
        {
            var pts = new List<GroundPoint>();
            void Point(int x, int y)
            {
                if (pts.Count == 0 || pts[^1] != new GroundPoint(x, y)) pts.Add(new GroundPoint(x, y));
            }
            bool any = false;
            Point(s1, FloorY);
            foreach (var f in features)
            {
                if (f[0].x < s1 || f[^1].x > s2) continue;
                any = true;
                foreach (var (x, h) in f)
                {
                    if (x == s1 && pts.Count == 1 && pts[0].Y == FloorY) pts.Clear(); // a shelf starting at the wall
                    Point(x, FloorY - h);
                }
            }
            if (!any) continue;
            if (pts[^1].X != s2) Point(s2, FloorY);
            map.Ground.Add(pts.ToArray());
        }
    }

    /// <summary>A ramp's run for a rise: 1.2 to 2.4 times it, rounded up to whole tiles.</summary>
    private static int Run(Rng rng, int rise) => (rise * rng.Range(6, 12) / 5 + Tile - 1) / Tile * Tile;

    /// <summary>A hill, stepped hill or mesa whose left foot is at <paramref name="x"/>.</summary>
    private static List<(int x, int h)> Bump(Rng rng, int x)
    {
        int h = rng.Range(MinHeight / Tile, MaxHeight / Tile) * Tile;
        int kind = rng.Range(0, 9);
        var pts = new List<(int x, int h)> { (x, 0) };
        int cx = x;
        if (kind <= 1 && h >= 32)
        {
            // Stepped: up to a ledge part of the way, then on up to the top.
            int h1 = h / 2 / Tile * Tile;
            cx += Run(rng, h1);
            pts.Add((cx, h1));
            cx += rng.Range(4, 6) * Tile;
            pts.Add((cx, h1));
            cx += Run(rng, h - h1);
            pts.Add((cx, h));
        }
        else
        {
            cx += Run(rng, h);
            pts.Add((cx, h));
        }
        cx += rng.Range(4, 12) * Tile;
        pts.Add((cx, h));
        if (kind == 8 && h <= 40) pts.Add((cx, 0)); // mesa: a short cliff to hop up on this side
        else pts.Add((cx + Run(rng, h), 0));
        // Either way round.
        if (rng.Chance(0.5f))
        {
            int x0 = pts[0].x, x1 = pts[^1].x;
            var flipped = new List<(int x, int h)>();
            for (int i = pts.Count - 1; i >= 0; i--) flipped.Add((x0 + x1 - pts[i].x, pts[i].h));
            pts = flipped;
        }
        return pts;
    }

    /// <summary>A raised shelf against the left or right wall, ramping down to the floor.</summary>
    private static List<(int x, int h)> Shelf(Rng rng, bool left)
    {
        int h = rng.Range(MinHeight / Tile, MaxHeight / Tile) * Tile, top = rng.Range(6, 15) * Tile, run = Run(rng, h);
        if (left) return new List<(int x, int h)> { (Wall, h), (Wall + top, h), (Wall + top + run, 0) };
        int x2 = Width - Wall;
        return new List<(int x, int h)> { (x2 - top - run, 0), (x2 - top, h), (x2, h) };
    }

    /// <summary>
    /// True if a feature lies on one floor segment (a wall shelf may start right at the wall; anything else keeps
    /// flat floor at its feet and clear of pit edges), keeps its distance from the other features, and has no
    /// platform or tower hanging lower than <see cref="Clearance"/> over it or near its feet.
    /// </summary>
    private static bool Fits(List<(int x1, int x2)> floor, List<RectI> placed, List<List<(int x, int h)>> features, List<(int x, int h)> f)
    {
        int x1 = f[0].x, x2 = f[^1].x, top = 0;
        foreach (var (_, h) in f) top = Math.Max(top, h);
        bool onSegment = false;
        foreach (var (s1, s2) in floor)
        {
            int lo = s1 == Wall ? (f[0].h > 0 ? Wall : Wall + FootRoom) : s1 + PitMargin;
            int hi = s2 == Width - Wall ? (f[^1].h > 0 ? s2 : s2 - FootRoom) : s2 - PitMargin;
            if ((x1 == lo || x1 >= lo) && x2 <= hi && x1 >= s1 && x2 <= s2) onSegment = true;
        }
        if (!onSegment) return false;
        foreach (var o in features)
            if (x1 < o[^1].x + FootRoom && o[0].x < x2 + FootRoom) return false;
        foreach (var p in placed)
            if (p.X < x2 + FootRoom && p.Right > x1 - FootRoom && p.Bottom > FloorY - top - Clearance) return false;
        return true;
    }
}
