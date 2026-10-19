using System;
using System.Collections.Generic;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Builds collision bodies for a generated layout and draws it: glowing-edged platforms, themed decorations
/// and jump pads, over a <see cref="Backdrop"/>. All decoration is seeded, so every peer sees the same arena.
/// </summary>
public partial class MapNode : Node2D
{
    public MapLayout Layout { get; private set; }
    public ArenaTheme Theme { get; private set; } = ArenaTheme.All[0];
    public Backdrop Backdrop { get; private set; }

    /// <summary>Raised on every peer when someone is launched by a jump pad.</summary>
    public event Action<Vector2> PadFired;
    public Func<IEnumerable<Avatar>> Avatars;

    private float _t;
    private float[] _padFlash = Array.Empty<float>();
    private Node2D _bodies;
    private DrawLayer _solids, _glow;
    private readonly List<(Vector2 P, float Size, int Kind, float Phase, bool Below)> _decos = new();

    public override void _Ready()
    {
        Backdrop = new Backdrop { Name = "Backdrop" };
        AddChild(Backdrop);
        _bodies = new Node2D { Name = "Bodies" };
        AddChild(_bodies);
        _solids = new DrawLayer(DrawSolids);
        _glow = new DrawLayer(DrawGlow, additive: true);
        AddChild(_solids);
        AddChild(_glow);
    }

