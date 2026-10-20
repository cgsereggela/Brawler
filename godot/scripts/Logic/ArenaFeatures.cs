using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brawler.Logic;

public enum PropKind { Crate, Barrel, Glass }

/// <summary>A dynamic block placed at battle start (top-left and size). Crates and barrels can be pushed; all break.</summary>
public readonly record struct PropSpawn(PropKind Kind, int X, int Y, int W, int H)
{
    public RectI Rect => new(X, Y, W, H);
    public bool Pushable => Kind != PropKind.Glass;
    public float MaxHp => Kind switch { PropKind.Crate => 60, PropKind.Barrel => 30, _ => 14 };
}

public enum HazardKind { Magma, Poison }

/// <summary>A pool lying on a surface, [X1, X2] at height Y. Standing in it hurts over time; poison lingers.</summary>
public readonly record struct Hazard(HazardKind Kind, int X1, int X2, int Y)
{
    /// <summary>How far above the surface the pool reaches feet.</summary>
    public const int Depth = 6;
    /// <summary>Seconds poison keeps working after you step out.</summary>
    public const float PoisonLinger = 2f;
    public static float DpsOf(HazardKind kind) => kind == HazardKind.Magma ? 45 : 16;
    public float Dps => DpsOf(Kind);
    public RectI Rect => new(X1, Y - Depth, X2 - X1, Depth + 2);
}

/// <summary>A sawblade sliding back and forth between two points on a fixed period.</summary>
public readonly record struct SawTrack(float X1, float Y1, float X2, float Y2, float Radius, float Period, float Phase)
{
    public const float Damage = 22, Knockback = 300, Cooldown = 0.45f;
    public Vector2 At(float t) => Vector2.Lerp(new Vector2(X1, Y1), new Vector2(X2, Y2), ArenaFeatures.Shuttle(t, Period, Phase));
}

/// <summary>A platform gliding back and forth horizontally; its top-left goes from (X1, Y) to (X2, Y).</summary>
public readonly record struct Mover(int X1, int X2, int Y, int W, int H, float Period, float Phase)
{
    public float XAt(float t) => X1 + (X2 - X1) * ArenaFeatures.Shuttle(t, Period, Phase);
    /// <summary>Everything the platform ever covers.</summary>
    public RectI Lane => new(X1, Y, X2 - X1 + W, H);
}

/// <summary>
/// Seeded extras added to a validated arena: crates and explosive barrels to push around and shoot apart, glass
/// bridges over pits, moving platforms, magma and poison pools, and sawblades. None of them is part of the jump graph
/// (the arena is fully playable without them), and each stays clear of spawns, jump pads and the others. Which
/// hazards appear, and how often, follows the seed's theme (<see cref="ThemeRules"/>): no magma on Toxic Grove, etc.
/// Positions of moving parts are pure functions of the battle clock, so every peer agrees without syncing them.
/// </summary>
public static class ArenaFeatures
{
    public const int CrateSize = 14, BarrelW = 12, BarrelH = 16, GlassTile = 8, MoverW = 40, MoverH = 8;
    public const float SawRadius = 9;

    /// <summary>0..1..0 over one period, easing at the ends (a smooth back-and-forth).</summary>
    public static float Shuttle(float t, float period, float phase)
    {
        if (period <= 0) return 0;
        return 0.5f - 0.5f * MathF.Cos((t / period + phase) * MathF.Tau);
    }

    /// <summary>Adds extras that suit the theme the seed picks (see <see cref="ThemeRules"/>).</summary>
    public static void Add(MapLayout map) => Add(map, ThemeRules.ForSeed(map.Seed));

    public static void Add(MapLayout map, ThemeId theme)
    {
        var rules = ThemeRules.Rules(theme);
        var rng = new Rng(((ulong)(uint)map.Seed << 1) ^ 0x51F15EEDUL);
        // Spots extras stay out of: around spawns, the air above jump pads, and each other.
        var keep = new List<RectI>();
        foreach (var sp in map.Spawns) keep.Add(new RectI(sp.X - 26, sp.Y - 40, 52, 46));
        foreach (var p in map.Pads) keep.Add(new RectI(p.X1 - 10, p.Y - 90, p.X2 - p.X1 + 20, 96));

        Pits(map, rng, keep);
        OpenAir(map, rng, keep);
        Pools(map, rng, keep, rules);
        Saws(map, rng, keep, rules);
        Crates(map, rng, keep, rules);
    }

