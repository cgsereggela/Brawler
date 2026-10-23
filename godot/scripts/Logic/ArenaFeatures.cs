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
/// A floating platform that players can shove around: landing on it, walking into its side, shooting it or blasting
/// it pushes it, and a spring eases it back to its spot (top-left X, Y). It never strays further than
/// <see cref="ReachX"/> sideways or <see cref="ReachY"/> up and down. Its top is a surface of the arena.
/// </summary>
public readonly record struct Drift(int X, int Y, int W, int H)
{
    public const int ReachX = 32, ReachY = 16;
    public RectI Rect => new(X, Y, W, H);
    public Surface Top => new(X, X + W, Y);
    /// <summary>Everything it can cover, pushed as far as it goes.</summary>
    public RectI Range => Rect.Grow(ReachX, ReachY);
}

/// <summary>
/// A platform hanging on chains from whatever is above it (the ceiling, or a block). Shooting or blasting a chain
/// cuts it; once every chain is cut the platform drops, crushing whoever is under it, and lands on the ground below
/// (or falls out through a pit). Top-left X, Y and size W, H.
/// </summary>
public readonly record struct Hanger(int X, int Y, int W, int H)
{
    /// <summary>Hit points of each chain: two ordinary shots, or one big one.</summary>
    public const float ChainHp = 40;
    /// <summary>Fall acceleration (px/s²), a little more than a player's, so it comes down hard.</summary>
    public const float Gravity = 1100;
    public const float CrushDamage = 45, CrushShove = 260;
    public RectI Rect => new(X, Y, W, H);
    /// <summary>Where each chain meets the platform: one in the middle of a short one, two near the ends of a long one.</summary>
    public int[] ChainXs => W < 56 ? new[] { X + W / 2 } : new[] { X + 8, X + W - 8 };
    /// <summary>How far it has dropped <paramref name="t"/> seconds after its last chain was cut.</summary>
    public static float Drop(float t) => t <= 0 ? 0 : 0.5f * Gravity * t * t;
}

/// <summary>Where a <see cref="Hanger"/>'s chains are anchored and where it lands, from the arena's static parts.</summary>
public static class HangerMath
{
    /// <summary>How far what a chain hangs from must reach past it on each side, so it hides the chain's bracket (8 wide) and its own rounded corner.</summary>
    public const int AnchorMargin = 8;

    /// <summary>True if every chain hangs from something that reaches <see cref="AnchorMargin"/> past it on both sides.</summary>
    public static bool AnchorsHidden(MapLayout map, Hanger h)
    {
        var xs = h.ChainXs;
        var anchors = Anchors(map, h);
        for (int i = 0; i < xs.Length; i++)
            if (!map.Solids.Exists(r => r.Bottom == anchors[i] && r.X <= xs[i] - AnchorMargin && r.Right >= xs[i] + AnchorMargin)) return false;
        return true;
    }

    /// <summary>
    /// For each chain, the y of the bottom of the lowest solid above it (the ceiling at worst): where the chain is
    /// bolted on.
    /// </summary>
    public static int[] Anchors(MapLayout map, Hanger h)
    {
        var xs = h.ChainXs;
        var tops = new int[xs.Length];
        for (int i = 0; i < xs.Length; i++)
        {
            int best = 0;
            foreach (var r in map.Solids)
                if (r.X <= xs[i] && r.Right > xs[i] && r.Bottom <= h.Y) best = Math.Max(best, r.Bottom);
            tops[i] = best;
        }
        return tops;
    }

