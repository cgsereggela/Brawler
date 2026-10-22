using System;
using System.Collections.Generic;

namespace Brawler.Logic;

/// <summary>
/// A hand-made arena, as the level editor edits it and as a map code carries it (<see cref="MapCode"/>): gaps in the
/// floor, solid blocks, hills, spawn points, jump pads and the arena extras. <see cref="Build"/> turns it into the
/// same <see cref="MapLayout"/> the generator makes, so everything that plays a generated arena (collision, drawing,
/// bots, game modes) plays this one too. The seed picks the theme (<see cref="ThemeRules.ForSeed"/>) and seeds the
/// decorations, so a design looks the same every time and on every peer.
/// </summary>
public sealed class MapDesign
{
    public const int MaxName = 24;
    public const int MaxSpawns = 8;
    public const int MaxBlocks = 120, MaxHills = 8, MaxPads = 8, MaxProps = 60, MaxHazards = 8, MaxSaws = 8, MaxMovers = 8, MaxPits = 12;
    private const int Wall = MapGenerator.Wall, FloorY = MapGenerator.FloorY, Width = MapLayout.Width, Height = MapLayout.Height;

    public string Name = "";
    public int Seed;
    public MapStyle Style;
    /// <summary>Gaps in the floor, [x1, x2).</summary>
    public List<(int x1, int x2)> Pits = new();
    public List<RectI> Blocks = new();
    /// <summary>Floor terrain: each hill's (x, height above the floor) corners, foot to foot (see <see cref="TerrainGen"/>).</summary>
    public List<List<(int x, int h)>> Hills = new();
    public List<SpawnPoint> Spawns = new();
    public List<JumpPad> Pads = new();
    public List<PropSpawn> Props = new();
    public List<Hazard> Hazards = new();
    public List<SawTrack> Saws = new();
    public List<Mover> Movers = new();

    public ThemeId Theme => ThemeRules.ForSeed(Seed);

    /// <summary>The first seed at or after <paramref name="from"/> whose theme is <paramref name="theme"/>.</summary>
    public static int SeedFor(ThemeId theme, int from)
    {
        int s = from;
        while (ThemeRules.ForSeed(s) != theme) s++;
        return s;
    }

    /// <summary>Changes the theme, keeping everything else (the decorations get a new seed).</summary>
    public void SetTheme(ThemeId theme) => Seed = SeedFor(theme, Seed + 1);

    /// <summary>A generated arena, ready to edit: everything in it becomes a piece the editor can move or delete.</summary>
    public static MapDesign From(MapLayout map)
    {
        var d = new MapDesign { Seed = map.Seed, Style = map.Style };
        var floor = new List<RectI>();
        var terrain = new HashSet<RectI>(map.Terrain);
        foreach (var r in map.Solids)
        {
            if (IsBorder(r) || terrain.Contains(r)) continue;
            if (r.Y == FloorY && r.H == Height - FloorY) floor.Add(r);
            else d.Blocks.Add(r);
        }
        floor.Sort((a, b) => a.X.CompareTo(b.X));
        int cursor = Wall;
        foreach (var f in floor)
        {
            if (f.X > cursor) d.Pits.Add((cursor, f.X));
            cursor = Math.Max(cursor, f.Right);
        }
        if (cursor < Width - Wall) d.Pits.Add((cursor, Width - Wall));
        d.Hills.AddRange(TerrainGen.Features(map));
        d.Spawns.AddRange(map.Spawns);
        d.Pads.AddRange(map.Pads);
        d.Props.AddRange(map.Props);
        d.Hazards.AddRange(map.Hazards);
        d.Saws.AddRange(map.Saws);
        d.Movers.AddRange(map.Movers);
        return d;
    }

    /// <summary>A copy of a generated arena of the given style and theme (its seed is searched from <paramref name="from"/>).</summary>
    public static MapDesign Generate(MapStyle style, ThemeId theme, int from)
    {
        for (int s = from; ; s++)
            if (ThemeRules.ForSeed(s) == theme && MapGenerator.StyleFor(s) == style)
            {
                var map = MapGenerator.Generate(s, 4);
                if (!map.UsedFallback) return From(map);
            }
    }

    /// <summary>Just the walls and a solid floor, in the given theme.</summary>
    public static MapDesign Empty(ThemeId theme, int from) => new() { Seed = SeedFor(theme, from), Style = MapStyle.Tiers };