    public void Build(MapLayout layout)
    {
        foreach (var c in _bodies.GetChildren()) c.QueueFree();
        Layout = layout;
        _decos.Clear();
        if (layout != null)
        {
            Theme = ArenaTheme.ForSeed(layout.Seed);
            Backdrop.Set(Theme, layout.Seed);
            foreach (var r in layout.Solids)
            {
                var body = new StaticBody2D { CollisionLayer = 1, CollisionMask = 0, Position = new Vector2(r.X + r.W / 2f, r.Y + r.H / 2f) };
                body.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(r.W, r.H) } });
                _bodies.AddChild(body);
            }
            _padFlash = new float[layout.Pads.Count];
            BuildDecos();
        }
        _solids.QueueRedraw();
        _glow.QueueRedraw();
    }

    private static bool IsBorder(RectI r) => r.X <= 0 || r.Y <= 0 || r.Right >= MapLayout.Width;

    private void BuildDecos()
    {
        var rng = new Rng((ulong)(uint)Layout.Seed ^ 0x9E3779B9UL);
        foreach (var s in Layout.Surfaces)
        {
            int n = s.Width / 22;
            for (int i = 0; i < n; i++)
            {
                if (!rng.Chance(0.55f)) continue;
                float x = rng.Range(s.X1 + 4f, s.X2 - 4f);
                bool onPad = false;
                foreach (var p in Layout.Pads)
                    if (p.Y == s.Y && x > p.X1 - 6 && x < p.X2 + 6) onPad = true;
                if (!onPad) _decos.Add((new Vector2(x, s.Y), rng.Range(0.7f, 1.3f), rng.Range(0, 2), rng.Range(0f, Mathf.Tau), false));
            }
        }
        // Undersides: icicles, string lights and such hang from floating platforms.
        foreach (var r in Layout.Solids)
        {
            if (IsBorder(r) || r.Y >= MapGenerator.FloorY) continue;
            for (float x = r.X + 5; x < r.Right - 4; x += rng.Range(7f, 13f))
                _decos.Add((new Vector2(x, r.Bottom), rng.Range(0.6f, 1.3f), rng.Range(0, 2), rng.Range(0f, Mathf.Tau), true));
        }
    }

    public override void _Process(double delta)
    {
        if (Layout == null || !IsVisibleInTree()) return;
        float dt = (float)delta;
        _t += dt;
        for (int i = 0; i < _padFlash.Length; i++)
        {
            _padFlash[i] = Mathf.Max(0, _padFlash[i] - dt * 3);
            if (_padFlash[i] > 0.3f || Avatars == null) continue;
            var pad = Layout.Pads[i];
            foreach (var a in Avatars())
            {
                var feet = a.GlobalPosition + new Vector2(0, a.HalfSize.Y);
                if (a.Velocity.Y < -300 && feet.X > pad.X1 - 4 && feet.X < pad.X2 + 4 && feet.Y > pad.Y - 30 && feet.Y < pad.Y + 2)
                {
                    _padFlash[i] = 1;
                    PadFired?.Invoke(new Vector2(pad.CenterX, pad.Y));
                }
            }
        }
        _solids.QueueRedraw();
        _glow.QueueRedraw();
    }

    // ---------- drawing ----------

    private void DrawSolids(DrawLayer l)
    {
        if (Layout == null) return;
        var t = Theme;
        var top = t.Block.Lightened(0.22f);
        var bottom = t.Block.Darkened(0.3f);

        foreach (var r in Layout.Solids)
        {
            var rect = new Rect2(r.X, r.Y, r.W, r.H);
            if (IsBorder(r))
            {
                l.DrawRect(rect, t.Block.Darkened(0.45f));
                continue;
            }
            l.DrawColoredPolygon(Shapes.RoundRect(new Rect2(rect.Position + new Vector2(2, 4), rect.Size), 3), new Color(0, 0, 0, 0.28f));
            var poly = Shapes.RoundRect(rect, Mathf.Min(4, r.H * 0.5f));
            Shapes.Gradient(l, poly, top, bottom);
            Shapes.DrawClosed(l, poly, t.Block.Darkened(0.55f), 1f);
            // Subtle panel lines on thick blocks.
            if (r.H >= 16)
                for (float y = r.Y + 10; y < r.Bottom - 4; y += 10)
                    l.DrawLine(new Vector2(r.X + 3, y), new Vector2(r.Right - 3, y), t.Block.Darkened(0.2f) with { A = 0.6f }, 1, true);
        }

        foreach (var d in _decos) DrawDeco(l, d.P, d.Size, d.Kind, d.Phase, d.Below, glowPass: false);

        // Jump pad bases.
        for (int i = 0; i < Layout.Pads.Count; i++)
        {
            var p = Layout.Pads[i];
            var plate = new Rect2(p.X1, p.Y - 4, p.X2 - p.X1, 4);
            Shapes.Fill(l, Shapes.RoundRect(plate, 2), new Color("#2b2f45"), new Color("#11131c"), 0.8f);
            l.DrawColoredPolygon(Shapes.RoundRect(new Rect2(p.X1 + 3, p.Y - 5.5f, p.X2 - p.X1 - 6, 2.2f), 1.1f),
                Shapes.Hdr(t.Edge, 1.2f + _padFlash[i] * 2));
        }
    }

    private void DrawGlow(DrawLayer l)
    {
        if (Layout == null) return;
        var t = Theme;
        // Abyss glow at the bottom, visible through pits.
        float h = MapLayout.Height;
        l.DrawPolygon(new[] { new Vector2(0, h - 70), new Vector2(MapLayout.Width, h - 70), new Vector2(MapLayout.Width, h), new Vector2(0, h) },
            new[] { t.Edge with { A = 0 }, t.Edge with { A = 0 }, t.Edge with { A = 0.35f }, t.Edge with { A = 0.35f } });

        foreach (var r in Layout.Solids)
        {
            if (IsBorder(r)) continue;
            // Neon top edge plus a soft bloom above it.
            l.DrawColoredPolygon(Shapes.RoundRect(new Rect2(r.X + 1, r.Y, r.W - 2, 2f), 1f), Shapes.Hdr(t.Edge, 1.25f));
            l.DrawTextureRect(GlowTex.Tex, new Rect2(r.X - 6, r.Y - 9, r.W + 12, 18), false, t.Edge with { A = 0.22f });
        }

        foreach (var d in _decos) DrawDeco(l, d.P, d.Size, d.Kind, d.Phase, d.Below, glowPass: true);

        for (int i = 0; i < Layout.Pads.Count; i++)
        {
            var p = Layout.Pads[i];
            float f = _padFlash[i];
            var c = t.Edge;
            // A light cone and chevrons climbing out of the pad.
            float cx = p.CenterX, w = p.X2 - p.X1;
            l.DrawPolygon(new[] { new Vector2(p.X1 + 2, p.Y - 5), new Vector2(p.X2 - 2, p.Y - 5), new Vector2(cx + w * 0.7f, p.Y - 60), new Vector2(cx - w * 0.7f, p.Y - 60) },
                new[] { c with { A = 0.35f + f * 0.5f }, c with { A = 0.35f + f * 0.5f }, c with { A = 0 }, c with { A = 0 } });
            for (int k = 0; k < 3; k++)
            {
                float u = Mathf.PosMod(_t * 1.2f + k / 3f, 1f);
                float y = p.Y - 8 - u * 40;
                var col = Shapes.Hdr(c, 1.3f) with { A = (1 - u) * 0.9f };
                l.DrawPolyline(new[] { new Vector2(cx - 6, y + 4), new Vector2(cx, y), new Vector2(cx + 6, y + 4) }, col, 1.6f, true);
            }
            if (f > 0) GlowTex.Draw(l, new Vector2(cx, p.Y - 10), 40 * f + 10, c with { A = f * 0.8f });
        }
    }

    private void DrawDeco(DrawLayer l, Vector2 p, float size, int kind, float phase, bool below, bool glowPass)
    {
        var t = Theme;
        var a = kind == 0 ? t.MoteA : t.MoteB;
        switch (t.Deco)
        {
            case Deco.Neon:
                if (!below) return;
                // String lights under platforms.
                float on = 0.55f + 0.45f * Mathf.Sin(_t * 3 + phase * 3);
                if (glowPass) GlowTex.Draw(l, p + new Vector2(0, 2.5f), 7, a with { A = 0.35f * on });
                else l.DrawCircle(p + new Vector2(0, 2.5f), 1.3f, Shapes.Hdr(a, 1 + on), true, -1, true);
                break;
            case Deco.Grass:
                if (below || glowPass) return;
                var grass = t.Edge.Lerp(new Color("#62d26f"), 0.65f);
                for (int i = -1; i <= 1; i++)
                {
                    float sway = Mathf.Sin(_t * 2 + phase + i) * 1.2f;
                    var basePt = p + new Vector2(i * 2.2f, 0);
                    l.DrawColoredPolygon(new[] { basePt + new Vector2(-1.1f, 0), basePt + new Vector2(sway + i, -5.5f * size - (i == 0 ? 2 : 0)), basePt + new Vector2(1.1f, 0) },
                        i == 0 ? grass : grass.Darkened(0.2f));
                }
                break;
            case Deco.Weeds:
                if (below) return;
                var pts = new Vector2[6];
                for (int i = 0; i < 6; i++)
                    pts[i] = p + new Vector2(Mathf.Sin(_t * 1.6f + phase + i * 0.8f) * i * 0.6f, -i * 2.6f * size);
                if (glowPass) return;
                l.DrawPolyline(pts, a.Darkened(0.2f) with { A = 0.85f }, 1.6f, true);
                break;
            case Deco.Mushrooms:
                if (below) return;
                float glowK = 0.6f + 0.4f * Mathf.Sin(_t * 1.5f + phase);
                var cap = p + new Vector2(0, -4 * size);
                if (glowPass) { GlowTex.Draw(l, cap, 9 * size, a with { A = 0.35f * glowK }); return; }
                l.DrawLine(p, cap, new Color("#e9f5d0"), 1.4f, true);
                l.DrawColoredPolygon(Shapes.Ellipse(cap, 3.2f * size, 2f * size, 14), Shapes.Hdr(a, 1 + glowK * 0.4f));
                break;
            case Deco.Cracks:
                // Glowing lava drips that swell and fall from under platforms.
                if (!below || size < 0.95f) return;
                float cycle = Mathf.PosMod(_t * 0.5f * size + phase, 1f);
                float swell = Mathf.Min(1, cycle * 1.6f);
                var drip = p + new Vector2(0, 1 + swell * 2.2f + Mathf.Max(0, cycle - 0.62f) * 60);
                float dr = 0.6f + 0.9f * swell;
                if (glowPass) GlowTex.Draw(l, drip, dr * 6, a with { A = 0.4f });
                else l.DrawColoredPolygon(Shapes.Ellipse(drip, dr, dr * 1.35f, 10), Shapes.Hdr(a, 1.5f));
                break;
            case Deco.Icicles:
                if (!below || glowPass) return;
                float len = 4 + 6 * size;
                l.DrawColoredPolygon(new[] { p + new Vector2(-1.6f, 0), p + new Vector2(1.6f, 0), p + new Vector2(0, len) }, new Color("#dff6ff") with { A = 0.9f });
                l.DrawLine(p + new Vector2(-0.6f, 0.5f), p + new Vector2(-0.1f, len * 0.6f), new Color(1, 1, 1, 0.9f), 0.6f, true);
                break;
            case Deco.Sprinkles:
                if (below || glowPass) return;
                var cols = new[] { new Color("#ff5f9e"), new Color("#7af7ff"), new Color("#fff27a"), new Color("#9dff7a") };
                var col = cols[(int)(phase * 10) % cols.Length];
                var dir = Vector2.FromAngle(phase) * 1.6f;
                l.DrawLine(p + new Vector2(0, -2.2f) - dir, p + new Vector2(0, -2.2f) + dir, col, 1.3f, true);
                break;
        }
    }
}