    /// <summary>
    /// The y its top comes to rest at after it drops: on the highest solid or slope under it, or null if there's
    /// nothing under it at all and it falls out of the arena.
    /// </summary>
    public static int? LandY(MapLayout map, Hanger h)
    {
        int? ground = null;
        void Take(int y) { if (y >= h.Y + h.H && (ground == null || y < ground)) ground = y; }
        foreach (var r in map.Solids)
            if (r.X < h.X + h.W && r.Right > h.X) Take(r.Y);
        foreach (var s in map.Slopes)
        {
            int a = Math.Max(s.X1, h.X), b = Math.Min(s.X2, h.X + h.W);
            if (a >= b) continue;
            Take(Math.Min(SlopeY(s, a), SlopeY(s, b)));
        }
        return ground - h.H;
    }

    /// <summary>True if it would land flat on one solid's top (not on a slope or across a step).</summary>
    public static bool LandsFlat(MapLayout map, Hanger h)
    {
        if (LandY(map, h) is not { } y) return true;
        foreach (var s in map.Slopes)
            if (s.X1 < h.X + h.W && s.X2 > h.X && s.Top <= y + h.H + 2) return false;
        foreach (var r in map.Solids)
            if (r.Y == y + h.H && r.X <= h.X && r.Right >= h.X + h.W) return true;
        return false;
    }

    private static int SlopeY(Slope s, int x) => s.X2 == s.X1 ? s.Top : s.Y1 + (s.Y2 - s.Y1) * (x - s.X1) / (s.X2 - s.X1);
}

/// <summary>
/// How a <see cref="Drift"/> moves: a damped spring back to its spot, shoves from players, bullets and blasts, and
/// limits on how far it goes. Pure, so every peer (and the tests) agree on it; the host runs it and sends offsets.
/// </summary>
public static class DriftMotion
{
    /// <summary>Spring stiffness (1/s²) and damping (1/s): a shove settles in about two seconds, with a little bob.</summary>
    public const float Spring = 14f, Damping = 3.2f;
    /// <summary>How hard someone walking into a side pushes it along (px/s²): about 27 px out against the spring.</summary>
    public const float Push = 380f;
    /// <summary>Share of a body's downward speed handed on when it lands or stands on one (its weight, in effect).</summary>
    public const float Weight = 0.15f;

    /// <summary>
    /// One tick: <paramref name="off"/> and <paramref name="vel"/> are the platform's offset from its spot and its
    /// velocity; <paramref name="accel"/> is everything shoving it this tick. Returns the new offset, kept within its
    /// reach and inside the walls (stopping it where it hits a limit).
    /// </summary>
    public static Vector2 Step(Drift d, Vector2 off, ref Vector2 vel, Vector2 accel, float dt)
    {
        vel += (accel - off * Spring - vel * Damping) * dt;
        var next = off + vel * dt;
        float minX = Math.Max(-Drift.ReachX, MapGenerator.Wall - d.X), maxX = Math.Min(Drift.ReachX, MapLayout.Width - MapGenerator.Wall - d.X - d.W);
        float minY = Math.Max(-Drift.ReachY, MapGenerator.Wall - d.Y), maxY = Drift.ReachY;
        if (next.X < minX || next.X > maxX) { next.X = Math.Clamp(next.X, minX, maxX); vel.X = 0; }
        if (next.Y < minY || next.Y > maxY) { next.Y = Math.Clamp(next.Y, minY, maxY); vel.Y = 0; }
        return next;
    }
}

