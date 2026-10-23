using System.Collections.Generic;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// A hanging platform (see <see cref="Hanger"/>): solid while it hangs and once it has landed, but not while it drops,
/// so it falls through whoever is under it (crushing them) instead of landing on their heads.
/// </summary>
public partial class HangBody : AnimatableBody2D
{
    public int Index;
    public Hanger Spec;
    /// <summary>Where each chain is bolted on (its y), and which chains are cut (a bit each).</summary>
    public int[] Anchors;
    public int CutMask;
    /// <summary>Host: each chain's hit points.</summary>
    public float[] ChainHp;
    /// <summary>Battle clock when its last chain was cut, or -1 while it hangs. Its drop follows the clock from then.</summary>
    public float FellAt = -1;
    /// <summary>The y its top lands at, or null when it falls out through a pit.</summary>
    public int? LandY;
    /// <summary>Who cut its last chain (credited for whoever it crushes), or -1.</summary>
    public int CutBy = -1;
    /// <summary>Its top now, and whether it is on its way down, has landed, or has fallen out of the arena.</summary>
    public float Y;
    public bool Dropping, Landed, Gone;
    /// <summary>Host: slots already crushed on this drop.</summary>
    public readonly HashSet<int> Crushed = new();

    public int[] ChainXs => Spec.ChainXs;
    public bool ChainCut(int c) => (CutMask & (1 << c)) != 0;
    public Rect2 Bounds => new(Spec.X, Y, Spec.W, Spec.H);
    public RectI Rect => new(Spec.X, Mathf.RoundToInt(Y), Spec.W, Spec.H);
}

// Hanging platforms: chains to shoot, the drop, and what it lands on.
public partial class Arena
{
    public readonly List<HangBody> Hangers = new();

