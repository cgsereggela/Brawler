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
    /// <summary>The y its top lands at, or null when it falls out through a pit (from where it hangs, until it drops).</summary>
    public int? LandY;
    /// <summary>
    /// Battle clock when one of its two chains was cut and it began to swing on the other, or -1. The swing, and the
    /// fall once the last chain goes, are worked out then and follow the clock from there, like the drop does.
    /// </summary>
    public float SwungAt = -1;
    public HangerSwing Swing;
    public HangerFall Fall;
    /// <summary>Where it is now: its centre and turn (radians, 0 = level).</summary>
    public Vector2 Center;
    public float Rot;
    /// <summary>Who cut its last chain (credited for whoever it crushes), or -1.</summary>
    public int CutBy = -1;
    /// <summary>Its top now, and whether it is on its way down, has landed, or has fallen out of the arena.</summary>
    public float Y;
    public bool Dropping, Landed, Gone;
    /// <summary>Host: slots already crushed on this drop.</summary>
    public readonly HashSet<int> Crushed = new();

    public int[] ChainXs => Spec.ChainXs;
    public bool ChainCut(int c) => (CutMask & (1 << c)) != 0;
    /// <summary>The box around it as it is now (when turned, the box around the turned platform).</summary>
    public Rect2 Bounds
    {
        get
        {
            float c = Mathf.Abs(Mathf.Cos(Rot)), s = Mathf.Abs(Mathf.Sin(Rot));
            var half = new Vector2(Spec.W * c + Spec.H * s, Spec.W * s + Spec.H * c) * 0.5f;
            return new Rect2(Center - half, half * 2);
        }
    }
    public bool Level => Mathf.Abs(Mathf.Sin(Rot)) < 0.02f;

    /// <summary>A point given in the level platform's frame (x across, y down, from its centre), where it is now.</summary>
    public Vector2 ToWorld(Vector2 local) => Center + local.Rotated(Rot);

    /// <summary>Chain <paramref name="c"/>: its bolt, and where it meets the platform now (it swings with it).</summary>
    public (Vector2 Top, Vector2 Bottom) ChainEnds(int c) =>
        (new Vector2(ChainXs[c], Anchors[c]), ToWorld(new Vector2(ChainXs[c] - (Spec.X + Spec.W * 0.5f), -Spec.H * 0.5f)));
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
                Center = new Vector2(s.X + s.W * 0.5f, s.Y + s.H * 0.5f),
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
            var s = h.Spec;
            var center = new System.Numerics.Vector2(s.X + s.W * 0.5f, s.Y + s.H * 0.5f);
            float rot = 0;
            h.Dropping = h.Landed = h.Gone = false;
            if (h.FellAt >= 0 && Clock > h.FellAt && h.Fall != null)
            {
                h.Landed = h.Fall.At(Clock - h.FellAt, out center, out rot);
                if (!h.Landed && center.Y - s.H * 0.5f > MapLayout.KillY) h.Gone = true;
                else if (!h.Landed) h.Dropping = true;
            }
            else if (h.SwungAt >= 0 && Clock > h.SwungAt && h.Swing != null)
                h.Swing.At(Clock - h.SwungAt, out center, out rot, out _, out _);
            h.Center = new Vector2(center.X, center.Y);
            h.Rot = rot;
            h.Y = h.Bounds.Position.Y;
            h.Position = h.Center;
            h.Rotation = rot;
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
            var sp = h.Spec;
            if (h.Level) add(new RectI(Mathf.RoundToInt(h.Center.X - sp.W * 0.5f), Mathf.RoundToInt(h.Center.Y - sp.H * 0.5f), sp.W, sp.H), -1);
            else
            {
                // Turned: a row of small squares along it, so bullets meet the platform and not the air around it.
                int size = sp.H + 2, n = Mathf.CeilToInt((float)sp.W / sp.H);
                for (int i = 0; i < n; i++)
                {
                    var at = h.ToWorld(new Vector2(sp.W * ((i + 0.5f) / n - 0.5f), 0));
                    add(new RectI(Mathf.RoundToInt(at.X - size * 0.5f), Mathf.RoundToInt(at.Y - size * 0.5f), size, size), -1);
                }
            }
            if (h.FellAt >= 0 && Clock > h.FellAt) continue;
            for (int c = 0; c < h.ChainXs.Length; c++)
            {
                if (h.ChainCut(c)) continue;
                var (top, bottom) = h.ChainEnds(c);
                if (Mathf.Abs(bottom.X - top.X) < 1)
                {
                    add(new RectI(Mathf.RoundToInt(top.X) - 2, Mathf.RoundToInt(top.Y), 4, Mathf.RoundToInt(bottom.Y - top.Y)), h.Index * 4 + c);
                    continue;
                }
                // Swinging at a slant: small squares along it.
                int n = Mathf.Max(1, Mathf.CeilToInt(top.DistanceTo(bottom) / 5));
                for (int i = 0; i <= n; i++)
                {
                    var at = top.Lerp(bottom, (float)i / n);
                    add(new RectI(Mathf.RoundToInt(at.X) - 3, Mathf.RoundToInt(at.Y) - 3, 6, 6), h.Index * 4 + c);
                }
            }
        }
    }

    /// <summary>Host: a bullet or blast hurt chain <paramref name="c"/>; when the last one goes, the platform drops.</summary>
    private void HostDamageChain(HangBody h, int c, float damage, int by)
    {
        if (h.FellAt >= 0 || h.ChainCut(c)) return;
        h.ChainHp[c] -= damage;
        if (h.ChainHp[c] > 0) return;
        Game.HostHangerCut(h.Index, h.CutMask | 1 << c, Clock, by);
    }

    /// <summary>
    /// Every peer: chains <paramref name="mask"/> are cut, at battle clock <paramref name="at"/>. With one of two left,
    /// the platform swings on it; with none, it falls, keeping the swing's speed and spin if it was swinging.
    /// </summary>
    public void CutChains(int index, int mask, float at, int by)
    {
        if (index < 0 || index >= Hangers.Count) return;
        var h = Hangers[index];
        var xs = h.ChainXs;
        for (int c = 0; c < xs.Length; c++)
        {
            if ((mask & (1 << c)) == 0 || h.ChainCut(c)) continue;
            var (top, bottom) = h.ChainEnds(c);
            var mid = (top + bottom) * 0.5f;
            Game.Fx.Emit(mid, PKind.Streak, new Color("#ffe6a8"), 8, 180, 0.25f, 1f, 300);
            Game.Fx.Emit(mid, PKind.Chunk, new Color("#8a8f99"), 3, 90, 0.6f, 1.6f, 520);
        }
        Game.Sfx.Play("ping");
        h.CutMask = mask;
        int left = 0, keep = -1;
        for (int c = 0; c < xs.Length; c++)
            if (!h.ChainCut(c)) { left++; keep = c; }
        if (left == 1 && xs.Length == 2 && h.SwungAt < 0 && h.FellAt < 0)
        {
            h.SwungAt = at;
            h.Swing = new HangerSwing(h.Spec, xs[keep], h.Anchors[keep]);
        }
        if (left == 0 && h.FellAt < 0)
        {
            var s = h.Spec;
            var from = new System.Numerics.Vector2(s.X + s.W * 0.5f, s.Y + s.H * 0.5f);
            System.Numerics.Vector2 vel = default;
            float rot = 0, spin = 0;
            if (h.Swing != null && at > h.SwungAt) h.Swing.At(at - h.SwungAt, out from, out rot, out vel, out spin);
            h.Fall = new HangerFall(_map, s, from, vel, rot, spin);
            h.LandY = h.Fall.LandTop;
            h.FellAt = at;
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
                var (top, bottom) = h.ChainEnds(c);
                float d = Geometry2D.GetClosestPointToSegment(at, top, bottom).DistanceTo(at);
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
            var xs = h.ChainXs;
            for (int c = 0; c < xs.Length; c++)
            {
                var (top, bottom) = h.ChainEnds(c);
                // A cut chain leaves a stub at the bracket and one hanging off the platform (which turns with it).
                if (h.ChainCut(c))
                {
                    DrawChain(top, top + new Vector2(0, 7), metal);
                    DrawChain(bottom - new Vector2(0, 5).Rotated(h.Rot), bottom, metal);
                }
                else DrawChain(top, bottom, metal);
                DrawColoredPolygon(Shapes.RoundRect(new Rect2(top.X - 4, top.Y - 1, 8, 3), 1), metal.Darkened(0.45f));
            }
            // The platform in its own frame, turned to where it is.
            var s = h.Spec;
            var r = new Rect2(-s.W * 0.5f, -s.H * 0.5f, s.W, s.H);
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(h.Center + new Vector2(2, 4) - h.Bounds.Size * 0.5f, h.Bounds.Size), 2), new Color(0, 0, 0, 0.28f));
            DrawSetTransform(h.Center, h.Rot);
            var poly = Shapes.RoundRect(r, 2);
            Shapes.Gradient(this, poly, block.Lightened(0.4f), block.Darkened(0.1f));
            Shapes.DrawClosed(this, poly, block.Darkened(0.65f), 1f);
            DrawLine(r.Position + new Vector2(2, 0.8f), new Vector2(r.End.X - 2, r.Position.Y + 0.8f), Game.MapNode.Theme.Edge with { A = 0.9f }, 1.4f);
            foreach (int x in xs) DrawCircle(new Vector2(x - (s.X + s.W * 0.5f), 0), 1.4f, metal.Lightened(0.2f));
            DrawSetTransform(Vector2.Zero);
        }
    }

    private void DrawChain(Vector2 a, Vector2 b, Color metal)
    {
        if (a.DistanceTo(b) < 1) return;
        DrawLine(a, b, metal.Darkened(0.55f), 3f);
        DrawDashedLine(a, b, metal, 1.6f, 3f);
    }
}
