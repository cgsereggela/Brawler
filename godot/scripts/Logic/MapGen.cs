using System;
using System.Collections.Generic;

namespace Brawler.Logic;

public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Intersects(RectI o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public RectI Grow(int dx, int dy) => new(X - dx, Y - dy, W + dx * 2, H + dy * 2);
    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;
}

/// <summary>A walkable top edge: [X1, X2] at height Y (y grows downward).</summary>
public readonly record struct Surface(int X1, int X2, int Y)
{
    public int Width => X2 - X1;
    public int CenterX => (X1 + X2) / 2;
}

public readonly record struct SpawnPoint(int X, int Y);

public sealed class MapLayout
{
    public const int Width = 640;
    public const int Height = 360;
    public const int KillY = Height + 24;

    public List<RectI> Solids { get; } = new();
    public List<Surface> Surfaces { get; } = new();
    public List<SpawnPoint> Spawns { get; } = new();
    public int Seed { get; init; }
    public int Attempts { get; set; }
    public bool UsedFallback { get; set; }
}

/// <summary>
/// Seeded random-platform arenas. The host broadcasts only the seed; every peer rebuilds the same layout.
/// Each candidate is validated (spawn spacing, mutual reachability between spawns) and rerolled on failure.
/// </summary>
public static class MapGenerator
{
    public const int Tile = 8;
    public const int Wall = 8;
    public const int FloorY = Height - 16;
    private const int Height = MapLayout.Height;
    private const int Width = MapLayout.Width;

    // Reachability limits, derived from base movement (jump apex = v^2 / 2g ≈ 60px) with margin.
    public const int MaxRise = 48;
    public const int MaxGapRising = 56;
    public const int MaxGapFalling = 110;
    public const int Headroom = 26;
    public const int MinSpawnDistance = 140;
    public const int TierSpacing = 40;

    public static MapLayout Generate(int seed, int playerCount)
    {
        var rng = new Rng((ulong)(uint)seed);
        for (int attempt = 1; attempt <= 300; attempt++)
        {
            var layout = TryBuild(rng, seed, playerCount);
            if (layout != null)
            {
                layout.Attempts = attempt;
                return layout;
            }
        }
        var fb = Fallback(seed, playerCount);
        fb.Attempts = 300;
        fb.UsedFallback = true;
        return fb;
    }

    private static MapLayout TryBuild(Rng rng, int seed, int playerCount)
    {
        var map = new MapLayout { Seed = seed };
        AddBorders(map);

        // Floor, optionally split by pits.
        var floorSegments = new List<(int x1, int x2)>();
        int pits = rng.Range(0, 2);
        var pitXs = new List<(int x1, int x2)>();
        for (int i = 0; i < pits; i++)
        {
            int w = rng.Range(6, 9) * Tile;
            int x = rng.Range(10, (Width - w) / Tile - 10) * Tile;
            var p = (x, x + w);
            bool clash = false;
            foreach (var q in pitXs)
                if (p.Item1 < q.x2 + 80 && q.x1 < p.Item2 + 80) clash = true;
            if (!clash) pitXs.Add(p);
        }
        pitXs.Sort((a, b) => a.x1.CompareTo(b.x1));
        int cursor = Wall;
        foreach (var p in pitXs)
        {
            floorSegments.Add((cursor, p.x1));
            cursor = p.x2;
        }
        floorSegments.Add((cursor, Width - Wall));
        foreach (var s in floorSegments)
            map.Solids.Add(new RectI(s.x1, FloorY, s.x2 - s.x1, Height - FloorY));

        // Floating platforms in loose height tiers (each tier is one comfortable jump above the last),
        // with random count, width and x per tier, plus the occasional pillar.
        var placed = new List<RectI>();
        int tierCount = rng.Range(4, 7);
        for (int tier = 1; tier <= tierCount; tier++)
        {
            int tierY = FloorY - tier * TierSpacing;
            int count = rng.Range(1, 3);
            for (int i = 0; i < count * 4 && count > 0; i++)
            {
                int w = rng.Range(3, 14) * Tile;
                int h = rng.Chance(0.65f) ? Tile : Tile * 2;
                int x = rng.Range(2, (Width - w) / Tile - 2) * Tile;
                int y = tierY - (rng.Chance(0.3f) ? Tile : 0);
                var r = new RectI(x, y, w, h);
                if (TryPlace(placed, r)) count--;
            }
        }
        int pillars = rng.Range(0, 2);
        for (int i = 0; i < pillars * 4 && pillars > 0; i++)
        {
            int w = rng.Range(2, 3) * Tile;
            int h = rng.Range(5, 9) * Tile;
            int x = rng.Range(6, (Width - w) / Tile - 6) * Tile;
            int y = rng.Range(6, (FloorY - h - 32) / Tile) * Tile;
            if (TryPlace(placed, new RectI(x, y, w, h))) pillars--;
        }
        map.Solids.AddRange(placed);

        BuildSurfaces(map, floorSegments, placed);
        if (!PickSpawns(map, rng, playerCount)) return null;
        if (!SpawnsMutuallyReachable(map)) return null;
        return map;
    }

