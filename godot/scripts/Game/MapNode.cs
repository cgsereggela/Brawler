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
    // Static layers are drawn once per arena; only the small animated layers redraw every frame.
    private DrawLayer _solids, _glow, _anim, _animGlow;
    private readonly List<(Vector2 P, float Size, int Kind, float Phase, bool Below)> _decos = new();
    private readonly HashSet<RectI> _groundDrawn = new();
    // The ground and platforms, painted once per battle (TerrainPainter), and their glowing veins (or null).
    private ImageTexture _ground, _groundGlow;
    private System.Threading.Tasks.Task<(Raster Body, Raster Glow)> _bake;
    private float _groundFade;
    private bool _backdropSet;
    private List<Vector2[]> _groundPolys;
    private List<Rect2> _borderRects;

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
        _anim = new DrawLayer(DrawAnim);
        _animGlow = new DrawLayer(DrawAnimGlow, additive: true);
        AddChild(_anim);
        AddChild(_animGlow);
    }

    public void Build(MapLayout layout)
    {
        foreach (var c in _bodies.GetChildren()) c.QueueFree();
        Layout = layout;
        _decos.Clear();
        if (layout != null)
        {
            Theme = ArenaTheme.ForSeed(layout.Seed);
            // The same scenery again (the editor rebuilding after an edit, a custom arena played twice) keeps its painting.
            if (Backdrop.Theme != Theme || Backdrop.Seed != layout.Seed || !_backdropSet) Backdrop.Set(Theme, layout.Seed);
            _backdropSet = true;
            foreach (var r in layout.Solids)
            {
                var body = new StaticBody2D { CollisionLayer = 1, CollisionMask = 0, Position = new Vector2(r.X + r.W / 2f, r.Y + r.H / 2f) };
                body.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(r.W, r.H) } });
                _bodies.AddChild(body);
            }
            foreach (var s in layout.Slopes)
            {
                var pts = Array.ConvertAll(s.Outline(), p => new Vector2(p.X, p.Y));
                var body = new StaticBody2D { CollisionLayer = 1, CollisionMask = 0 };
                body.AddChild(new CollisionShape2D { Shape = new ConvexPolygonShape2D { Points = pts } });
                _bodies.AddChild(body);
            }
            // Floor segments with terrain are drawn as one piece of ground along their outline, not block by block.
            _groundDrawn.Clear();
            foreach (var r in layout.Terrain) _groundDrawn.Add(r);
            foreach (var g in layout.Ground)
                foreach (var r in layout.Solids)
                    if (r.Y == MapGenerator.FloorY && r.X == g[0].X && r.Right == g[^1].X) _groundDrawn.Add(r);
            _padFlash = new float[layout.Pads.Count];
            BuildDecos();
            PaintGround();
        }
        else (_ground, _groundGlow, _groundPolys, _bake) = (null, null, null, null);
        _solids.QueueRedraw();
        _glow.QueueRedraw();
        _anim.QueueRedraw();
        _animGlow.QueueRedraw();
    }

    /// <summary>Decorations that sway or pulse redraw every frame; the rest are baked into the static layers.</summary>
    private bool DecoAnimated => Theme.Deco is Deco.Grass or Deco.Weeds or Deco.Mushrooms or Deco.Cracks
        or Deco.Torches or Deco.Ferns or Deco.Circuits;

    private static bool IsBorder(RectI r) => r.X <= 0 || r.Y <= 0 || r.Right >= MapLayout.Width;

    private void BuildDecos()
    {
        var rng = new Rng((ulong)(uint)Layout.Seed ^ 0x9E3779B9UL);
        foreach (var s in Layout.Surfaces)
        {
            if (Layout.DriftIndex(s) >= 0) continue; // drifting platforms move: nothing painted on them stays put
            int n = s.Width / 22;
            for (int i = 0; i < n; i++)
            {
                if (!rng.Chance(0.55f)) continue;
                float x = rng.Range(s.X1 + 4f, s.X2 - 4f);
                bool onPad = false;
                foreach (var p in Layout.Pads)
                    if (p.Y == s.Y && x > p.X1 - 6 && x < p.X2 + 6) onPad = true;
                foreach (var c in Layout.Conveyors)
                    if (c.Y == s.Y && x > c.X - 6 && x < c.X + c.W + 6) onPad = true; // nor on a belt, which would run under it
                if (!onPad) _decos.Add((new Vector2(x, s.Y), rng.Range(0.7f, 1.3f), rng.Range(0, 2), rng.Range(0f, Mathf.Tau), false));
            }
        }
        // Along ramps too, following the slope.
        foreach (var s in Layout.Slopes)
            for (int i = 0; i < s.Width / 22; i++)
            {
                if (!rng.Chance(0.55f)) continue;
                float x = rng.Range(s.X1 + 3f, s.X2 - 3f);
                _decos.Add((new Vector2(x, s.YAt(x)), rng.Range(0.7f, 1.3f), rng.Range(0, 2), rng.Range(0f, Mathf.Tau), false));
            }
        // Undersides: icicles, string lights and such hang from floating platforms.
        foreach (var r in Layout.Solids)
        {
            if (IsBorder(r) || r.Y >= MapGenerator.FloorY || _groundDrawn.Contains(r)) continue;
            for (float x = r.X + 5; x < r.Right - 4; x += rng.Range(7f, 13f))
                _decos.Add((new Vector2(x, r.Bottom), rng.Range(0.6f, 1.3f), rng.Range(0, 2), rng.Range(0f, Mathf.Tau), true));
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        TakeBake(dt);
        if (Layout == null || !IsVisibleInTree()) return;
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
        _anim.QueueRedraw();
        _animGlow.QueueRedraw();
    }

    /// <summary>Swap in a finished ground painting and fade it in.</summary>
    private void TakeBake(float dt)
    {
        if (_bake is { IsCompleted: true } done)
        {
            _bake = null;
            if (done.IsCompletedSuccessfully)
            {
                var (body, glow) = done.Result;
                (_ground, _groundGlow) = (body.ToTexture(), glow?.ToTexture());
                _groundFade = 0;
            }
            else GD.PushError($"Painting the arena failed: {done.Exception?.GetBaseException().Message}");
        }
        if (_ground == null || _groundFade >= 1) return;
        _groundFade = Mathf.Min(1, _groundFade + dt * 4);
        _solids.QueueRedraw();
        _glow.QueueRedraw();
    }

    // ---------- drawing ----------

    private void DrawSolids(DrawLayer l)
    {
        if (Layout == null) return;
        var full = new Rect2(0, 0, MapLayout.Width, MapLayout.Height);
        // A soft shadow under floating platforms, then the painted ground itself.
        foreach (var r in Layout.Solids)
            if (!IsBorder(r) && !_groundDrawn.Contains(r) && r.Y < MapGenerator.FloorY)
                l.DrawTextureRect(GlowTex.Tex, new Rect2(r.X - 4, r.Bottom - 5, r.W + 8, 16), false, new Color(0, 0, 0, 0.35f));
        if (_groundFade < 1 && _groundPolys != null)
        {
            // Plain silhouettes while the painting is on its way (and under it as it fades in).
            var flat = TerrainPainter.Ink(Theme).Lerp(Theme.Block, 0.45f);
            foreach (var b in _borderRects) l.DrawRect(b, flat.Darkened(0.3f));
            foreach (var poly in _groundPolys) l.DrawColoredPolygon(poly, flat);
        }
        if (_ground != null) l.DrawTextureRect(_ground, full, false, new Color(1, 1, 1, _groundFade));

        if (!DecoAnimated)
            DrawDecos(l, glowPass: false);
    }

    private void DrawAnim(DrawLayer l)
    {
        if (Layout == null) return;
        if (DecoAnimated)
            DrawDecos(l, glowPass: false);

        // Jump pad bases.
        for (int i = 0; i < Layout.Pads.Count; i++)
        {
            var p = Layout.Pads[i];
            var plate = new Rect2(p.X1, p.Y - 4, p.X2 - p.X1, 4);
            Shapes.Fill(l, Shapes.RoundRect(plate, 2), new Color("#2b2f45"), new Color("#11131c"), 0.8f);
            l.DrawColoredPolygon(Shapes.RoundRect(new Rect2(p.X1 + 3, p.Y - 5.5f, p.X2 - p.X1 - 6, 2.2f), 1.1f),
                Shapes.Hdr(Theme.Edge, 1.2f + _padFlash[i] * 2));
        }

        // Portal rings: each pair in its own colour.
        for (int i = 0; i < Layout.Portals.Count; i++)
        {
            PortalArt.Base(l, Layout.Portals[i].A, PortalArt.ColorOf(i), _t);
            PortalArt.Base(l, Layout.Portals[i].B, PortalArt.ColorOf(i), _t);
        }

        // Conveyor belts: a dark track set into the ground with a roller at each end, and chevrons running the way it
        // carries you (all of them in one batch).
        if (Layout.Conveyors.Count == 0) return;
        _beltLines.Clear();
        foreach (var c in Layout.Conveyors)
        {
            var track = new Rect2(c.X, c.Y - 2, c.W, Conveyor.Thick + 2);
            Shapes.Fill(l, Shapes.RoundRect(track, 3), new Color("#2b2f45"), new Color("#11131c"), 0.8f);
            foreach (float rx in new[] { c.X + 3.5f, c.X + c.W - 3.5f })
                l.DrawCircle(new Vector2(rx, c.Y + 1), 2.2f, new Color("#4a5070"));
            const float Gap = 8;
            float shift = Mathf.PosMod(_t * c.Vel, Gap);
            for (float x = c.X + 4 + shift; x < c.X + c.W - 6; x += Gap)
            {
                float tip = x + c.Dir * 2.5f;
                _beltLines.Add(new Vector2(x, c.Y - 1.2f)); _beltLines.Add(new Vector2(tip, c.Y + 1));
                _beltLines.Add(new Vector2(tip, c.Y + 1)); _beltLines.Add(new Vector2(x, c.Y + 3.2f));
            }
        }
        l.DrawMultiline(_beltLines.ToArray(), Shapes.Hdr(Theme.Edge, 0.9f), 1.2f);
    }

    private readonly System.Collections.Generic.List<Vector2> _beltLines = new();

    private void DrawGlow(DrawLayer l)
    {
        if (Layout == null) return;
        var t = Theme;
        // A faint light rising from the depths, visible through pits.
        float h = MapLayout.Height;
        l.DrawPolygon(new[] { new Vector2(0, h - 70), new Vector2(MapLayout.Width, h - 70), new Vector2(MapLayout.Width, h), new Vector2(0, h) },
            new[] { t.Edge with { A = 0 }, t.Edge with { A = 0 }, t.Edge with { A = 0.14f }, t.Edge with { A = 0.14f } });

        // Glowing veins (magma), lifted into HDR so they bloom.
        if (_groundGlow != null) l.DrawTextureRect(_groundGlow, new Rect2(0, 0, MapLayout.Width, h), false, new Color(1.6f, 1.6f, 1.6f, _groundFade));
        // A gentle sheen of light just above each platform's top.
        foreach (var r in Layout.Solids)
        {
            if (IsBorder(r) || _groundDrawn.Contains(r)) continue;
            l.DrawTextureRect(GlowTex.Tex, new Rect2(r.X - 4, r.Y - 6, r.W + 8, 10), false, t.Edge with { A = 0.07f });
        }

        if (!DecoAnimated)
            DrawDecos(l, glowPass: true);
    }

    private void DrawAnimGlow(DrawLayer l)
    {
        if (Layout == null) return;
        var t = Theme;
        if (DecoAnimated)
            DrawDecos(l, glowPass: true);

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

        for (int i = 0; i < Layout.Portals.Count; i++)
        {
            PortalArt.Glow(l, Layout.Portals[i].A, PortalArt.ColorOf(i), _t);
            PortalArt.Glow(l, Layout.Portals[i].B, PortalArt.ColorOf(i), _t);
        }
    }

    private static Vector2[] GroundOutline(GroundPoint[] g)
    {
        var pts = new List<Vector2>();
        float h = MapLayout.Height;
        // Rounded top corners at the segment's ends, like a plain floor block's.
        pts.Add(new Vector2(g[0].X, h));
        pts.Add(new Vector2(g[0].X, g[0].Y + 4));
        pts.Add(new Vector2(g[0].X + 1.2f, g[0].Y + 1.2f));
        pts.Add(new Vector2(g[0].X + 4, g[0].Y));
        for (int i = 1; i + 1 < g.Length; i++) pts.Add(new Vector2(g[i].X, g[i].Y));
        pts.Add(new Vector2(g[^1].X - 4, g[^1].Y));
        pts.Add(new Vector2(g[^1].X - 1.2f, g[^1].Y + 1.2f));
        pts.Add(new Vector2(g[^1].X, g[^1].Y + 4));
        pts.Add(new Vector2(g[^1].X, h));
        return pts.ToArray();
    }

    /// <summary>
    /// Craggy rock hanging up to 4 px below a floating platform (cosmetic; collision stays the box), tapering to the
    /// corners so the sides stay straight.
    /// </summary>
    private Vector2[] Underside(RectI r)
    {
        var pts = new List<Vector2> { new(r.Right - 2, r.Bottom - 2) };
        for (float x = r.Right - 3; x > r.X + 3; x -= 3)
        {
            float edge = Mathf.Min(x - r.X, r.Right - x);
            float drop = Mathf.Min(1, edge / 10f) * (1 + 3 * NoiseTile.Hash((int)x, r.Y, Layout.Seed));
            pts.Add(new Vector2(x, r.Bottom + drop));
        }
        pts.Add(new Vector2(r.X + 2, r.Bottom - 2));
        return pts.ToArray();
    }

    private void PaintGround()
    {
        var ground = new List<Vector2[]>();
        var border = new List<Rect2>();
        foreach (var r in Layout.Solids)
        {
            if (IsBorder(r)) border.Add(new Rect2(r.X, r.Y, r.W, r.H));
            else if (!_groundDrawn.Contains(r))
            {
                ground.Add(Shapes.RoundRect(new Rect2(r.X, r.Y, r.W, r.H), Mathf.Min(3, r.H * 0.5f)));
                if (r.Y < MapGenerator.FloorY) ground.Add(Underside(r));
            }
        }
        foreach (var g in Layout.Ground) ground.Add(GroundOutline(g));
        _groundPolys = ground;
        _borderRects = border;
        (_ground, _groundGlow) = (null, null);
        _groundFade = 0; // the silhouettes stand in from now until the new painting lands
        // Paint on a worker thread so starting a battle doesn't stall a frame; the plain silhouettes stand in until
        // it lands (Process), then the painting fades in over them.
        PaintNoise.Warm();
        var shapes = new TerrainPainter.GroundShapes(ground, border);
        var theme = Theme;
        int seed = Layout.Seed;
        _bake = System.Threading.Tasks.Task.Run(() => TerrainPainter.Bake(shapes, theme, seed));
    }

    // Decorations go out as two batches per pass (shapes, and glow sprites in the glow pass) instead of a draw call
    // each: on the themes whose decorations move, they're redrawn every frame, and a few hundred little lines and
    // polygons cost the slowest themes up to two milliseconds a frame.
    private readonly TriBatch _decoTris = new(), _decoGlows = new(textured: true);
    private static readonly Color GrassTint = new("#8fa860"), MushroomStem = new("#e9f5d0"), Icicle = new Color("#dff6ff") with { A = 0.9f },
        Chain = new("#6b5a52"), Candle = new("#e8dcc0"), CandleTop = new("#fff6e0"), Vine = new("#4f6b3a"), FernTint = new("#7f9e54");
    private static readonly Color[] SprinkleColors = { new("#ff5f9e"), new("#7af7ff"), new("#fff27a"), new("#9dff7a") };

    /// <summary>Draws every decoration into <paramref name="l"/>, the glow parts or the solid ones.</summary>
    private void DrawDecos(DrawLayer l, bool glowPass)
    {
        var b = _decoTris;
        b.Begin(l);
        b.Feather = 0.5f; // the soft rim of Godot's antialiased circles
        b.SoftLines = true;
        if (glowPass) _decoGlows.Begin(l, GlowTex.Tex);
        foreach (var d in _decos) DrawDeco(d.P, d.Size, d.Kind, d.Phase, d.Below, glowPass);
        b.Flush();
        if (glowPass) _decoGlows.Flush();
    }

    private void DrawDeco(Vector2 p, float size, int kind, float phase, bool below, bool glowPass)
    {
        var t = Theme;
        var b = _decoTris;
        var g = _decoGlows;
        var a = kind == 0 ? t.MoteA : t.MoteB;
        switch (t.Deco)
        {
            case Deco.Neon:
                if (!below) return;
                // String lights under platforms.
                float on = 0.55f + 0.45f * Mathf.Sin(_t * 3 + phase * 3);
                if (glowPass) g.Sprite(p + new Vector2(0, 2.5f), 7, a with { A = 0.35f * on });
                else b.Circle(p + new Vector2(0, 2.5f), 1.3f, Shapes.Hdr(a, 1 + on));
                break;
            case Deco.Grass:
                if (below || glowPass) return;
                var grass = t.Block.Lerp(GrassTint, 0.6f);
                for (int i = -1; i <= 1; i++)
                {
                    float sway = Mathf.Sin(_t * 2 + phase + i) * 1.2f;
                    var basePt = p + new Vector2(i * 2.2f, 0);
                    b.Triangle(basePt + new Vector2(-1.1f, 0), basePt + new Vector2(sway + i, -5.5f * size - (i == 0 ? 2 : 0)), basePt + new Vector2(1.1f, 0),
                        i == 0 ? grass : grass.Darkened(0.2f));
                }
                break;
            case Deco.Weeds:
                if (below || glowPass) return;
                Span<Vector2> pts = stackalloc Vector2[6];
                for (int i = 0; i < 6; i++)
                    pts[i] = p + new Vector2(Mathf.Sin(_t * 1.6f + phase + i * 0.8f) * i * 0.6f, -i * 2.6f * size);
                b.Stroke(pts, 1.6f, a.Darkened(0.2f) with { A = 0.85f });
                break;
            case Deco.Mushrooms:
                if (below) return;
                float glowK = 0.6f + 0.4f * Mathf.Sin(_t * 1.5f + phase);
                var cap = p + new Vector2(0, -4 * size);
                if (glowPass) { g.Sprite(cap, 9 * size, a with { A = 0.35f * glowK }); return; }
                b.Line(p, cap, 1.4f, MushroomStem);
                b.Polygon(Shapes.Ellipse(cap, 3.2f * size, 2f * size, 14), Shapes.Hdr(a, 1 + glowK * 0.4f));
                break;
            case Deco.Cracks:
                // Glowing lava drips that swell and fall from under platforms.
                if (!below || size < 0.95f) return;
                float cycle = Mathf.PosMod(_t * 0.5f * size + phase, 1f);
                float swell = Mathf.Min(1, cycle * 1.6f);
                var drip = p + new Vector2(0, 1 + swell * 2.2f + Mathf.Max(0, cycle - 0.62f) * 60);
                float dr = 0.6f + 0.9f * swell;
                if (glowPass) g.Sprite(drip, dr * 6, a with { A = 0.4f });
                else b.Polygon(Shapes.Ellipse(drip, dr, dr * 1.35f, 10), Shapes.Hdr(a, 1.5f));
                break;
            case Deco.Icicles:
                if (!below || glowPass) return;
                float len = 4 + 6 * size;
                b.Triangle(p + new Vector2(-1.6f, 0), p + new Vector2(1.6f, 0), p + new Vector2(0, len), Icicle);
                b.Line(p + new Vector2(-0.6f, 0.5f), p + new Vector2(-0.1f, len * 0.6f), 0.6f, new Color(1, 1, 1, 0.9f));
                break;
            case Deco.Sprinkles:
                if (below || glowPass) return;
                var col = SprinkleColors[(int)(phase * 10) % SprinkleColors.Length];
                var dir = Vector2.FromAngle(phase) * 1.6f;
                b.Line(p + new Vector2(0, -2.2f) - dir, p + new Vector2(0, -2.2f) + dir, 1.3f, col);
                break;
            case Deco.Torches:
                if (below)
                {
                    // Rusty chains hanging from the odd platform, swaying a touch.
                    if (size < 1.15f || glowPass) return;
                    float links = 2 + (int)(phase * 0.8f);
                    var tip = p + new Vector2(Mathf.Sin(_t * 0.9f + phase) * 0.8f * links, links * 3.2f);
                    for (int i = 0; i < links; i++)
                    {
                        var c = p.Lerp(tip, (i + 0.5f) / links);
                        b.Arc(c, i % 2 == 0 ? 1.4f : 0.9f, 0, Mathf.Tau, 8, Chain, 0.8f);
                    }
                    return;
                }
                // Candles of a few heights, with flickering flames.
                if (size < 1.05f) return;
                float ch = 2.5f + (size - 1) * 14;
                float flick = 0.75f + 0.25f * Mathf.Sin(_t * 11 + phase * 5) * Mathf.Sin(_t * 7.3f + phase);
                var flame = p + new Vector2(Mathf.Sin(_t * 5 + phase) * 0.3f, -ch - 1.6f);
                if (glowPass)
                {
                    g.Sprite(flame, 14 * flick, a with { A = 0.3f * flick });
                    b.Polygon(Shapes.Ellipse(flame, 0.9f, 1.7f * flick, 10), Shapes.Hdr(t.MoteB, 1.8f));
                    return;
                }
                Rect(b, new Rect2(p.X - 1.1f, p.Y - ch, 2.2f, ch), Candle);
                Rect(b, new Rect2(p.X - 1.1f, p.Y - ch, 2.2f, 0.7f), CandleTop);
                break;
            case Deco.Ferns:
                if (glowPass) return;
                if (below)
                {
                    // Vines trailing from platforms.
                    if (size < 1.1f) return;
                    Span<Vector2> vine = stackalloc Vector2[5];
                    for (int i = 0; i < 5; i++)
                        vine[i] = p + new Vector2(Mathf.Sin(_t * 1.1f + phase + i * 0.7f) * i * 0.5f, i * 2.4f * size);
                    b.Stroke(vine, 1f, Vine);
                    return;
                }
                var leaf = t.Block.Lerp(FernTint, 0.65f);
                if (kind == 1 && size > 1.1f)
                {
                    // A small flower on a stalk.
                    var head = p + new Vector2(Mathf.Sin(_t * 1.7f + phase) * 0.8f, -5 * size);
                    b.Line(p, head, 0.9f, leaf.Darkened(0.2f));
                    b.Circle(head, 1.3f, t.MoteA);
                    return;
                }
                // A fern: three arching fronds.
                for (int i = -1; i <= 1; i++)
                {
                    float sway = Mathf.Sin(_t * 1.5f + phase + i * 0.5f) * 0.8f;
                    var mid = p + new Vector2(i * 2.6f * size + sway * 0.5f, -4.5f * size);
                    var end = p + new Vector2(i * 4.4f * size + sway, -(i == 0 ? 6.5f : 3f) * size);
                    b.Stroke(stackalloc Vector2[] { p, mid, end }, 1.3f, i == 0 ? leaf : leaf.Darkened(0.18f));
                }
                break;
            case Deco.Circuits:
                // Status lights on and under the platforms, blinking in their own rhythms.
                if (!below && kind == 1) return;
                bool lit = Mathf.Sin(_t * (1.5f + size * 2) + phase * 4) > -0.2f;
                var led = p + new Vector2(0, below ? 1.6f : -1.4f);
                var ledCol = size > 1.1f ? t.MoteB : a;
                if (glowPass) { if (lit) g.Sprite(led, 6, ledCol with { A = 0.35f }); }
                else Rect(b, new Rect2(led - new Vector2(1, 0.7f), new Vector2(2, 1.4f)), lit ? Shapes.Hdr(ledCol, 1.6f) : ledCol.Darkened(0.6f));
                break;
        }
    }

    private static void Rect(TriBatch b, Rect2 r, Color c) =>
        b.Polygon(stackalloc Vector2[] { r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y) }, c);
}
