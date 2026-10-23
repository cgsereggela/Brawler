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

/// <summary>A jump pad on top of a surface: standing on [X1, X2] at height Y launches you upward.</summary>
public readonly record struct JumpPad(int X1, int X2, int Y)
{
    public int CenterX => (X1 + X2) / 2;
}

public enum MapStyle { Tiers, Mirror, Islands, Towers, Cave, Tunnel, Temple, Sky }

public sealed class MapLayout
{
    // 25% bigger than the original 800x450; 562 is 16:9 rounded down, and the base viewport matches it exactly.
    public const int Width = 1000;
    public const int Height = 562;
    public const int KillY = Height + 24;

    public List<RectI> Solids { get; } = new();
    public List<Surface> Surfaces { get; } = new();
    public List<SpawnPoint> Spawns { get; } = new();
    public List<JumpPad> Pads { get; } = new();
    // Dynamic extras (see ArenaFeatures). None of them is in Solids, and reachability never relies on them.
    public List<PropSpawn> Props { get; } = new();
    public List<Hazard> Hazards { get; } = new();
    public List<SawTrack> Saws { get; } = new();
    public List<Mover> Movers { get; } = new();
    /// <summary>Drifting platforms (see <see cref="Drift"/>): not solids, but their tops are in Surfaces, at their spots.</summary>
    public List<Drift> Drifts { get; } = new();
    // Floor terrain (see TerrainGen): raised flat stretches are rects in Solids (also listed in Terrain), the ramps
    // between levels are Slopes, and Ground holds each terrain-bearing floor segment's top outline for drawing.
    public List<RectI> Terrain { get; } = new();
    public List<Slope> Slopes { get; } = new();
    public List<GroundPoint[]> Ground { get; } = new();
    public MapStyle Style { get; init; }
    public int Seed { get; init; }
    public int Attempts { get; set; }
    public bool UsedFallback { get; set; }
    /// <summary>Clear air a jump or drop between surfaces needs above the higher one (per body size in NavLayout).</summary>
    public int Headroom { get; init; } = MapGenerator.Headroom;
    /// <summary>Whether the jump graph counts jump pad launches (bots plan with them; map validation never does).</summary>
    public bool UsePads { get; init; }
    /// <summary>Bots' jump graph only (see NavLayout): surfaces that are a moving platform's lane, and which mover runs it.</summary>
    public Dictionary<int, int> MoverOf { get; } = new();
    /// <summary>Bots' jump graph only: surfaces that are a drifting platform's top, and which drift it is.</summary>
    public Dictionary<int, int> DriftOf { get; } = new();

    /// <summary>The drifting platform whose top (at its spot) is <paramref name="s"/>, or -1.</summary>
    public int DriftIndex(Surface s)
    {
        for (int i = 0; i < Drifts.Count; i++)
            if (Drifts[i].Top == s) return i;
        return -1;
    }

    /// <summary>True if a rect overlaps any solid or slope.</summary>
    public bool Blocked(RectI r)
    {
        foreach (var o in Solids)
            if (o.Intersects(r)) return true;
        foreach (var s in Slopes)
            if (s.Intersects(r)) return true;
        return false;
    }
}

/// <summary>
/// Seeded random arenas. The host broadcasts only the seed; every peer rebuilds the same layout.
/// Each seed picks a <see cref="MapStyle"/> (scattered tiers, a mirrored layout, floating islands over pits,
/// towers, a cave under a ragged rock ceiling, stacked tunnels joined by shafts, a stepped temple, or floorless sky islands), and many get rolling floor terrain (<see cref="TerrainGen"/>). Each candidate is validated (spawn
/// spacing, mutual reachability between spawns) and rerolled on failure. Jump pads are added afterwards as extras: reachability never relies on them.
/// </summary>
public static class MapGenerator
{
    public const int Tile = 8;
    public const int Wall = 8;
    /// <summary>
    /// Thickness of the floor: a solid band along the bottom of the arena, deep enough for the HUD's card strip
    /// (HudUI) to sit inside it instead of over the playable space. The floor's top is <see cref="FloorY"/>.
    /// </summary>
    public const int FloorDepth = 32;
    public const int FloorY = Height - FloorDepth;
    private const int Height = MapLayout.Height;
    private const int Width = MapLayout.Width;

    // Reachability limits, derived from base movement (jump apex = v^2 / 2g ≈ 82 px at 144 px/s) with margin: a full
    // jump carries ~144 px across on the level and ~108 px onto a ledge MaxRise up. Bots jump a few px before an edge
    // and land with a body width to spare, so a level gap is planned at well under a full jump.
    public const int MaxRise = 60;
    public const int MaxGapRising = 72;
    public const int MaxGapFalling = 130;
    public const int Headroom = 26;
    public const int MinSpawnDistance = 220;

    /// <summary>
    /// How far apart spawns must be. Up to four players get the full <see cref="MinSpawnDistance"/>; past that the
    /// arena can't fit everyone that far apart, so each extra player shaves some off (eight players: 140 px).
    /// </summary>
    public static int SpawnDistance(int playerCount) => MinSpawnDistance - Math.Max(0, playerCount - 4) * 20;
    public const int TierSpacing = 50;
    /// <summary>
    /// Least clear air between the bottom of anything solid (platforms, movers, glass, the ceiling) and the top of
    /// anything under it. A body is 18 px tall (a Giant 32.4), so a normal body gets 32 px of hop before it bonks and a
    /// Giant still walks under. It equals <see cref="TierSpacing"/>, so neighbouring tiers never stack: they stagger.
    /// </summary>
    public const int MinClearance = 50;
    public const int PadWidth = 24;
    /// <summary>Pad launch speed: a ~217 px apex at base gravity (25% higher than the old 173 px launch).</summary>
    public const float PadVelocity = 531f;
    /// <summary>Open air a jump pad needs above it.</summary>
    public const int PadAir = 112;
    /// <summary>Base player gravity (StatBlock.Base), for planning pad launches.</summary>
    public const float BaseGravity = 650f;

