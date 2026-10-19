using System.Collections.Generic;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// The animated scenery behind an arena (and the menus): a gradient sky, a glowing sun, two layers of silhouettes
/// and drifting motes. Everything is generated from the theme and a seed and is purely cosmetic.
/// </summary>
public partial class Backdrop : Node2D
{
    public Vector2 Size = new(MapLayout.Width, MapLayout.Height);
    public ArenaTheme Theme { get; private set; } = ArenaTheme.All[0];

    private int _seed;
    private float _t;
    private Vector2 _sun;
    private readonly List<Vector2[]> _far = new(), _near = new();
    private readonly List<(Rect2 R, int Kind, float Phase, bool Far)> _windows = new();
    private readonly List<(Vector2 P, float R, float Speed, float Phase, int Kind)> _motes = new();
    private DrawLayer _glow, _land, _motesLayer;

    public override void _Ready()
    {
        _glow = new DrawLayer(DrawGlow, additive: true);
        _land = new DrawLayer(DrawLand);
        _motesLayer = new DrawLayer(DrawMotes, additive: true);
        AddChild(_glow);
        AddChild(_land);
        AddChild(_motesLayer);
        if (_far.Count == 0) Set(Theme, 1);
    }

    public void Set(ArenaTheme theme, int seed)
    {
        Theme = theme;
        _seed = seed;
        var rng = new Rng((ulong)(uint)seed ^ 0xA5A5_5A5AUL);
        _sun = new Vector2(rng.Range(0.15f, 0.85f) * Size.X, rng.Range(0.18f, 0.4f) * Size.Y);
        _far.Clear();
        _near.Clear();
        _windows.Clear();
        BuildLayer(rng, _far, Size.Y * 0.42f, Size.Y * 0.72f, far: true);
        BuildLayer(rng, _near, Size.Y * 0.62f, Size.Y * 0.86f, far: false);

        _motes.Clear();
        int n = theme.Motes == Motes.Bokeh ? 26 : 46;
        for (int i = 0; i < n; i++)
            _motes.Add((new Vector2(rng.Range(0f, Size.X), rng.Range(0f, Size.Y)),
                theme.Motes == Motes.Bokeh ? rng.Range(6f, 22f) : rng.Range(1.2f, 3.2f),
                rng.Range(0.4f, 1f), rng.Range(0f, Mathf.Tau), rng.Range(0, 1)));
        QueueRedrawAll();
    }