    private static bool IsBorder(RectI r) =>
        r == new RectI(0, 0, Wall, Height) || r == new RectI(Width - Wall, 0, Wall, Height) || r == new RectI(0, 0, Width, Wall);

    public List<(int x1, int x2)> Floor() => MapGenerator.FloorBetween(new List<(int x1, int x2)>(Pits));

    /// <summary>
    /// The playable arena. Spawns that stand on something are kept; when there are fewer than
    /// <paramref name="playerCount"/>, more are picked on the ground everyone can reach, as far apart as fit.
    /// Hills that don't stand on one stretch of floor are left out (see <see cref="Problems"/>).
    /// </summary>
    public MapLayout Build(int playerCount)
    {
        var map = new MapLayout { Seed = Seed, Style = Style, Attempts = 1 };
        MapGenerator.AddBorders(map);
        var floor = Floor();
        foreach (var (x1, x2) in floor) map.Solids.Add(new RectI(x1, FloorY, x2 - x1, Height - FloorY));
        var blocks = new List<RectI>();
        foreach (var b in Blocks)
            if (Clip(b) is { } c) blocks.Add(c);
        map.Solids.AddRange(blocks);
        var hills = new List<List<(int x, int h)>>();
        foreach (var h in Hills)
            if (h.Count >= 2 && TerrainGen.SegmentOf(floor, h) >= 0) hills.Add(new List<(int x, int h)>(h));
        TerrainGen.Apply(map, floor, hills);
        MapGenerator.BuildSurfaces(map, floor, blocks);

        foreach (var sp in Spawns)
            if (MapGenerator.SurfaceIndexOf(map, sp) >= 0) map.Spawns.Add(sp);
        if (map.Spawns.Count < playerCount) FillSpawns(map, playerCount);
        map.Pads.AddRange(Pads);
        map.Props.AddRange(Props);
        map.Hazards.AddRange(Hazards);
        map.Saws.AddRange(Saws);
        map.Movers.AddRange(Movers);
        return map;
    }

    /// <summary>A block cut to the inside of the arena walls, or null if nothing of it is left.</summary>
    private static RectI? Clip(RectI r)
    {
        int x1 = Math.Max(r.X, Wall), y1 = Math.Max(r.Y, Wall - 4), x2 = Math.Min(r.Right, Width - Wall), y2 = Math.Min(r.Bottom, Height);
        return x2 - x1 >= 2 && y2 - y1 >= 2 ? new RectI(x1, y1, x2 - x1, y2 - y1) : null;
    }

    /// <summary>
    /// Tops up the spawns to <paramref name="count"/> from points along the ground everyone can reach: each new one as far
    /// from the others as the generator wants, then closer if they don't fit. Seeded, so every peer picks the same.
    /// </summary>
    private void FillSpawns(MapLayout map, int count)
    {
        var core = map.Surfaces.Count > 0 ? MapGenerator.MutualCore(map) : Array.Empty<bool>();
        var candidates = new List<SpawnPoint>();
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = map.Surfaces[i];
            if (s.Width < 24 || !core[i]) continue;
            for (int x = s.X1 + 12; x <= s.X2 - 12; x += 16) candidates.Add(new SpawnPoint(x, s.Y));
        }
        // Points on jump pads or in pools make poor spawns.
        candidates.RemoveAll(c => Pads.Exists(p => p.Y == c.Y && c.X > p.X1 - 12 && c.X < p.X2 + 12)
                                  || Hazards.Exists(h => h.Y == c.Y && c.X > h.X1 - 12 && c.X < h.X2 + 12));
        var rng = new Rng((ulong)(uint)Seed ^ 0x5BA7115UL);
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = rng.Range(0, i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        for (int minD = MapGenerator.SpawnDistance(count); map.Spawns.Count < count && minD >= 0; minD -= 30)
            foreach (var c in candidates)
            {
                if (map.Spawns.Count >= count) break;
                bool far = true;
                foreach (var o in map.Spawns)
                {
                    int dx = o.X - c.X, dy = o.Y - c.Y;
                    if (dx * dx + dy * dy < minD * minD || o == c) { far = false; break; }
                }
                if (far) map.Spawns.Add(c);
            }
        // Nothing to stand on at all: drop everyone in the middle (Problems says so).
        if (map.Spawns.Count == 0) map.Spawns.Add(new SpawnPoint(Width / 2, FloorY));
    }