    /// <summary>The layout style a seed asks for (a map that falls back is a plain Mirror arena instead).</summary>
    public static MapStyle StyleFor(int seed) => StyleFor(new Rng((ulong)(uint)seed), seed);

    /// <summary>
    /// The first seven styles come from the seed's first roll, as they always have, so a seed keeps its arena. A
    /// separate roll on the seed turns some seeds into the newer styles (one in ten is a floorless Sky arena).
    /// </summary>
    private static MapStyle StyleFor(Rng rng, int seed)
    {
        var style = (MapStyle)rng.Range(0, (int)MapStyle.Temple);
        int late = new Rng((ulong)(uint)seed ^ 0x5C1E5EEDUL).Range(0, 99);
        return late < 10 ? MapStyle.Sky : style;
    }

    public static MapLayout Generate(int seed, int playerCount)
    {
        var rng = new Rng((ulong)(uint)seed);
        var style = StyleFor(rng, seed);
        for (int attempt = 1; attempt <= 300; attempt++)
        {
            var layout = TryBuild(rng, seed, playerCount, style);
            if (layout != null)
            {
                layout.Attempts = attempt;
                ArenaFeatures.Add(layout);
                return layout;
            }
        }
        var fb = Fallback(seed, playerCount);
        fb.Attempts = 300;
        fb.UsedFallback = true;
        ArenaFeatures.Add(fb);
        return fb;
    }

    private static MapLayout TryBuild(Rng rng, int seed, int playerCount, MapStyle style)
    {
        var map = new MapLayout { Seed = seed, Style = style };
        AddBorders(map);

        var placed = new List<RectI>();
        // The height of a layout's high point that must be in play (the temple's top, the top tunnel), or -1.
        int summit = -1;
        var floor = style switch
        {
            MapStyle.Islands => IslandFloor(rng),
            MapStyle.Mirror => MirrorFloor(rng),
            MapStyle.Temple => Temple(rng, placed, out summit),
            MapStyle.Tunnel => PitFloor(rng, 0),
            MapStyle.Cave => PitFloor(rng, rng.Range(0, 1)),
            MapStyle.Sky => new List<(int x1, int x2)>(),
            _ => PitFloor(rng, rng.Range(0, 2)),
        };
        foreach (var s in floor)
            map.Solids.Add(new RectI(s.x1, FloorY, s.x2 - s.x1, Height - FloorY));

        switch (style)
        {
            case MapStyle.Cave:
                CaveRock(rng, placed, floor);
                TierPlatforms(rng, placed, rng.Range(4, 7), 3);
                break;
            case MapStyle.Tunnel:
                summit = Tunnels(rng, placed);
                if (summit < 0) return null;
                break;
            case MapStyle.Temple:
                TierPlatforms(rng, placed, rng.Range(5, 8), 3);
                break;
            case MapStyle.Mirror:
                MirrorPlatforms(rng, placed);
                break;
            case MapStyle.Towers:
                Towers(rng, placed, floor);
                TierPlatforms(rng, placed, rng.Range(5, 8), 3);
                break;
            case MapStyle.Islands:
                TierPlatforms(rng, placed, rng.Range(5, 8), 4);
                break;
            case MapStyle.Sky:
                SkyIslands(rng, placed);
                TierPlatforms(rng, placed, rng.Range(6, 8), 3, first: 2);
                break;
            default:
                TierPlatforms(rng, placed, rng.Range(6, 9), 4);
                Pillars(rng, placed);
                break;
        }
        map.Solids.AddRange(placed);
        TerrainGen.Add(map, rng, floor, placed);

        BuildSurfaces(map, floor, placed);
        if (!PickSpawns(map, rng, playerCount)) return null;
        if (!SpawnsMutuallyReachable(map)) return null;
        // Spawns can all reach each other and still miss half the arena (all of them on one side of a temple whose
        // last step is overhung), so these layouts also need their high point in reach.
        if (summit >= 0)
        {
            var reach = ReachableFrom(map, SurfaceIndexOf(map, map.Spawns[0]));
            bool up = false;
            for (int i = 0; i < map.Surfaces.Count; i++)
                if (reach[i] && map.Surfaces[i].Y == summit) up = true;
            if (!up) return null;
        }
        AddPads(map, rng, style);
        return map;
    }

    // ---------- floors ----------

    private static List<(int x1, int x2)> PitFloor(Rng rng, int pits)
    {
        var pitXs = new List<(int x1, int x2)>();
        for (int i = 0; i < pits; i++)
        {
            int w = rng.Range(8, 11) * Tile;
            int x = rng.Range(12, (Width - w) / Tile - 12) * Tile;
            var p = (x, x + w);
            bool clash = false;
            foreach (var q in pitXs)
                if (p.Item1 < q.x2 + 100 && q.x1 < p.Item2 + 100) clash = true;
            if (!clash) pitXs.Add(p);
        }
        return FloorBetween(pitXs);
    }