    private void BuildLayer(Rng rng, List<Vector2[]> into, float minY, float maxY, bool far)
    {
        float w = Size.X, bottom = Size.Y + 2;
        switch (Theme.Skyline)
        {
            case Skyline.City:
            {
                float x = -10;
                while (x < w)
                {
                    float bw = rng.Range(far ? 26f : 34f, far ? 56f : 80f);
                    float top = rng.Range(minY - (far ? 80 : 30), maxY);
                    into.Add(new[] { new Vector2(x, bottom), new Vector2(x, top), new Vector2(x + bw, top), new Vector2(x + bw, bottom) });
                    if (rng.Chance(0.3f)) // antenna
                        into.Add(new[] { new Vector2(x + bw * 0.5f - 1, top), new Vector2(x + bw * 0.5f, top - rng.Range(10f, 26f)), new Vector2(x + bw * 0.5f + 1, top) });
                    for (float wy = top + 6; wy < bottom - 8; wy += 8)
                        for (float wx = x + 4; wx < x + bw - 5; wx += 6)
                            if (rng.Chance(far ? 0.07f : 0.12f))
                                _windows.Add((new Rect2(wx, wy, far ? 2f : 2.6f, far ? 2.6f : 3.4f), rng.Range(0, 1), rng.Range(0f, 50f), far));
                    x += bw + rng.Range(0f, far ? 6f : 14f);
                }
                break;
            }
            case Skyline.Mountains:
            {
                var pts = new List<Vector2> { new(-10, bottom) };
                float x = -10;
                while (x < w + 40)
                {
                    pts.Add(new Vector2(x, rng.Range(minY, maxY)));
                    x += rng.Range(far ? 50f : 36f, far ? 110f : 80f);
                }
                pts.Add(new Vector2(w + 40, bottom));
                into.Add(pts.ToArray());
                break;
            }
            case Skyline.Hills:
            {
                var pts = new List<Vector2> { new(-10, bottom) };
                float p1 = rng.Range(0f, 6f), p2 = rng.Range(0f, 6f), f1 = rng.Range(0.006f, 0.012f), f2 = rng.Range(0.015f, 0.03f);
                for (float x = -10; x <= w + 10; x += 6)
                {
                    float k = 0.5f + 0.35f * Mathf.Sin(x * f1 + p1) + 0.15f * Mathf.Sin(x * f2 + p2);
                    pts.Add(new Vector2(x, Mathf.Lerp(maxY, minY, k)));
                }
                pts.Add(new Vector2(w + 10, bottom));
                into.Add(pts.ToArray());
                break;
            }
            case Skyline.Spires:
            {
                float x = -20;
                while (x < w + 20)
                {
                    float sw = rng.Range(far ? 14f : 20f, far ? 34f : 46f);
                    float top = rng.Range(minY - 40, maxY);
                    into.Add(new[] { new Vector2(x, bottom), new Vector2(x + sw * rng.Range(0.35f, 0.65f), top), new Vector2(x + sw, bottom) });
                    x += sw * rng.Range(0.45f, 0.9f);
                }
                break;
            }
        }
    }

    private void QueueRedrawAll()
    {
        QueueRedraw();
        _glow?.QueueRedraw();
        _land?.QueueRedraw();
        _motesLayer?.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        _t += (float)delta;
        _glow.QueueRedraw();
        _motesLayer.QueueRedraw();
        // The land layer (silhouettes and lit windows) is static: it only redraws when the theme changes.
    }

    public override void _Draw()
    {
        var t = Theme;
        float mid = Size.Y * 0.55f;
        DrawPolygon(new[] { Vector2.Zero, new Vector2(Size.X, 0), new Vector2(Size.X, mid), new Vector2(0, mid) },
            new[] { t.SkyTop, t.SkyTop, t.SkyMid, t.SkyMid });
        DrawPolygon(new[] { new Vector2(0, mid), new Vector2(Size.X, mid), Size, new Vector2(0, Size.Y) },
            new[] { t.SkyMid, t.SkyMid, t.SkyBottom, t.SkyBottom });
    }

    private void DrawGlow(DrawLayer l)
    {
        var t = Theme;
        float pulse = 1f + 0.04f * Mathf.Sin(_t * 0.8f);
        GlowTex.Draw(l, _sun, 190 * pulse, t.SkyBottom.Lightened(0.2f) with { A = 0.35f });
        GlowTex.Draw(l, _sun, 70 * pulse, t.MoteA with { A = 0.55f });
        l.DrawCircle(_sun, 22, t.MoteA.Lightened(0.5f) with { A = 0.55f }, true, -1, true);
        // A few slow light shafts from the sun.
        for (int i = 0; i < 5; i++)
        {
            float a = Mathf.Pi * 0.5f + (i - 2) * 0.32f + Mathf.Sin(_t * 0.15f + i) * 0.05f;
            var dir = Vector2.FromAngle(a);
            var side = new Vector2(-dir.Y, dir.X) * 26;
            var far = _sun + dir * 520;
            l.DrawPolygon(new[] { _sun, far + side, far - side },
                new[] { t.MoteA with { A = 0.07f }, t.MoteA with { A = 0 }, t.MoteA with { A = 0 } });
        }
    }