    /// <summary>What would play badly, in plain words for the editor (empty when the arena is fine).</summary>
    public List<string> Problems()
    {
        var list = new List<string>();
        var map = Build(0);
        var floor = Floor();
        if (map.Surfaces.Count == 0) list.Add("There's no ground to stand on.");
        int left = 0;
        foreach (var h in Hills)
            if (h.Count < 2 || TerrainGen.SegmentOf(floor, h) < 0) left++;
        if (left > 0) list.Add(left == 1 ? "A hill runs over a gap in the floor, so it's left out." : $"{left} hills run over gaps in the floor, so they're left out.");

        var idx = new List<int>();
        for (int i = 0; i < Spawns.Count; i++)
        {
            int s = MapGenerator.SurfaceIndexOf(map, Spawns[i]);
            if (s < 0) list.Add($"Spawn {i + 1} isn't standing on anything, so it's skipped.");
            idx.Add(s);
        }
        for (int i = 0; i < idx.Count; i++)
        {
            if (idx[i] < 0) continue;
            var reach = MapGenerator.ReachableFrom(map, idx[i]);
            for (int j = 0; j < idx.Count; j++)
                if (j != i && idx[j] >= 0 && !reach[idx[j]])
                {
                    list.Add($"From spawn {i + 1} you can't get to spawn {j + 1} (without jump pads or moving platforms).");
                    i = idx.Count; // one is enough to say
                    break;
                }
        }
        if (Spawns.Count < 2) list.Add(Spawns.Count == 0 ? "No spawns placed: players start at spots picked for them." : "Only one spawn placed: the rest are picked for you.");

        int floating = 0;
        foreach (var p in Pads)
            if (!OnSurface(map, p.X1, p.X2, p.Y)) floating++;
        if (floating > 0) list.Add(floating == 1 ? "A jump pad isn't on the ground." : $"{floating} jump pads aren't on the ground.");
        int pools = 0;
        foreach (var h in Hazards)
            if (!OnSurface(map, h.X1, h.X2, h.Y)) pools++;
        if (pools > 0) list.Add(pools == 1 ? "A pool isn't on the ground." : $"{pools} pools aren't on the ground.");
        return list;
    }

    private static bool OnSurface(MapLayout map, int x1, int x2, int y)
    {
        foreach (var s in map.Surfaces)
            if (s.Y == y && x1 >= s.X1 - 2 && x2 <= s.X2 + 2) return true;
        return false;
    }

    public MapDesign Copy()
    {
        var d = new MapDesign { Name = Name, Seed = Seed, Style = Style };
        d.Pits.AddRange(Pits);
        d.Blocks.AddRange(Blocks);
        foreach (var h in Hills) d.Hills.Add(new List<(int x, int h)>(h));
        d.Spawns.AddRange(Spawns);
        d.Pads.AddRange(Pads);
        d.Props.AddRange(Props);
        d.Hazards.AddRange(Hazards);
        d.Saws.AddRange(Saws);
        d.Movers.AddRange(Movers);
        return d;
    }

    // ---------- editing helpers ----------

    /// <summary>
    /// The top of the nearest walkable surface at or below (x, y), for dropping a spawn, pad or prop onto: the
    /// highest one that starts below <paramref name="y"/> - 2 and spans x; or null over a bottomless pit.
    /// </summary>
    public static Surface? SurfaceBelow(MapLayout map, int x, int y)
    {
        Surface? best = null;
        foreach (var s in map.Surfaces)
            if (x >= s.X1 && x <= s.X2 && s.Y >= y - 2 && (best == null || s.Y < best.Value.Y)) best = s;
        return best;
    }

    /// <summary>How long a sawblade takes there and back on a track this long (as the generator times them).</summary>
    public static float SawPeriod(float x1, float y1, float x2, float y2) =>
        MathF.Max(2.2f, MathF.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) * 2 / 55f);

    /// <summary>How long a moving platform takes there and back across this lane.</summary>
    public static float MoverPeriod(int x1, int x2) => MathF.Max(3f, (x2 - x1) * 2 / 56f);
}