    /// <summary>Three or four islands separated by wide pits.</summary>
    private static List<(int x1, int x2)> IslandFloor(Rng rng)
    {
        int islands = rng.Range(3, 4);
        var pits = new List<(int x1, int x2)>();
        int span = (Width - Wall * 2) / islands;
        for (int i = 1; i < islands; i++)
        {
            int w = rng.Range(8, 14) * Tile;
            int x = (Wall + span * i + rng.Range(-4, 4) * Tile - w / 2) / Tile * Tile;
            pits.Add((x, x + w));
        }
        return FloorBetween(pits);
    }

    /// <summary>Symmetric floor: solid, a center pit, or a pair of mirrored pits.</summary>
    private static List<(int x1, int x2)> MirrorFloor(Rng rng)
    {
        var pits = new List<(int x1, int x2)>();
        int kind = rng.Range(0, 2);
        if (kind == 1)
        {
            int w = rng.Range(5, 7) * Tile * 2;
            pits.Add((Width / 2 - w / 2, Width / 2 + w / 2));
        }
        else if (kind == 2)
        {
            int w = rng.Range(8, 11) * Tile;
            int x = rng.Range(15, 38) * Tile;
            pits.Add((x, x + w));
            pits.Add((Width - x - w, Width - x));
        }
        return FloorBetween(pits);
    }

    /// <summary>The floor segments left between pits (sorts the pits in place), from wall to wall.</summary>
    public static List<(int x1, int x2)> FloorBetween(List<(int x1, int x2)> pits)
    {
        pits.Sort((a, b) => a.x1.CompareTo(b.x1));
        var segments = new List<(int x1, int x2)>();
        int cursor = Wall;
        foreach (var p in pits)
        {
            if (p.x1 > cursor) segments.Add((cursor, p.x1));
            cursor = Math.Max(cursor, p.x2);
        }
        if (cursor < Width - Wall) segments.Add((cursor, Width - Wall));
        return segments;
    }

    // ---------- platforms ----------

    /// <summary>
    /// Top of platform tier <paramref name="tier"/> (1 = lowest). The lowest tier leaves a thin platform exactly
    /// <see cref="MinClearance"/> above the floor (a 58 px climb, within MaxRise); each next tier is TierSpacing higher.
    /// </summary>
    public static int TierY(int tier) => FloorY - MinClearance - Tile - (tier - 1) * TierSpacing;

    /// <summary>
    /// A Sky arena's lowest tier, standing in for the floor it doesn't have: a row of wide islands across the arena,
    /// each a running jump (56-112 px) from the next, over a bottomless drop.
    /// </summary>
    private static void SkyIslands(Rng rng, List<RectI> placed)
    {
        int y = TierY(1);
        for (int x = Wall + rng.Range(2, 8) * Tile; ;)
        {
            int w = rng.Range(10, 18) * Tile;
            if (x + w > Width - Wall - 2 * Tile) w = Width - Wall - 2 * Tile - x;
            if (w < 6 * Tile) break;
            placed.Add(new RectI(x, y, w, rng.Chance(0.5f) ? Tile : Tile * 2));
            x += w + rng.Range(7, 14) * Tile;
        }
    }

    /// <summary>Floating platforms in loose height tiers, each tier one comfortable jump above the last.</summary>
    private static void TierPlatforms(Rng rng, List<RectI> placed, int tierCount, int maxPerTier, int first = 1)
    {
        for (int tier = first; tier <= tierCount; tier++)
        {
            int tierY = TierY(tier);
            int count = rng.Range(1, maxPerTier);
            for (int i = 0; i < count * 4 && count > 0; i++)
            {
                int w = rng.Range(3, 14) * Tile;
                int h = rng.Chance(0.65f) ? Tile : Tile * 2;
                int x = rng.Range(2, (Width - w) / Tile - 2) * Tile;
                // The lowest tier is already a full MaxRise above the floor, so only higher tiers get raised.
                int y = tierY - (tier > 1 && rng.Chance(0.3f) ? Tile : 0);
                if (TryPlace(placed, new RectI(x, y, w, h))) count--;
            }
        }
    }

    private static void Pillars(Rng rng, List<RectI> placed)
    {
        int pillars = rng.Range(0, 3);
        for (int i = 0; i < pillars * 4 && pillars > 0; i++)
        {
            int w = rng.Range(2, 3) * Tile;
            int h = rng.Range(6, 11) * Tile;
            int x = rng.Range(6, (Width - w) / Tile - 6) * Tile;
            int y = rng.Range(6, (FloorY - h - MinClearance) / Tile) * Tile;
            if (TryPlace(placed, new RectI(x, y, w, h))) pillars--;
        }
    }

    /// <summary>Platforms on the left half mirrored to the right, with the odd centerpiece straddling the middle.</summary>
    private static void MirrorPlatforms(Rng rng, List<RectI> placed)
    {
        int tiers = rng.Range(6, 9);
        for (int tier = 1; tier <= tiers; tier++)
        {
            int tierY = TierY(tier);
            if (rng.Chance(0.4f))
            {
                int w = rng.Range(3, 9) * Tile * 2;
                TryPlace(placed, new RectI(Width / 2 - w / 2, tierY, w, rng.Chance(0.7f) ? Tile : Tile * 2));
            }
            int count = rng.Range(1, 2);
            for (int i = 0; i < count * 4 && count > 0; i++)
            {
                int w = rng.Range(3, 14) * Tile;
                int h = rng.Chance(0.65f) ? Tile : Tile * 2;
                int x = rng.Range(2, (Width / 2 - w) / Tile - 2) * Tile;
                int y = tierY - (tier > 1 && rng.Chance(0.3f) ? Tile : 0);
                var r = new RectI(x, y, w, h);
                var m = new RectI(Width - x - w, y, w, h);
                if (r.Grow(16, MinClearance).Intersects(m) || !Fits(placed, r) || !Fits(placed, m)) continue;
                placed.Add(r);
                placed.Add(m);
                count--;
            }
        }
    }