    private static bool TryPlace(List<RectI> placed, RectI r)
    {
        if (r.Y < Wall + 32 || r.Bottom > FloorY - 24) return false; // keep corridors under the ceiling and above the floor
        foreach (var o in placed)
            if (r.Grow(16, 22).Intersects(o)) return false;
        placed.Add(r);
        return true;
    }

    private static void AddBorders(MapLayout map)
    {
        map.Solids.Add(new RectI(0, 0, Wall, Height));                 // left wall
        map.Solids.Add(new RectI(Width - Wall, 0, Wall, Height));      // right wall
        map.Solids.Add(new RectI(0, 0, Width, Wall));                  // ceiling
    }

    private static void BuildSurfaces(MapLayout map, List<(int x1, int x2)> floor, List<RectI> platforms)
    {
        foreach (var s in floor)
            if (s.x2 - s.x1 >= 16) map.Surfaces.Add(new Surface(s.x1, s.x2, FloorY));
        foreach (var p in platforms)
        {
            if (p.W < 16 || p.Y < Wall + Headroom) continue;
            var head = new RectI(p.X, p.Y - Headroom, p.W, Headroom);
            bool blocked = false;
            foreach (var o in map.Solids)
                if (o != p && o.Intersects(head)) { blocked = true; break; }
            if (!blocked) map.Surfaces.Add(new Surface(p.X, p.Right, p.Y));
        }
    }

    private static bool PickSpawns(MapLayout map, Rng rng, int playerCount)
    {
        // Candidate spawn points every 16px along each wide-enough surface (so the floor can host several).
        var candidates = new List<SpawnPoint>();
        foreach (var s in map.Surfaces)
        {
            if (s.Width < 24) continue;
            for (int x = s.X1 + 12; x <= s.X2 - 12; x += 16) candidates.Add(new SpawnPoint(x, s.Y));
        }
        if (candidates.Count < playerCount) return false;

        for (int tries = 0; tries < 40; tries++)
        {
            map.Spawns.Clear();
            var pool = new List<SpawnPoint>(candidates);
            while (map.Spawns.Count < playerCount && pool.Count > 0)
            {
                int i = rng.Range(0, pool.Count - 1);
                var sp = pool[i];
                pool.RemoveAt(i);
                bool farEnough = true;
                foreach (var o in map.Spawns)
                {
                    int dx = o.X - sp.X, dy = o.Y - sp.Y;
                    if (dx * dx + dy * dy < MinSpawnDistance * MinSpawnDistance) { farEnough = false; break; }
                }
                if (farEnough) map.Spawns.Add(sp);
            }
            if (map.Spawns.Count == playerCount) return true;
        }
        return false;
    }

    /// <summary>Directed jump graph between surfaces. Exposed for tests.</summary>
    public static bool CanTraverse(Surface a, Surface b)
    {
        int gap = Math.Max(0, Math.Max(a.X1, b.X1) - Math.Min(a.X2, b.X2));
        int rise = a.Y - b.Y; // positive: b is higher
        if (rise > 0) return rise <= MaxRise && gap <= MaxGapRising;
        return gap <= MaxGapFalling;
    }

    public static bool[] ReachableFrom(MapLayout map, int start)
    {
        int n = map.Surfaces.Count;
        var seen = new bool[n];
        var stack = new Stack<int>();
        stack.Push(start);
        seen[start] = true;
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            for (int j = 0; j < n; j++)
                if (!seen[j] && CanTraverse(map.Surfaces[i], map.Surfaces[j]))
                {
                    seen[j] = true;
                    stack.Push(j);
                }
        }
        return seen;
    }

    public static int SurfaceIndexOf(MapLayout map, SpawnPoint sp)
    {
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = map.Surfaces[i];
            if (s.Y == sp.Y && sp.X >= s.X1 && sp.X <= s.X2) return i;
        }
        return -1;
    }

    public static bool SpawnsMutuallyReachable(MapLayout map)
    {
        var idx = new List<int>();
        foreach (var sp in map.Spawns)
        {
            int i = SurfaceIndexOf(map, sp);
            if (i < 0) return false;
            idx.Add(i);
        }
        foreach (int a in idx)
        {
            var reach = ReachableFrom(map, a);
            foreach (int b in idx)
                if (!reach[b]) return false;
        }
        return true;
    }

    private static MapLayout Fallback(int seed, int playerCount)
    {
        var map = new MapLayout { Seed = seed };
        AddBorders(map);
        map.Solids.Add(new RectI(Wall, FloorY, Width - Wall * 2, Height - FloorY));
        var plats = new List<RectI>
        {
            new(96, 280, 96, 8), new(448, 280, 96, 8),
            new(272, 240, 96, 8), new(120, 200, 80, 8), new(440, 200, 80, 8),
        };
        map.Solids.AddRange(plats);
        BuildSurfaces(map, new List<(int, int)> { (Wall, Width - Wall) }, plats);
        int[] xs = { 80, 560, 200, 440 };
        for (int i = 0; i < playerCount; i++) map.Spawns.Add(new SpawnPoint(xs[i % xs.Length], FloorY));
        return map;
    }
}