    private void DrawLand(DrawLayer l)
    {
        var t = Theme;
        var farCol = t.SkyMid.Lerp(t.SkyTop, 0.45f);
        var nearCol = t.SkyTop.Lerp(t.Block, 0.4f).Darkened(0.15f);
        foreach (var poly in _far) Shapes.Gradient(l, poly, farCol, farCol.Lerp(t.SkyBottom, 0.25f));
        DrawWindows(l, far: true);
        // Haze between the layers.
        float hy = Size.Y * 0.55f;
        l.DrawPolygon(new[] { new Vector2(0, hy), new Vector2(Size.X, hy), Size, new Vector2(0, Size.Y) },
            new[] { t.SkyBottom with { A = 0 }, t.SkyBottom with { A = 0 }, t.SkyBottom with { A = 0.35f }, t.SkyBottom with { A = 0.35f } });
        foreach (var poly in _near) Shapes.Gradient(l, poly, nearCol, nearCol.Darkened(0.3f));
        DrawWindows(l, far: false);
    }

    private void DrawWindows(DrawLayer l, bool far)
    {
        foreach (var (r, kind, phase, isFar) in _windows)
        {
            if (isFar != far || Mathf.Sin(phase) < -0.85f) continue; // a few windows stay dark
            var c = kind == 0 ? Theme.MoteA : Theme.MoteB;
            l.DrawRect(r, c with { A = far ? 0.3f : 0.55f });
        }
    }

    private void DrawMotes(DrawLayer l)
    {
        var t = Theme;
        foreach (var (p0, r, speed, phase, kind) in _motes)
        {
            var c = kind == 0 ? t.MoteA : t.MoteB;
            Vector2 p;
            float a;
            switch (t.Motes)
            {
                case Motes.Embers:
                    p = Wrap(p0 + new Vector2(Mathf.Sin(_t * speed + phase) * 14, -_t * 30 * speed));
                    a = 0.6f + 0.4f * Mathf.Sin(_t * 5 * speed + phase);
                    GlowTex.Draw(l, p, r * 5, c with { A = 0.35f * a });
                    l.DrawCircle(p, r * 0.6f, Shapes.Hdr(c, 1.6f) with { A = a }, true, -1, true);
                    break;
                case Motes.Bubbles:
                    p = Wrap(p0 + new Vector2(Mathf.Sin(_t * speed * 1.5f + phase) * 6, -_t * 22 * speed));
                    l.DrawArc(p, r * 1.6f, 0, Mathf.Tau, 16, c with { A = 0.35f }, 0.8f, true);
                    l.DrawCircle(p + new Vector2(-r * 0.5f, -r * 0.5f), r * 0.35f, c with { A = 0.5f }, true, -1, true);
                    break;
                case Motes.Snow:
                    p = Wrap(p0 + new Vector2(Mathf.Sin(_t * speed + phase) * 18 + _t * 8, _t * 24 * speed));
                    l.DrawCircle(p, r * 0.8f, c with { A = 0.55f }, true, -1, true);
                    break;
                case Motes.Fireflies:
                    p = Wrap(p0 + new Vector2(Mathf.Sin(_t * 0.6f * speed + phase) * 30, Mathf.Cos(_t * 0.45f * speed + phase * 2) * 20));
                    a = Mathf.Max(0, Mathf.Sin(_t * 2 * speed + phase));
                    GlowTex.Draw(l, p, r * 6, c with { A = 0.4f * a });
                    l.DrawCircle(p, r * 0.6f, Shapes.Hdr(c, 1.5f) with { A = a }, true, -1, true);
                    break;
                default:
                    p = Wrap(p0 + new Vector2(_t * 6 * speed, Mathf.Sin(_t * 0.3f + phase) * 10));
                    a = 0.5f + 0.5f * Mathf.Sin(_t * 0.7f * speed + phase);
                    GlowTex.Draw(l, p, r, c with { A = 0.08f + 0.1f * a });
                    break;
            }
        }
    }

    private Vector2 Wrap(Vector2 p) => new(Mathf.PosMod(p.X + 40, Size.X + 80) - 40, Mathf.PosMod(p.Y + 40, Size.Y + 80) - 40);
}