    /// <summary>Two or three solid towers rising from the floor.</summary>
    private static void Towers(Rng rng, List<RectI> placed, List<(int x1, int x2)> floor)
    {
        int towers = rng.Range(2, 3);
        for (int i = 0; i < towers * 6 && towers > 0; i++)
        {
            var seg = floor[rng.Range(0, floor.Count - 1)];
            int w = rng.Range(3, 5) * Tile;
            int h = rng.Range(2, 4) * TierSpacing;
            int lo = (seg.x1 + 24) / Tile, hi = (seg.x2 - w - 24) / Tile;
            if (hi <= lo) continue;
            int x = rng.Range(lo, hi) * Tile;
            var tower = new RectI(x, FloorY - h, w, h);
            bool clash = false;
            foreach (var o in placed)
                if (tower.Grow(60, MinClearance).Intersects(o)) clash = true;
            if (clash) continue;
            placed.Add(tower);
            towers--;
            // A staircase of ledges up one side, so the top (and the far side) can always be reached. Ledges a tier
            // apart are too close to stack, so they zigzag between a column by the tower and one further out (16 px
            // past the widest inner ledge); the top ledge is always by the tower. The side must have room for both.
            const int inner = 24, outer = inner + 5 * Tile + 16;
            bool left = rng.Chance(0.5f);
            if (left && x - outer - 5 * Tile < Wall + 16) left = false;
            else if (!left && tower.Right + outer + 5 * Tile > Width - Wall - 16) left = true;
            int steps = 0;
            for (int y = TierY(1); y > tower.Y; y -= TierSpacing) steps++;
            for (int k = 0; k < steps; k++)
            {
                int sw = rng.Range(3, 5) * Tile;
                int off = (steps - 1 - k) % 2 == 0 ? inner : outer;
                int sx = left ? x - off - sw : tower.Right + off;
                TryPlace(placed, new RectI(sx, TierY(1 + k), sw, Tile));
            }
        }
    }

    /// <summary>
    /// A rock ceiling hanging down to <paramref name="bottom"/> (give or take two tiles, never lower), in chunks of
    /// ragged depth across the arena. Each chunk tucks 4 px up into the border ceiling so it reads as one mass.
    /// </summary>
    private static void RaggedCeiling(Rng rng, List<RectI> placed, int bottom, int jag)
    {
        for (int x = Wall; x < Width - Wall;)
        {
            int w = rng.Range(6, 16) * Tile;
            if (Width - Wall - x - w < 6 * Tile) w = Width - Wall - x;
            int b = Math.Max(Wall + Tile, bottom - rng.Range(0, jag) * Tile);
            placed.Add(new RectI(x, Wall - 4, w, b - Wall + 4));
            x += w;
        }
    }

    /// <summary>
    /// A cave: a ragged rock ceiling, stalactites hanging from it (a thick root and a narrower tip) and a few short
    /// stalagmites rising from the floor, each low enough to hop straight onto.
    /// </summary>
    private static void CaveRock(Rng rng, List<RectI> placed, List<(int x1, int x2)> floor)
    {
        RaggedCeiling(rng, placed, Wall + rng.Range(3, 7) * Tile, 3);
        var ceiling = new List<RectI>(placed);

        var tips = new List<RectI>();
        int want = rng.Range(3, 6);
        for (int i = 0; i < want * 8 && tips.Count < want; i++)
        {
            int w = rng.Range(3, 4) * Tile;
            int x = rng.Range((Wall + 48) / Tile, (Width - Wall - 48 - w) / Tile) * Tile;
            bool clash = false;
            foreach (var t in tips)
                if (x < t.Right + 64 && t.X < x + w + 64) clash = true;
            if (clash) continue;
            // Rooted in the shallowest chunk it spans (it overlaps the deeper ones), down to 56-136 px below it.
            int top = Height;
            foreach (var c in ceiling)
                if (c.X < x + w && c.Right > x) top = Math.Min(top, c.Bottom);
            int root = rng.Range(3, 7) * Tile, tip = rng.Range(4, 10) * Tile;
            var r = new RectI(x, top - 4, w, root + 4);
            var t2 = new RectI(x + Tile / 2, r.Bottom, w - Tile, tip);
            placed.Add(r);
            placed.Add(t2);
            tips.Add(t2);
        }

        int mites = rng.Range(1, 3);
        for (int i = 0; i < mites * 8 && mites > 0; i++)
        {
            var seg = floor[rng.Range(0, floor.Count - 1)];
            int w = rng.Range(2, 4) * Tile, h = rng.Range(3, 7) * Tile; // 24-56 px: one hop up
            int lo = (seg.x1 + 40) / Tile, hi = (seg.x2 - w - 40) / Tile;
            if (hi <= lo) continue;
            var r = new RectI(rng.Range(lo, hi) * Tile, FloorY - h, w, h);
            bool clash = false;
            foreach (var o in placed)
                if (r.Grow(64, MinClearance).Intersects(o)) clash = true;
            if (clash) continue;
            placed.Add(r);
            mites--;
        }
    }