    private static bool Clear(MapLayout map, List<RectI> keep, RectI r)
    {
        if (r.X < MapGenerator.Wall || r.Right > MapLayout.Width - MapGenerator.Wall || r.Y < MapGenerator.Wall) return false;
        foreach (var o in map.Solids)
            if (o.Intersects(r)) return false;
        foreach (var o in keep)
            if (o.Intersects(r)) return false;
        return true;
    }

    // ---------- pits: glass bridges or moving platforms ----------

    private static void Pits(MapLayout map, Rng rng, List<RectI> keep)
    {
        var floor = new List<RectI>();
        foreach (var r in map.Solids)
            if (r.Y == MapGenerator.FloorY) floor.Add(r);
        floor.Sort((a, b) => a.X.CompareTo(b.X));
        for (int i = 0; i + 1 < floor.Count; i++)
        {
            int x1 = floor[i].Right, x2 = floor[i + 1].X;
            if (x2 - x1 < 32) continue;
            if (rng.Chance(0.4f))
            {
                // Glass across the pit at floor level: a shortcut until someone shoots it out from under you.
                if (!Clear(map, keep, new RectI(x1, MapGenerator.FloorY - 30, x2 - x1, 30))) continue;
                for (int x = x1; x + GlassTile <= x2; x += GlassTile)
                    map.Props.Add(new PropSpawn(PropKind.Glass, x, MapGenerator.FloorY, GlassTile, GlassTile));
                keep.Add(new RectI(x1, MapGenerator.FloorY - 30, x2 - x1, 38));
            }
            else if (rng.Chance(0.55f))
            {
                int lx1 = Math.Max(MapGenerator.Wall + 4, x1 - 40), lx2 = Math.Min(MapLayout.Width - MapGenerator.Wall - 4 - MoverW, x2 + 40 - MoverW);
                foreach (int lift in new[] { 56, 72, 88 })
                    if (TryMover(map, rng, keep, lx1, lx2, MapGenerator.FloorY - lift)) break;
            }
        }
    }

    /// <summary>A mover on [lx1, lx2] at height y, if its whole lane (plus room to ride it and pass under) is free.</summary>
    private static bool TryMover(MapLayout map, Rng rng, List<RectI> keep, int lx1, int lx2, int y)
    {
        if (lx2 - lx1 < 48) return false;
        var lane = new RectI(lx1 - 2, y - 34, lx2 - lx1 + MoverW + 4, MoverH + 34 + 22);
        if (!Clear(map, keep, lane)) return false;
        float period = MathF.Max(3f, (lx2 - lx1) * 2 / 45f);
        map.Movers.Add(new Mover(lx1, lx2, y, MoverW, MoverH, period, rng.Range(0f, 1f)));
        keep.Add(lane);
        return true;
    }

    /// <summary>Maybe a mover in open air (if no pit got one) and maybe a floating pane of glass.</summary>
    private static void OpenAir(MapLayout map, Rng rng, List<RectI> keep)
    {
        if (map.Movers.Count == 0 && rng.Chance(0.45f))
            for (int i = 0; i < 30; i++)
            {
                int y = MapGenerator.FloorY - MapGenerator.TierSpacing * rng.Range(1, 6) - 20;
                int travel = rng.Range(10, 20) * 8;
                int x = rng.Range(3, (MapLayout.Width - travel - MoverW) / 8 - 3) * 8;
                if (TryMover(map, rng, keep, x, x + travel, y)) break;
            }

        bool hasGlass = map.Props.Exists(p => p.Kind == PropKind.Glass);
        if (!hasGlass && rng.Chance(0.35f))
            for (int i = 0; i < 30; i++)
            {
                int tiles = rng.Range(4, 7), w = tiles * GlassTile;
                int y = MapGenerator.FloorY - MapGenerator.TierSpacing * rng.Range(1, 5) - 16;
                int x = rng.Range(3, (MapLayout.Width - w) / 8 - 3) * 8;
                var room = new RectI(x - 16, y - 30, w + 32, GlassTile + 30 + 24);
                if (!Clear(map, keep, room)) continue;
                for (int k = 0; k < tiles; k++) map.Props.Add(new PropSpawn(PropKind.Glass, x + k * GlassTile, y, GlassTile, GlassTile));
                keep.Add(room);
                break;
            }
    }