    private void BuildHangers(MapLayout map)
    {
        for (int i = 0; i < map.Hangers.Count; i++)
        {
            var s = map.Hangers[i];
            var h = new HangBody
            {
                Index = i, Spec = s, CollisionLayer = 1, CollisionMask = 0, Y = s.Y,
                Anchors = HangerMath.Anchors(map, s), LandY = HangerMath.LandY(map, s),
                Position = new Vector2(s.X + s.W * 0.5f, s.Y + s.H * 0.5f),
            };
            h.ChainHp = new float[h.ChainXs.Length];
            for (int c = 0; c < h.ChainHp.Length; c++) h.ChainHp[c] = Hanger.ChainHp;
            h.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(s.W, s.H) } });
            AddChild(h);
            Hangers.Add(h);
        }
    }

    /// <summary>Every peer: where each hanger is at this clock. <paramref name="live"/> is false in kill cam replays (no landing effects).</summary>
    private void AdvanceHangers(bool live)
    {
        foreach (var h in Hangers)
        {
            bool wasLanded = h.Landed;
            float y = h.Spec.Y;
            h.Dropping = h.Landed = h.Gone = false;
            if (h.FellAt >= 0 && Clock > h.FellAt)
            {
                y += Hanger.Drop(Clock - h.FellAt);
                if (h.LandY is { } ly && y >= ly) { y = ly; h.Landed = true; }
                else if (h.LandY == null && y > MapLayout.KillY) h.Gone = true;
                else h.Dropping = true;
            }
            h.Y = y;
            h.Position = new Vector2(h.Spec.X + h.Spec.W * 0.5f, y + h.Spec.H * 0.5f);
            h.CollisionLayer = h.Dropping || h.Gone ? 0u : 1u;
            if (live && h.Landed && !wasLanded) HangerLanded(h);
        }
    }

    private void HangerLanded(HangBody h)
    {
        var r = h.Bounds;
        var dust = new Color(0.55f, 0.5f, 0.45f, 0.55f);
        foreach (float fx in new[] { 0f, 1f })
            Game.Fx.Emit(new Vector2(r.Position.X + r.Size.X * fx, r.End.Y), PKind.Smoke, dust, 4, 60, 0.6f, 3f, -20);
        Game.Fx.Emit(new Vector2(r.GetCenter().X, r.End.Y), PKind.Chunk, Game.MapNode.Theme.Block, 6, 120, 0.7f, 2f, 520, Vector2.Up, 1.6f);
        Game.Sfx.Play("crate");
        Game.Shake(0.25f);
    }

    /// <summary>Solids for bullets: each hanger where it is now, and the chains it still hangs on.</summary>
    private void AddHangerSolids(System.Action<RectI, int> add)
    {
        foreach (var h in Hangers)
        {
            if (h.Gone) continue;
            add(h.Rect, -1);
            if (h.FellAt >= 0 && Clock > h.FellAt) continue;
            var xs = h.ChainXs;
            for (int c = 0; c < xs.Length; c++)
                if (!h.ChainCut(c)) add(new RectI(xs[c] - 2, h.Anchors[c], 4, Mathf.RoundToInt(h.Y) - h.Anchors[c]), h.Index * 4 + c);
        }
    }

    /// <summary>Host: a bullet or blast hurt chain <paramref name="c"/>; when the last one goes, the platform drops.</summary>
    private void HostDamageChain(HangBody h, int c, float damage, int by)
    {
        if (h.FellAt >= 0 || h.ChainCut(c)) return;
        h.ChainHp[c] -= damage;
        if (h.ChainHp[c] > 0) return;
        int mask = h.CutMask | 1 << c;
        bool all = mask == (1 << h.ChainXs.Length) - 1;
        Game.HostHangerCut(h.Index, mask, all ? Clock : -1, by);
    }

    /// <summary>Every peer: chains <paramref name="mask"/> are cut; <paramref name="fellAt"/> ≥ 0 when that was the last.</summary>
    public void CutChains(int index, int mask, float fellAt, int by)
    {
        if (index < 0 || index >= Hangers.Count) return;
        var h = Hangers[index];
        var xs = h.ChainXs;
        for (int c = 0; c < xs.Length; c++)
        {
            if ((mask & (1 << c)) == 0 || h.ChainCut(c)) continue;
            var at = new Vector2(xs[c], (h.Anchors[c] + h.Y) * 0.5f);
            Game.Fx.Emit(at, PKind.Streak, new Color("#ffe6a8"), 8, 180, 0.25f, 1f, 300);
            Game.Fx.Emit(at, PKind.Chunk, new Color("#8a8f99"), 3, 90, 0.6f, 1.6f, 520);
        }
        Game.Sfx.Play("ping");
        h.CutMask = mask;
        if (fellAt >= 0)
        {
            h.FellAt = fellAt;
            h.CutBy = by;
            h.Crushed.Clear();
            HangersDropped++;
        }
        RebuildSolids();
    }

    /// <summary>Host: blasts hurt the chains in reach.</summary>
    private void HostBlastChains(Vector2 at, float radius, float damage, int owner)
    {
        foreach (var h in Hangers)
        {
            var xs = h.ChainXs;
            for (int c = 0; c < xs.Length; c++)
            {
                if (h.ChainCut(c)) continue;
                float y = Mathf.Clamp(at.Y, h.Anchors[c], h.Y);
                float d = new Vector2(xs[c], y).DistanceTo(at);
                if (d <= radius) HostDamageChain(h, c, damage * (1f - 0.5f * d / radius), owner);
            }
        }
    }

    /// <summary>Host: a dropping platform smashes props and crushes whoever is under it, once each, shoving them out.</summary>
    private void HostCrush(bool fighting)
    {
        foreach (var h in Hangers)
        {
            if (!h.Dropping) continue;
            var r = h.Bounds;
            foreach (var p in Props)
                if (!p.Broken && p.Bounds.Intersects(r)) HostDamageProp(p, 500, h.CutBy);
            if (!fighting) continue;
            foreach (var a in Game.LiveAvatars())
            {
                int slot = a.Player.Slot;
                if (h.Crushed.Contains(slot) || !a.Bounds.Intersects(r) || a.GlobalPosition.Y < r.Position.Y) continue;
                h.Crushed.Add(slot);
                float side = a.GlobalPosition.X < r.GetCenter().X ? -1 : 1;
                if (h.CutBy >= 0 && h.CutBy != slot) Game.CreditHit(a, h.CutBy);
                Game.HostHazardDamage(a, Hanger.CrushDamage, new Vector2(side * Hanger.CrushShove, 120), a.GlobalPosition, GameManager.HazardHit.Crush);
            }
        }
    }

    /// <summary>Kill cam: which chains each hanger still had.</summary>
    public void CaptureHangers(List<int> into)
    {
        into.Clear();
        foreach (var h in Hangers) into.Add(h.CutMask);
    }

    public void ShowHangers(List<int> masks)
    {
        for (int i = 0; i < Hangers.Count && i < masks.Count; i++) Hangers[i].CutMask = masks[i];
    }

    private void DrawHangers()
    {
        var block = Game.MapNode.Theme.Block;
        var metal = new Color("#8a8f99");
        foreach (var h in Hangers)
        {
            if (h.Gone) continue;
            var r = h.Bounds;
            var xs = h.ChainXs;
            for (int c = 0; c < xs.Length; c++)
            {
                var top = new Vector2(xs[c], h.Anchors[c]);
                var bottom = new Vector2(xs[c], r.Position.Y);
                // A cut chain leaves a stub at the bracket and one on the platform.
                if (h.ChainCut(c)) DrawChain(top, top + new Vector2(0, 7), metal);
                if (h.ChainCut(c)) DrawChain(bottom - new Vector2(0, 5), bottom, metal);
                else DrawChain(top, bottom, metal);
                DrawColoredPolygon(Shapes.RoundRect(new Rect2(top.X - 4, top.Y - 1, 8, 3), 1), metal.Darkened(0.45f));
            }
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(r.Position + new Vector2(2, 4), r.Size), 2), new Color(0, 0, 0, 0.28f));
            var poly = Shapes.RoundRect(r, 2);
            Shapes.Gradient(this, poly, block.Lightened(0.4f), block.Darkened(0.1f));
            Shapes.DrawClosed(this, poly, block.Darkened(0.65f), 1f);
            DrawLine(r.Position + new Vector2(2, 0.8f), new Vector2(r.End.X - 2, r.Position.Y + 0.8f), Game.MapNode.Theme.Edge with { A = 0.9f }, 1.4f);
            foreach (int x in xs) DrawCircle(new Vector2(x, r.Position.Y + r.Size.Y * 0.5f), 1.4f, metal.Lightened(0.2f));
        }
    }

    private void DrawChain(Vector2 a, Vector2 b, Color metal)
    {
        if (b.Y - a.Y < 1) return;
        DrawLine(a, b, metal.Darkened(0.55f), 3f);
        DrawDashedLine(a, b, metal, 1.6f, 3f);
    }
}