    /// <summary>
    /// Stacked tunnels: one or two long rock slabs split the arena into corridors under a lowered ceiling. Each slab
    /// has two or three shafts, none above another slab's, and each shaft holds a little climb: a ledge in the middle
    /// one hop above the corridor floor, then a ledge at either side one more hop up, beside the slab's edge, from
    /// which the slab's top is one more hop. Returns the top slab's height, or -1 if the shafts didn't fit.
    /// </summary>
    private static int Tunnels(Rng rng, List<RectI> placed)
    {
        const int thick = 2 * Tile;
        int slabs = rng.Chance(0.6f) ? 2 : 1;
        int f = FloorY; // the current corridor's floor
        var below = new List<(int x1, int x2)>();
        for (int k = 0; k < slabs; k++)
        {
            // Floor, then ledges one tier (a lowest-tier platform's climb) and two tiers up, then the slab top.
            int lowY = f - MinClearance - Tile, highY = lowY - TierSpacing, top = highY - TierSpacing;
            var holes = new List<(int x1, int x2, int sw, int mw)>();
            int want = rng.Range(2, 3);
            for (int i = 0; i < 40 && holes.Count < want; i++)
            {
                int sw = rng.Range(5, 6) * Tile, mw = rng.Range(5, 7) * Tile;
                int w = 2 * sw + mw + 4 * 16;
                int x = rng.Range((Wall + 64) / Tile, (Width - Wall - 64 - w) / Tile) * Tile;
                bool clash = false;
                foreach (var h in holes)
                    if (x < h.x2 + 120 && h.x1 < x + w + 120) clash = true;
                foreach (var (b1, b2) in below)
                    if (x < b2 + 32 && b1 < x + w + 32) clash = true;
                if (!clash) holes.Add((x, x + w, sw, mw));
            }
            if (holes.Count == 0) return -1;
            holes.Sort((a, b) => a.x1.CompareTo(b.x1));

            int cursor = Wall;
            foreach (var (x1, x2, sw, mw) in holes)
            {
                placed.Add(new RectI(cursor, top, x1 - cursor, thick));
                placed.Add(new RectI(x1 + 16, highY, sw, Tile));
                placed.Add(new RectI(x2 - 16 - sw, highY, sw, Tile));
                placed.Add(new RectI((x1 + x2 - mw) / 2 / Tile * Tile, lowY, mw, Tile));
                cursor = x2;
            }
            placed.Add(new RectI(cursor, top, Width - Wall - cursor, thick));
            CorridorCover(rng, placed, lowY);
            below.Clear();
            foreach (var h in holes) below.Add((h.x1, h.x2));
            f = top;
        }
        // The top corridor: about as tall as the others with two slabs, taller with one.
        int room = slabs == 2 ? rng.Range(18, 19) * Tile : rng.Range(22, 30) * Tile;
        RaggedCeiling(rng, placed, f - room, 2);
        CorridorCover(rng, placed, f - MinClearance - Tile);
        return f;
    }

    /// <summary>One to three short platforms a hop above a corridor floor, where they fit, for cover.</summary>
    private static void CorridorCover(Rng rng, List<RectI> placed, int y)
    {
        int count = rng.Range(1, 3);
        for (int i = 0; i < count * 6 && count > 0; i++)
        {
            int w = rng.Range(4, 9) * Tile;
            int x = rng.Range(4, (Width - w) / Tile - 4) * Tile;
            if (TryPlace(placed, new RectI(x, y, w, Tile))) count--;
        }
    }

    /// <summary>
    /// A stepped temple in the middle of the arena: columns rising from the floor in steps of one hop each to a flat
    /// top, mirrored either side; sometimes a pit by each wall. Returns the floor; <paramref name="top"/> is the top's height.
    /// </summary>
    private static List<(int x1, int x2)> Temple(Rng rng, List<RectI> placed, out int top)
    {
        int steps = rng.Range(3, 5);
        int sw = rng.Range(5, 8) * Tile, sh = rng.Range(5, 7) * Tile, tw = rng.Range(4, 9) * Tile * 2;
        while (steps > 2 && (steps - 1) * sw * 2 + tw > 640) steps--;
        int cx = Width / 2 + rng.Range(-4, 4) * Tile;
        for (int k = 1; k < steps; k++)
        {
            int h = k * sh, off = tw / 2 + (steps - k) * sw;
            placed.Add(new RectI(cx - off, FloorY - h, sw, h));
            placed.Add(new RectI(cx + off - sw, FloorY - h, sw, h));
        }
        top = FloorY - steps * sh;
        placed.Add(new RectI(cx - tw / 2, top, tw, steps * sh));

        var pits = new List<(int x1, int x2)>();
        int foot = tw / 2 + (steps - 1) * sw;
        if (rng.Chance(0.5f))
        {
            int w = rng.Range(8, 10) * Tile, x = Wall + rng.Range(8, 14) * Tile;
            if (x + w + 80 < cx - foot && Width - x - w - 80 > cx + foot)
            {
                pits.Add((x, x + w));
                pits.Add((Width - x - w, Width - x));
            }
        }
        return FloorBetween(pits);
    }

    private static bool Fits(List<RectI> placed, RectI r)
    {
        // Keep MinClearance under the ceiling, above the floor and between anything within 16 px sideways.
        if (r.Y < Wall + MinClearance || r.Bottom > FloorY - MinClearance) return false;
        foreach (var o in placed)
            if (r.Grow(16, MinClearance).Intersects(o)) return false;
        return true;
    }

    private static bool TryPlace(List<RectI> placed, RectI r)
    {
        if (!Fits(placed, r)) return false;
        placed.Add(r);
        return true;
    }

    // ---------- jump pads ----------