    // ---------- pools ----------

    private static void Pools(MapLayout map, Rng rng, List<RectI> keep, FeatureRules rules)
    {
        int want = rng.Chance(rules.PoolChance) ? (rng.Chance(0.4f) ? 2 : 1) : 0;
        var used = new HashSet<int>();
        for (int i = 0; i < 40 && map.Hazards.Count < want; i++)
        {
            int si = rng.Range(0, map.Surfaces.Count - 1);
            var s = map.Surfaces[si];
            int w = rng.Range(3, 6) * 8;
            if (used.Contains(si) || s.Width < w + 32) continue;
            int x = rng.Range((s.X1 + 8) / 4, (s.X2 - 8 - w) / 4) * 4;
            var zone = new RectI(x, s.Y - 30, w, 34);
            if (!ClearOfKeep(keep, zone)) continue;
            var kind = rng.Range(0f, rules.MagmaWeight + rules.PoisonWeight) < rules.MagmaWeight ? HazardKind.Magma : HazardKind.Poison;
            map.Hazards.Add(new Hazard(kind, x, x + w, s.Y));
            keep.Add(zone.Grow(10, 0));
            used.Add(si);
        }
    }

    private static bool ClearOfKeep(List<RectI> keep, RectI r)
    {
        foreach (var o in keep)
            if (o.Intersects(r)) return false;
        return true;
    }

    // ---------- sawblades ----------

    private static void Saws(MapLayout map, Rng rng, List<RectI> keep, FeatureRules rules)
    {
        int want = rng.Chance(rules.SawChance) ? (rng.Chance(0.35f) ? 2 : 1) : 0;
        float r = SawRadius;
        for (int i = 0; i < 40 && map.Saws.Count < want; i++)
        {
            var s = map.Surfaces[rng.Range(0, map.Surfaces.Count - 1)];
            if (s.Width < 48) continue;
            float x1 = s.X1 + 10, x2 = s.X2 - 10;
            if (x2 - x1 > 110)
            {
                float len = rng.Range(60f, 110f);
                x1 = rng.Range(x1, x2 - len);
                x2 = x1 + len;
            }
            float y = s.Y - 2; // mostly above the surface, its bottom teeth sunk into a slot
            var zone = new RectI((int)(x1 - r - 6), (int)(y - r - 30), (int)(x2 - x1 + 2 * r + 12), (int)(r + 34));
            if (!ClearOfKeep(keep, zone)) continue;
            float period = MathF.Max(2.2f, (x2 - x1) * 2 / 55f);
            map.Saws.Add(new SawTrack(x1, y, x2, y, r, period, rng.Range(0f, 1f)));
            keep.Add(zone);
        }
    }

    // ---------- crates and barrels ----------

    private static void Crates(MapLayout map, Rng rng, List<RectI> keep, FeatureRules rules)
    {
        int crates = rng.Range(1, 4), barrels = rng.Chance(rules.BarrelChance) ? rng.Range(1, 2) : 0;
        var todo = new List<PropKind>();
        for (int i = 0; i < crates; i++) todo.Add(PropKind.Crate);
        for (int i = 0; i < barrels; i++) todo.Add(PropKind.Barrel);
        foreach (var kind in todo)
            for (int i = 0; i < 40; i++)
            {
                var s = map.Surfaces[rng.Range(0, map.Surfaces.Count - 1)];
                int w = kind == PropKind.Crate ? CrateSize : BarrelW, h = kind == PropKind.Crate ? CrateSize : BarrelH;
                if (s.Width < w + 16) continue;
                // Sometimes a second crate on top: a little stack to hop up or knock over.
                bool stack = kind == PropKind.Crate && rng.Chance(0.3f);
                int total = stack ? h * 2 : h;
                int x = rng.Range(s.X1 + 4, s.X2 - 4 - w);
                if (!Clear(map, keep, new RectI(x - 2, s.Y - total - 34, w + 4, total + 34))) continue;
                map.Props.Add(new PropSpawn(kind, x, s.Y - h, w, h));
                if (stack) map.Props.Add(new PropSpawn(kind, x, s.Y - h * 2, w, h));
                keep.Add(new RectI(x - 4, s.Y - total - 4, w + 8, total + 4));
                break;
            }
    }
}