/// <summary>
/// Seeded extras added to a validated arena: crates and explosive barrels to push around and shoot apart, glass
/// bridges over pits, moving platforms, drifting platforms, magma and poison pools, and sawblades. None of them is
/// part of the jump graph the arena is checked with (it's fully playable without them), and each stays clear of
/// spawns, jump pads and the others. Which
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
        // A Drift arena is all moving parts already: crates or pools on platforms that get shoved about would only slide off.
        if (map.Style == MapStyle.Drift) return;
        var rules = ThemeRules.Rules(theme);
        var rng = new Rng(((ulong)(uint)map.Seed << 1) ^ 0x51F15EEDUL);
        // Spots extras stay out of: around spawns, the air above jump pads, and each other.
        var keep = new List<RectI>();
        foreach (var sp in map.Spawns) keep.Add(new RectI(sp.X - 26, sp.Y - 40, 52, 46));
        foreach (var p in map.Pads) keep.Add(new RectI(p.X1 - 10, p.Y - MapGenerator.PadAir, p.X2 - p.X1 + 20, MapGenerator.PadAir + 6));

        Pits(map, rng, keep);
        OpenAir(map, rng, keep);
        Pools(map, rng, keep, rules);
        Saws(map, rng, keep, rules);
        Crates(map, rng, keep, rules);
        Hangers(map, rng, keep);
        Drifts(map, rng, keep);
    }

    private static bool Clear(MapLayout map, List<RectI> keep, RectI r)
    {
        if (r.X < MapGenerator.Wall || r.Right > MapLayout.Width - MapGenerator.Wall || r.Y < MapGenerator.Wall) return false;
        foreach (var o in map.Solids)
            if (o.Intersects(r)) return false;
        // A little extra room around slopes, so nothing pushable starts at the foot or crest of one.
        foreach (var s in map.Slopes)
            if (s.Intersects(r.Grow(14, 2))) return false;
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
                int lx1 = Math.Max(MapGenerator.Wall + 4, x1 - 50), lx2 = Math.Min(MapLayout.Width - MapGenerator.Wall - 4 - MoverW, x2 + 50 - MoverW);
                foreach (int lift in new[] { 70, 88, 106 })
                    if (TryMover(map, rng, keep, lx1, lx2, MapGenerator.FloorY - lift)) break;
            }
        }
    }

    /// <summary>A mover on [lx1, lx2] at height y, if its whole lane (plus MinClearance to ride it and pass under) is free.</summary>
    private static bool TryMover(MapLayout map, Rng rng, List<RectI> keep, int lx1, int lx2, int y)
    {
        if (lx2 - lx1 < 48) return false;
        int c = MapGenerator.MinClearance;
        var lane = new RectI(lx1 - 2, y - c, lx2 - lx1 + MoverW + 4, MoverH + c * 2);
        if (!Clear(map, keep, lane)) return false;
        float period = MathF.Max(3f, (lx2 - lx1) * 2 / 56f);
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
                // Halfway between two tiers, so it only needs those two clear over its lane.
                int y = MapGenerator.TierY(rng.Range(1, 7)) - MapGenerator.TierSpacing / 2;
                int travel = rng.Range(12, 25) * 8;
                int x = rng.Range(3, (MapLayout.Width - travel - MoverW) / 8 - 3) * 8;
                if (TryMover(map, rng, keep, x, x + travel, y)) break;
            }

        bool hasGlass = map.Props.Exists(p => p.Kind == PropKind.Glass);
        if (!hasGlass && rng.Chance(0.35f))
            for (int i = 0; i < 30; i++)
            {
                int tiles = rng.Range(4, 7), w = tiles * GlassTile;
                int y = MapGenerator.TierY(rng.Range(1, 6)) - MapGenerator.TierSpacing / 2;
                int x = rng.Range(3, (MapLayout.Width - w) / 8 - 3) * 8;
                int c = MapGenerator.MinClearance;
                var room = new RectI(x - 16, y - c, w + 32, GlassTile + c * 2);
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
            if (x2 - x1 > 140)
            {
                float len = rng.Range(75f, 140f);
                x1 = rng.Range(x1, x2 - len);
                x2 = x1 + len;
            }
            float y = s.Y - 2; // mostly above the surface, its bottom teeth sunk into a slot
            int zx1 = (int)MathF.Floor(x1 - r - 6), zx2 = (int)MathF.Ceiling(x2 + r + 6); // round outward, never shrink
            var zone = new RectI(zx1, (int)(y - r - 30), zx2 - zx1, (int)(r + 34));
            if (!ClearOfKeep(keep, zone)) continue;
            float period = MathF.Max(2.2f, (x2 - x1) * 2 / 55f);
            map.Saws.Add(new SawTrack(x1, y, x2, y, r, period, rng.Range(0f, 1f)));
            keep.Add(zone);
        }
    }

    // ---------- drifting platforms ----------

    /// <summary>
    /// Sometimes one or two drifting platforms in open air, halfway between two tiers, with room to be shoved their
    /// full reach and still leave clearance to stand on them and pass under. Added last: their tops become surfaces
    /// (so bots can use them), but nothing else is put on them.
    /// </summary>
    private static void Drifts(MapLayout map, Rng rng, List<RectI> keep)
    {
        int want = rng.Chance(0.35f) ? rng.Range(1, 2) : 0;
        int c = MapGenerator.MinClearance;
        for (int i = 0; i < 40 && want > 0; i++)
        {
            int w = rng.Range(5, 9) * 8;
            int y = (MapGenerator.TierY(rng.Range(1, 7)) - MapGenerator.TierSpacing / 2) / 8 * 8; // on the grid, as map codes store it
            int x = rng.Range(4, (MapLayout.Width - w) / 8 - 4) * 8;
            var d = new Drift(x, y, w, 8);
            var room = d.Range.Grow(16, c);
            if (!Clear(map, keep, room)) continue;
            AddDrift(map, d);
            keep.Add(room);
            want--;
        }
    }

    /// <summary>Adds a drifting platform and its top as a surface.</summary>
    public static void AddDrift(MapLayout map, Drift d)
    {
        map.Drifts.Add(d);
        if (d.W >= 16) map.Surfaces.Add(d.Top);
    }

    // ---------- hanging platforms ----------

    /// <summary>
    /// Sometimes a platform or two hanging on chains in open air, with clear air for its chains, room to stand on it
    /// and under it, and a flat spot below to land on that isn't on top of anything else (or a pit to fall through).
    /// </summary>
    private static void Hangers(MapLayout map, Rng rng, List<RectI> keep)
    {
        int want = rng.Chance(0.45f) ? rng.Range(1, 2) : 0;
        int c = MapGenerator.MinClearance;
        for (int i = 0; i < 80 && want > 0; i++)
        {
            int w = rng.Range(6, 12) * 8;
            int y = MapGenerator.TierY(rng.Range(2, 6)) / 8 * 8;
            int x = rng.Range(4, (MapLayout.Width - w) / 8 - 4) * 8;
            var h = new Hanger(x, y, w, 8);
            var room = new RectI(x - 16, y - c, w + 32, 2 * c + 8);
            if (!Clear(map, keep, room)) continue;
            var anchors = HangerMath.Anchors(map, h);
            var xs = h.ChainXs;
            var chains = new List<RectI>();
            for (int k = 0; k < xs.Length; k++) chains.Add(new RectI(xs[k] - 3, anchors[k], 6, y - anchors[k]));
            if (chains.Exists(ch => ch.H < 24 || !Clear(map, keep, ch))) continue;
            if (!HangerMath.AnchorsHidden(map, h)) continue; // a bracket half off the end of a block shows
            if (!HangerMath.LandsFlat(map, h)) continue;
            var landed = HangerMath.LandY(map, h) is { } ly ? new RectI(x, ly, w, h.H) : (RectI?)null;
            if (landed is { } l && !Clear(map, keep, l.Grow(4, 0))) continue;
            map.Hangers.Add(h);
            keep.Add(room);
            foreach (var ch in chains) keep.Add(ch.Grow(4, 0));
            if (landed is { } lk) keep.Add(lk.Grow(4, 4));
            want--;
        }
    }

    // ---------- crates and barrels ----------

    private static void Crates(MapLayout map, Rng rng, List<RectI> keep, FeatureRules rules)
    {
        int crates = rng.Range(1, 5), barrels = rng.Chance(rules.BarrelChance) ? rng.Range(1, 2) : 0;
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