    private static void AddPads(MapLayout map, Rng rng, MapStyle style)
    {
        int want = style == MapStyle.Islands ? rng.Range(1, 3) : rng.Range(0, 2);
        for (int i = 0; i < want * 8 && map.Pads.Count < want; i++)
        {
            var s = map.Surfaces[rng.Range(0, map.Surfaces.Count - 1)];
            if (s.Width < PadWidth + 32) continue;
            int x = rng.Range((s.X1 + 16) / 4, (s.X2 - 16 - PadWidth) / 4) * 4;
            var pad = new JumpPad(x, x + PadWidth, s.Y);
            bool ok = true;
            foreach (var sp in map.Spawns)
                if (sp.Y == s.Y && sp.X > x - 24 && sp.X < pad.X2 + 24) ok = false;
            foreach (var p in map.Pads)
                if (Math.Abs(p.CenterX - pad.CenterX) < 125) ok = false;
            // It needs open air above, or it just bonks you into a ceiling.
            if (ok && !map.Blocked(new RectI(x - 4, s.Y - PadAir, PadWidth + 8, PadAir))) map.Pads.Add(pad);
        }
    }

    /// <summary>The pad under a point standing on a surface at height <paramref name="feetY"/>, if any.</summary>
    public static bool OnPad(MapLayout map, float x, float feetY)
    {
        foreach (var p in map.Pads)
            if (x >= p.X1 - 2 && x <= p.X2 + 2 && Math.Abs(feetY - p.Y) < 3) return true;
        return false;
    }

    // ---------- shared ----------

    public static void AddBorders(MapLayout map)
    {
        map.Solids.Add(new RectI(0, 0, Wall, Height));                 // left wall
        map.Solids.Add(new RectI(Width - Wall, 0, Wall, Height));      // right wall
        map.Solids.Add(new RectI(0, 0, Width, Wall));                  // ceiling
    }

    public static void BuildSurfaces(MapLayout map, List<(int x1, int x2)> floor, List<RectI> platforms)
    {
        // Floor segments are split around anything standing on them (towers, terrain), so nobody spawns inside one.
        var cutters = new List<RectI>(platforms);
        cutters.AddRange(map.Terrain);
        foreach (var sl in map.Slopes) cutters.Add(sl.Bounds);
        foreach (var s in floor)
        {
            var open = new List<(int a, int b)> { (s.x1, s.x2) };
            foreach (var o in cutters)
            {
                if (!o.Intersects(new RectI(s.x1, FloorY - Headroom, s.x2 - s.x1, Headroom))) continue;
                var next = new List<(int a, int b)>();
                foreach (var (a, b) in open)
                {
                    if (o.Right <= a || o.X >= b) { next.Add((a, b)); continue; }
                    if (o.X > a) next.Add((a, o.X));
                    if (o.Right < b) next.Add((o.Right, b));
                }
                open = next;
            }
            foreach (var (a, b) in open)
                if (b - a >= 16) map.Surfaces.Add(new Surface(a, b, FloorY));
        }
        // Platform tops, then the tops of raised terrain.
        var tops = new List<RectI>(platforms);
        tops.AddRange(map.Terrain);
        foreach (var p in tops)
        {
            if (p.W < 16 || p.Y < Wall + Headroom) continue;
            var head = new RectI(p.X, p.Y - Headroom, p.W, Headroom);
            bool blocked = false;
            foreach (var o in map.Solids)
                if (o != p && o.Intersects(head)) { blocked = true; break; }
            foreach (var sl in map.Slopes)
                if (sl.Intersects(head)) blocked = true;
            if (!blocked) map.Surfaces.Add(new Surface(p.X, p.Right, p.Y));
        }
    }

    private static bool PickSpawns(MapLayout map, Rng rng, int playerCount)
    {
        // Candidate spawn points every 16px along each wide-enough surface (so the floor can host several),
        // only on the biggest group of surfaces that can all reach each other.
        var core = MutualCore(map);
        var candidates = new List<SpawnPoint>();
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = map.Surfaces[i];
            if (s.Width < 24 || !core[i]) continue;
            for (int x = s.X1 + 12; x <= s.X2 - 12; x += 16) candidates.Add(new SpawnPoint(x, s.Y));
        }
        if (candidates.Count < playerCount) return false;

        int minD = SpawnDistance(playerCount);
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
                    if (dx * dx + dy * dy < minD * minD) { farEnough = false; break; }
                }
                if (farEnough) map.Spawns.Add(sp);
            }
            if (map.Spawns.Count == playerCount) return true;
        }
        return false;
    }

    /// <summary>The largest set of surfaces that are all reachable from each other (a strongly connected component).</summary>
    public static bool[] MutualCore(MapLayout map)
    {
        int n = map.Surfaces.Count;
        var reach = new bool[n][];
        for (int i = 0; i < n; i++) reach[i] = ReachableFrom(map, i);
        var best = new bool[n];
        int bestSize = 0;
        var done = new bool[n];
        for (int root = 0; root < n; root++)
        {
            if (done[root]) continue;
            var comp = new bool[n];
            int size = 0;
            for (int j = 0; j < n; j++)
                if (reach[root][j] && reach[j][root]) { comp[j] = true; done[j] = true; size++; }
            if (size > bestSize) { bestSize = size; best = comp; }
        }
        return best;
    }

    /// <summary>Directed jump graph between surfaces. Exposed for tests.</summary>
    public static bool CanTraverse(Surface a, Surface b)
    {
        int gap = Math.Max(0, Math.Max(a.X1, b.X1) - Math.Min(a.X2, b.X2));
        int rise = a.Y - b.Y; // positive: b is higher
        if (rise > 0) return rise <= MaxRise && gap <= MaxGapRising;
        return gap <= MaxGapFalling;
    }

    /// <summary>
    /// <see cref="CanTraverse"/> plus obstacles: a jump or drop across a horizontal gap needs the space between the
    /// two surfaces (from a little above the higher one down to the lower one) to be free of solids, so a tower
    /// standing between two floor segments really separates them.
    /// </summary>
    public static bool CanTraverse(MapLayout map, Surface a, Surface b)
    {
        if (SlopeLinks(map, a, b)) return true;
        if (!CanTraverse(a, b)) return map.UsePads && PadTo(map, a, b) is not null;
        int gx1 = Math.Min(a.X2, b.X2), gx2 = Math.Max(a.X1, b.X1);
        if (gx2 <= gx1)
        {
            // Overlapping spans. Climbing needs a launch spot on a that isn't under b, with clear air up past b's
            // top; dropping needs b to stick out past a (or enough fall to drift under a).
            if (a.Y > b.Y)
            {
                if (LaunchSpot(map, a, b, a.X1, Math.Min(a.X2, b.X1 - 6))) return true;
                return LaunchSpot(map, a, b, Math.Max(a.X1, b.X2 + 6), a.X2);
            }
            return b.Y - a.Y >= 40 || b.X1 < a.X1 - 8 || b.X2 > a.X2 + 8;
        }
        int top = Math.Min(a.Y, b.Y) - map.Headroom, bottom = Math.Max(a.Y, b.Y);
        return !map.Blocked(new RectI(gx1, top, gx2 - gx1, bottom - top));
    }

    /// <summary>True if a slope runs from an end of surface a to the facing end of b: walking it gets you there.</summary>
    public static bool SlopeLinks(MapLayout map, Surface a, Surface b)
    {
        foreach (var s in map.Slopes)
        {
            if (a.X2 == s.X1 && a.Y == s.Y1 && b.X1 == s.X2 && b.Y == s.Y2) return true;
            if (a.X1 == s.X2 && a.Y == s.Y2 && b.X2 == s.X1 && b.Y == s.Y1) return true;
        }
        return false;
    }

    /// <summary>
    /// For a body standing on a slope at <paramref name="x"/> with its feet at <paramref name="feetY"/>: the surface at
    /// the nearer end of that slope (the one it is walking off onto), or -1 if it isn't on a slope with a surface there.
    /// </summary>
    public static int SlopeSurface(MapLayout map, float x, float feetY)
    {
        foreach (var s in map.Slopes)
        {
            if (x < s.X1 || x > s.X2 || Math.Abs(feetY - s.YAt(x)) > 6) continue;
            bool left = x - s.X1 < s.X2 - x;
            int ex = left ? s.X1 : s.X2, ey = left ? s.Y1 : s.Y2;
            for (int i = 0; i < map.Surfaces.Count; i++)
            {
                var f = map.Surfaces[i];
                if (f.Y == ey && (left ? f.X2 == ex : f.X1 == ex)) return i;
            }
        }
        return -1;
    }

    /// <summary>Highest climb a pad launch (jump held, base gravity) is planned for: ~217 px apex minus a margin.</summary>
    public const int PadRise = (int)(PadVelocity * PadVelocity / (2 * BaseGravity)) - 10;

    /// <summary>
    /// A jump pad on surface a that launches a body up and onto b (higher, beside the launch column, at most
    /// <see cref="PadRise"/> up), with clear air above the pad up past b's top; or null.
    /// </summary>
    public static JumpPad? PadTo(MapLayout map, Surface a, Surface b)
    {
        int rise = a.Y - b.Y;
        if (rise <= 0 || rise > PadRise) return null;
        foreach (var p in map.Pads)
        {
            if (p.Y != a.Y || p.X1 < a.X1 - 2 || p.X2 > a.X2 + 2) continue;
            int gap = Math.Max(0, Math.Max(p.X1, b.X1) - Math.Min(p.X2, b.X2));
            if (gap > 50 || (b.X1 < p.X2 && b.X2 > p.X1)) continue; // too far to drift over, or right overhead
            if (!map.Blocked(new RectI(p.X1, b.Y - map.Headroom, p.X2 - p.X1, a.Y - (b.Y - map.Headroom)))) return p;
        }
        return null;
    }

    /// <summary>True if [x1, x2] on surface a is wide enough to jump from, with clear air up to above b.</summary>
    private static bool LaunchSpot(MapLayout map, Surface a, Surface b, int x1, int x2)
    {
        if (x2 - x1 < 10) return false;
        return !map.Blocked(new RectI(x1, b.Y - map.Headroom, x2 - x1, a.Y - (b.Y - map.Headroom)));
    }

    /// <summary>Where on surface a to jump from to climb onto b, which overlaps it from above (NaN if nowhere).</summary>
    public static float LaunchX(MapLayout map, Surface a, Surface b, float fromX)
    {
        float best = float.NaN;
        foreach (var (x1, x2) in new[] { (a.X1, Math.Min(a.X2, b.X1 - 6)), (Math.Max(a.X1, b.X2 + 6), a.X2) })
            if (LaunchSpot(map, a, b, x1, x2))
            {
                float x = Math.Clamp(fromX, x1 + 4, x2 - 4);
                if (float.IsNaN(best) || Math.Abs(x - fromX) < Math.Abs(best - fromX)) best = x;
            }
        return best;
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
                if (!seen[j] && CanTraverse(map, map.Surfaces[i], map.Surfaces[j]))
                {
                    seen[j] = true;
                    stack.Push(j);
                }
        }
        return seen;
    }

    /// <summary>Next surface on a shortest jump path from surface <paramref name="from"/> to <paramref name="to"/>, or -1.</summary>
    public static int NextHop(MapLayout map, int from, int to)
    {
        if (from < 0 || to < 0 || from == to) return to;
        int n = map.Surfaces.Count;
        var prev = new int[n];
        Array.Fill(prev, -2);
        prev[from] = -1;
        var q = new Queue<int>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            if (i == to) break;
            for (int j = 0; j < n; j++)
                if (prev[j] == -2 && CanTraverse(map, map.Surfaces[i], map.Surfaces[j]))
                {
                    prev[j] = i;
                    q.Enqueue(j);
                }
        }
        if (prev[to] == -2) return -1;
        int step = to;
        while (prev[step] != from) step = prev[step];
        return step;
    }

    /// <summary>
    /// The jump graph as a body of the given size sees it: the same solids, but surfaces lose the stretches under
    /// overhangs too low to stand under, and jumps need headroom for exactly this body. The generator's graph assumes
    /// 26 px everywhere, so a Giant would try to walk under a 32 px gap forever and a normal bot believed it couldn't
    /// walk under a 24 px bar out of a floor pocket. It also counts jump pad launches (the generator never relies
    /// on pads, but a bot stuck in a pocket with one should use it). Pools are cut out of the surfaces they lie on,
    /// and each moving platform's lane is added as a surface (<see cref="MapLayout.MoverOf"/>).
    /// </summary>
    public static MapLayout NavLayout(MapLayout map, float bodyW, float bodyH)
    {
        // The generator plans with a fixed 26 px (a normal body plus margin); a bot only needs room for its own body.
        int headroom = (int)Math.Ceiling(bodyH) + 1;
        var nav = new MapLayout { Seed = map.Seed, Style = map.Style, Headroom = headroom, UsePads = true, Attempts = map.Attempts, UsedFallback = map.UsedFallback };
        nav.Solids.AddRange(map.Solids);
        nav.Terrain.AddRange(map.Terrain);
        nav.Slopes.AddRange(map.Slopes);
        nav.Ground.AddRange(map.Ground);
        nav.Spawns.AddRange(map.Spawns);
        nav.Pads.AddRange(map.Pads);
        int hw = (int)Math.Ceiling(bodyW * 0.5f);
        foreach (var s in map.Surfaces)
        {
            var open = new List<(int a, int b)> { (s.X1, s.X2) };
            foreach (var o in map.Solids)
            {
                // Only overhangs (solids ending above this surface) that leave less room than the body needs.
                if (o.Bottom > s.Y || o.Bottom <= s.Y - bodyH - 1 || o.Right <= s.X1 - hw || o.X >= s.X2 + hw) continue;
                open = Cut(open, o.X - hw, o.Right + hw);
            }
            // Pools: standing in one hurts, so the graph treats them as gaps. Bots land beside them and jump across.
            foreach (var h in map.Hazards)
                if (h.Y == s.Y) open = Cut(open, h.X1 - hw, h.X2 + hw);
            int drift = map.DriftIndex(s);
            foreach (var (a, b) in open)
                if (b - a >= 4)
                {
                    if (drift >= 0) nav.DriftOf[nav.Surfaces.Count] = drift;
                    nav.Surfaces.Add(new Surface(a, b, s.Y));
                }
        }
        // Moving platforms: each one's whole lane is a surface, which bots ride by waiting for the platform (BotBrain).
        for (int i = 0; i < map.Movers.Count; i++)
        {
            var m = map.Movers[i];
            nav.MoverOf[nav.Surfaces.Count] = i;
            nav.Surfaces.Add(new Surface(m.X1, m.X2 + m.W, m.Y));
        }
        nav.Hazards.AddRange(map.Hazards);
        nav.Saws.AddRange(map.Saws);
        nav.Movers.AddRange(map.Movers);
        nav.Drifts.AddRange(map.Drifts);
        return nav;
    }

    /// <summary>Spans with [x1, x2) taken out.</summary>
    private static List<(int a, int b)> Cut(List<(int a, int b)> spans, int x1, int x2)
    {
        var next = new List<(int a, int b)>();
        foreach (var (a, b) in spans)
        {
            if (x2 <= a || x1 >= b) { next.Add((a, b)); continue; }
            if (x1 > a) next.Add((a, x1));
            if (x2 < b) next.Add((x2, b));
        }
        return next;
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
        var map = new MapLayout { Seed = seed, Style = MapStyle.Mirror };
        AddBorders(map);
        map.Solids.Add(new RectI(Wall, FloorY, Width - Wall * 2, Height - FloorY));
        // Mirrored about the middle, one tier apart.
        var plats = new List<RectI>
        {
            new(150, TierY(1), 140, 8), new(710, TierY(1), 140, 8),
            new(420, TierY(2), 160, 8), new(190, TierY(3), 120, 8), new(690, TierY(3), 120, 8),
            new(430, TierY(4), 140, 8),
        };
        map.Solids.AddRange(plats);
        BuildSurfaces(map, new List<(int, int)> { (Wall, Width - Wall) }, plats);
        int[] xs = { 110, 890, 330, 670, 220, 780, 440, 560 };
        for (int i = 0; i < playerCount; i++) map.Spawns.Add(new SpawnPoint(xs[i % xs.Length], FloorY));
        return map;
    }
}
