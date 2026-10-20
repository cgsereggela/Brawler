using System;
using System.Collections.Generic;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// The animated scenery behind an arena (and the menus), painted like a storybook backdrop: a cloudy gradient sky
/// with a hazy light, three layers of silhouettes that fade into the fog with distance, drifting fog banks between
/// them, slow light shafts and floating motes. The layers are painted into textures from the theme and a seed when
/// the arena changes; only the light, fog and motes move. Purely cosmetic.
/// </summary>
public partial class Backdrop : Node2D
{
    public Vector2 Size = new(MapLayout.Width, MapLayout.Height);
    public ArenaTheme Theme { get; private set; } = ArenaTheme.All[0];

    private int _seed;
    private float _t;
    private Vector2 _sun;
    // Silhouettes far to near (shapes in world space), and the windows lit in them.
    private readonly List<Vector2[]>[] _layers = { new(), new(), new() };
    private readonly List<(Rect2 R, int Kind, float Phase, int Layer)> _windows = new();
    private readonly List<(Vector2 P, float R, float Speed, float Phase, int Kind)> _motes = new();
    private ImageTexture _skyTex, _fogTex;
    private ImageTexture[] _layerTex = new ImageTexture[3];
    // The previous painting, shown under the new one while it fades in.
    private ImageTexture _oldSky;
    private ImageTexture[] _oldLayers;
    private float _fade = 1;
    private System.Threading.Tasks.Task<Painted> _bake;

    /// <summary>The painted rasters for one backdrop, made off the main thread.</summary>
    private sealed record Painted(Raster Sky, Raster[] Layers, Raster Fog);
    private DrawLayer _glow, _land, _motesLayer;

    /// <summary>Raster pixels per world pixel for the painted layers; the sky is smooth enough for a quarter of that.</summary>
    private const float LayerScale = 1f, SkyScale = 0.5f;

    public override void _Ready()
    {
        _glow = new DrawLayer(DrawGlow, additive: true);
        _land = new DrawLayer(DrawLand);
        _motesLayer = new DrawLayer(DrawMotes, additive: true);
        AddChild(_glow);
        AddChild(_land);
        AddChild(_motesLayer);
        if (_skyTex == null && _bake == null) Set(Theme, 1);
    }

    public void Set(ArenaTheme theme, int seed)
    {
        Theme = theme;
        _seed = seed;
        var rng = new Rng((ulong)(uint)seed ^ 0xA5A5_5A5AUL);
        _sun = new Vector2(rng.Range(0.15f, 0.85f) * Size.X, rng.Range(0.14f, 0.32f) * Size.Y);
        foreach (var l in _layers) l.Clear();
        _windows.Clear();
        BuildLayer(rng, 0, Size.Y * 0.36f, Size.Y * 0.62f);
        BuildLayer(rng, 1, Size.Y * 0.5f, Size.Y * 0.74f);
        BuildLayer(rng, 2, Size.Y * 0.64f, Size.Y * 0.88f);
        if (theme.Motes is not Motes.Bokeh) BuildCeiling(rng);

        _motes.Clear();
        int n = theme.Motes == Motes.Bokeh ? 26 : 46;
        for (int i = 0; i < n; i++)
            _motes.Add((new Vector2(rng.Range(0f, Size.X), rng.Range(0f, Size.Y)),
                theme.Motes == Motes.Bokeh ? rng.Range(6f, 22f) : rng.Range(1.2f, 3.2f),
                rng.Range(0.4f, 1f), rng.Range(0f, Mathf.Tau), rng.Range(0, 1)));
        // Paint on a worker thread from a snapshot of the shapes; Process swaps the result in and fades it up.
        PaintNoise.Warm();
        var shapes = Array.ConvertAll(_layers, l => l.ToArray());
        var sun = _sun;
        _bake = System.Threading.Tasks.Task.Run(() => Paint(theme, seed, sun, shapes, Size));
        QueueRedrawAll();
    }

    // ---------- shapes ----------

    private void BuildLayer(Rng rng, int layer, float minY, float maxY)
    {
        var into = _layers[layer];
        bool far = layer == 0;
        float w = Size.X, bottom = Size.Y + 2;
        float k = 1 + layer * 0.25f; // nearer layers have bigger shapes
        switch (Theme.Skyline)
        {
            case Skyline.City:
            {
                // Tall, narrow towers with pointed and domed roofs, like a drowned gothic city.
                float x = -10;
                while (x < w)
                {
                    float bw = rng.Range(22f, 44f) * k;
                    float top = rng.Range(minY - (far ? 80 : 40), maxY);
                    into.Add(new[] { new Vector2(x, bottom), new Vector2(x, top), new Vector2(x + bw, top), new Vector2(x + bw, bottom) });
                    float cx = x + bw * 0.5f;
                    if (rng.Chance(0.45f)) // spire
                        into.Add(new[] { new Vector2(x + bw * 0.15f, top + 1), new Vector2(cx, top - rng.Range(16f, 40f) * k), new Vector2(x + bw * 0.85f, top + 1) });
                    else if (rng.Chance(0.5f)) // dome
                        into.Add(Shapes.Ellipse(new Vector2(cx, top + 1), bw * 0.42f, bw * 0.36f, 18));
                    for (float wy = top + 8; wy < bottom - 10; wy += 11 * k)
                        for (float wx = x + 4; wx < x + bw - 6; wx += 7 * k)
                            if (rng.Chance(0.08f))
                                _windows.Add((new Rect2(wx, wy, 2.4f * k, 4.5f * k), rng.Range(0, 1), rng.Range(0f, 50f), layer));
                    x += bw + rng.Range(2f, 18f) * k;
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
                    x += rng.Range(30f, 90f) * k;
                }
                pts.Add(new Vector2(w + 40, bottom));
                into.Add(pts.ToArray());
                break;
            }
            case Skyline.Hills:
            {
                var pts = new List<Vector2> { new(-10, bottom) };
                float p1 = rng.Range(0f, 6f), p2 = rng.Range(0f, 6f), f1 = rng.Range(0.006f, 0.012f), f2 = rng.Range(0.015f, 0.03f);
                var tops = new List<Vector2>();
                for (float x = -10; x <= w + 10; x += 6)
                {
                    float h = 0.5f + 0.35f * Mathf.Sin(x * f1 + p1) + 0.15f * Mathf.Sin(x * f2 + p2);
                    var p = new Vector2(x, Mathf.Lerp(maxY, minY, h));
                    pts.Add(p);
                    tops.Add(p);
                }
                pts.Add(new Vector2(w + 10, bottom));
                into.Add(pts.ToArray());
                // Spindly trees with round crowns along the ridge.
                for (int i = 0; i < 9 + layer * 3; i++)
                {
                    var b = tops[rng.Range(0, tops.Count - 1)];
                    float h = rng.Range(18f, 42f) * k, lean = rng.Range(-5f, 5f);
                    var crown = b + new Vector2(lean, -h);
                    into.Add(new[] { b + new Vector2(-2.4f * k, 6), crown + new Vector2(-1.2f, 2), crown + new Vector2(1.2f, 2), b + new Vector2(2.4f * k, 6) });
                    // A clump of three overlapping blobs for the crown.
                    float cr = rng.Range(6f, 10f) * k;
                    into.Add(Shapes.Ellipse(crown, cr, cr * 0.8f, 18));
                    into.Add(Shapes.Ellipse(crown + new Vector2(-cr * 0.7f, cr * 0.35f), cr * 0.7f, cr * 0.6f, 16));
                    into.Add(Shapes.Ellipse(crown + new Vector2(cr * 0.7f, cr * 0.3f), cr * 0.7f, cr * 0.6f, 16));
                }
                break;
            }
            case Skyline.Spires:
            {
                float x = -20;
                while (x < w + 20)
                {
                    float sw = rng.Range(14f, 34f) * k;
                    float top = rng.Range(minY - 40, maxY);
                    float tip = x + sw * rng.Range(0.35f, 0.65f);
                    // A slightly curved thorn, with a crook partway up.
                    into.Add(new[]
                    {
                        new Vector2(x, bottom), new Vector2(x + sw * 0.18f, Mathf.Lerp(bottom, top, 0.55f)),
                        new Vector2(tip + rng.Range(-3f, 3f), top), new Vector2(x + sw * 0.82f, Mathf.Lerp(bottom, top, 0.5f)),
                        new Vector2(x + sw, bottom),
                    });
                    x += sw * rng.Range(0.45f, 0.9f);
                }
                break;
            }
        }
    }

    /// <summary>A cavern roof with hanging rock for underground themes, on the nearest layer.</summary>
    private void BuildCeiling(Rng rng)
    {
        var pts = new List<Vector2> { new(-10, -10) };
        float x = -10;
        while (x < Size.X + 20)
        {
            pts.Add(new Vector2(x, rng.Range(4f, 22f)));
            if (rng.Chance(0.3f)) // stalactite
            {
                float wd = rng.Range(6f, 16f);
                pts.Add(new Vector2(x + wd * 0.5f, rng.Range(40f, 90f)));
                x += wd;
                pts.Add(new Vector2(x, rng.Range(8f, 22f)));
            }
            x += rng.Range(14f, 40f);
        }
        pts.Add(new Vector2(Size.X + 20, -10));
        _layers[2].Add(pts.ToArray());
    }

    // ---------- painting ----------

    /// <summary>Layer colors far to near: pale and close to the fog far away, dark up close.</summary>
    private static Color LayerColor(ArenaTheme t, int layer)
    {
        return layer switch
        {
            0 => t.SkyBottom.Lerp(t.SkyMid, 0.35f),
            1 => t.SkyMid.Lerp(t.SkyBottom, 0.25f),
            _ => t.SkyMid.Lerp(t.SkyTop, 0.4f),
        };
    }

    private static Color FogOf(ArenaTheme t) => t.SkyBottom.Lerp(t.SkyMid, 0.45f);

    private static Painted Paint(ArenaTheme t, int seed, Vector2 sun, Vector2[][][] shapes, Vector2 size)
    {
        float ox = NoiseTile.Hash(seed, 9, 1) * 256, oy = NoiseTile.Hash(seed, 10, 1) * 256;

        // Sky: a three-stop gradient with soft drifts of cloud and a haze around the light.
        var sky = new Raster(size.X, size.Y, SkyScale);
        for (int y = 0; y < sky.H; y++)
        for (int x = 0; x < sky.W; x++)
        {
            float wx = x / SkyScale, wy = y / SkyScale, v = wy / size.Y;
            var c = v < 0.5f ? t.SkyTop.Lerp(t.SkyMid, v / 0.5f) : t.SkyMid.Lerp(t.SkyBottom, (v - 0.5f) / 0.5f);
            c = c.Lerp(t.SkyBottom, 0.15f);
            float cloud = PaintNoise.Fbm.At(wx * 0.18f + ox, wy * 0.55f + oy);
            c = c.Lerp(t.SkyBottom.Lerp(t.MoteA, 0.15f), Mathf.SmoothStep(0.55f, 0.95f, cloud) * 0.3f * (1 - v * 0.5f));
            c = c.Darkened(Mathf.SmoothStep(0.5f, 0.1f, cloud) * 0.12f);
            float haze = 1 - Mathf.SmoothStep(0, 340, new Vector2(wx, wy).DistanceTo(sun));
            c = c.Lerp(t.SkyBottom.Lerp(t.MoteA, 0.3f), haze * haze * 0.35f);
            sky.Set(y * sky.W + x, c with { A = 1 });
        }

        // Silhouette layers: shapes with crumbly, noise-eaten edges, lit along their tops and sinking into fog.
        var fog = FogOf(t);
        var layers = new Raster[3];
        System.Threading.Tasks.Parallel.For(0, 3, layer =>
        {
            var r = new Raster(size.X, size.Y, LayerScale);
            var mask = r.NewMask();
            foreach (var poly in shapes[layer]) r.FillPolygon(mask, poly);
            r.Blur(mask, 2);
            float rough = 0.45f - layer * 0.08f;
            for (int i = 0; i < mask.Length; i++)
            {
                float wx = i % r.W / LayerScale, wy = i / r.W / LayerScale;
                float n = PaintNoise.Detail.At(wx * 0.9f + layer * 40, wy * 0.9f) - 0.5f;
                mask[i] = Mathf.SmoothStep(0.42f, 0.58f, mask[i] + n * rough);
            }
            var top = r.DepthFromTop(mask);
            var col = LayerColor(t, layer);
            var rim = col.Lerp(t.MoteA, 0.35f).Lightened(0.1f);
            for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                int i = y * r.W + x;
                if (mask[i] <= 0) continue;
                float wx = x / LayerScale, wy = y / LayerScale, v = wy / size.Y;
                float n = PaintNoise.Fbm.At(wx * 0.6f + ox + layer * 70, wy * 0.6f + oy);
                var c = col.Darkened((n - 0.5f) * 0.25f);
                // Long brush strokes running down the shapes.
                c = c.Darkened((PaintNoise.Detail.At(wx * 0.22f + layer * 30, wy * 1.6f) - 0.5f) * 0.2f);
                // Fog thickens toward each layer's foot.
                c = c.Lerp(fog, Mathf.SmoothStep(0.55f, 1.05f, v) * (0.55f - layer * 0.12f));
                c = c.Lerp(rim, (1 - Mathf.SmoothStep(0.5f, 2.5f, top[i] / LayerScale)) * (0.5f - layer * 0.12f));
                r.Set(i, c with { A = mask[i] });
            }
            layers[layer] = r;
        });

        // Fog: a tileable strip of soft streaks, darker at the edges, drawn drifting between the layers.
        var f = new Raster(size.X, 140, 0.5f);
        for (int y = 0; y < f.H; y++)
        for (int x = 0; x < f.W; x++)
        {
            float u = (float)x / f.W, v = (float)y / f.H;
            // Sample around a circle so the strip wraps seamlessly in x.
            float a = u * Mathf.Tau;
            float n = PaintNoise.Fbm.At(40 + Mathf.Cos(a) * 38 + ox, 40 + Mathf.Sin(a) * 38 + v * 30);
            float bell = Mathf.Sin(v * Mathf.Pi);
            f.Set(y * f.W + x, new Color(1, 1, 1, Mathf.SmoothStep(0.3f, 0.85f, n) * bell * bell));
        }
        return new Painted(sky, layers, f);
    }

    private void QueueRedrawAll()
    {
        QueueRedraw();
        _glow?.QueueRedraw();
        _land?.QueueRedraw();
        _motesLayer?.QueueRedraw();
    }

    /// <summary>Swap in a finished painting, keeping the old one to fade from.</summary>
    private void TakeBake(float dt)
    {
        if (_bake is { IsCompleted: true } done)
        {
            _bake = null;
            if (done.IsCompletedSuccessfully)
            {
                var p = done.Result;
                (_oldSky, _oldLayers) = (_skyTex, _layerTex);
                _skyTex = p.Sky.ToTexture();
                _layerTex = Array.ConvertAll(p.Layers, r => r.ToTexture());
                _fogTex ??= p.Fog.ToTexture();
                _fade = _oldSky == null ? 1 : 0;
                QueueRedraw();
            }
            else GD.PushError($"Painting the backdrop failed: {done.Exception?.GetBaseException().Message}");
        }
        if (_fade >= 1) return;
        _fade = Mathf.Min(1, _fade + dt * 3);
        QueueRedraw();
        if (_fade >= 1) (_oldSky, _oldLayers) = (null, null);
    }

    public override void _Process(double delta)
    {
        TakeBake((float)delta);
        if (!IsVisibleInTree()) return;
        _t += (float)delta;
        _glow.QueueRedraw();
        _land.QueueRedraw();
        _motesLayer.QueueRedraw();
    }

    public override void _Draw()
    {
        var full = new Rect2(Vector2.Zero, Size);
        if (_fade < 1 && _oldSky != null) DrawTextureRect(_oldSky, full, false);
        if (_skyTex != null) DrawTextureRect(_skyTex, full, false, new Color(1, 1, 1, _fade));
    }

    private void DrawGlow(DrawLayer l)
    {
        var t = Theme;
        float pulse = 1f + 0.04f * Mathf.Sin(_t * 0.8f);
        // A hazy light rather than a crisp disc.
        GlowTex.Draw(l, _sun, 220 * pulse, t.SkyBottom.Lightened(0.15f) with { A = 0.22f });
        GlowTex.Draw(l, _sun, 80 * pulse, t.MoteA with { A = 0.35f });
        GlowTex.Draw(l, _sun, 26 * pulse, t.MoteA.Lightened(0.6f) with { A = 0.6f });
        // Slow, wide light shafts.
        for (int i = 0; i < 6; i++)
        {
            float a = Mathf.Pi * 0.5f + (i - 2.5f) * 0.26f + Mathf.Sin(_t * 0.12f + i * 1.7f) * 0.05f;
            var dir = Vector2.FromAngle(a);
            var side = new Vector2(-dir.Y, dir.X) * (30 + 12 * Mathf.Sin(i * 2.3f));
            var far = _sun + dir * 620;
            float k = 0.05f + 0.025f * Mathf.Sin(_t * 0.5f + i * 1.3f);
            l.DrawPolygon(new[] { _sun, far + side, far - side },
                new[] { t.MoteA with { A = k }, t.MoteA with { A = 0 }, t.MoteA with { A = 0 } });
        }
    }

    private void DrawLand(DrawLayer l)
    {
        var full = new Rect2(Vector2.Zero, Size);
        var fog = FogOf(Theme);
        for (int layer = 0; layer < 3; layer++)
        {
            if (_fade < 1 && _oldLayers?[layer] != null) l.DrawTextureRect(_oldLayers[layer], full, false, new Color(1, 1, 1, 1 - _fade));
            if (_layerTex[layer] != null) l.DrawTextureRect(_layerTex[layer], full, false, new Color(1, 1, 1, _fade));
            DrawWindows(l, layer);
            // A fog bank in front of each layer, drifting at its own pace.
            float y = Size.Y * (0.5f + layer * 0.16f);
            DrawFog(l, y, 7f + layer * 5, fog with { A = 0.45f - layer * 0.1f });
        }
        // Haze at the bottom of the scene, behind the arena.
        l.DrawPolygon(new[] { new Vector2(0, Size.Y * 0.7f), new Vector2(Size.X, Size.Y * 0.7f), Size, new Vector2(0, Size.Y) },
            new[] { fog with { A = 0 }, fog with { A = 0 }, fog with { A = 0.35f }, fog with { A = 0.35f } });
    }

    private void DrawFog(DrawLayer l, float y, float speed, Color c)
    {
        if (_fogTex == null) return;
        float x = -Mathf.PosMod(_t * speed, Size.X);
        var size = new Vector2(Size.X, 140);
        l.DrawTextureRect(_fogTex, new Rect2(new Vector2(x, y - 70), size), false, c);
        l.DrawTextureRect(_fogTex, new Rect2(new Vector2(x + Size.X, y - 70), size), false, c);
    }

    private void DrawWindows(DrawLayer l, int layer)
    {
        foreach (var (r, kind, phase, at) in _windows)
        {
            if (at != layer || Mathf.Sin(phase) < -0.85f) continue; // a few windows stay dark
            var c = kind == 0 ? Theme.MoteA : Theme.MoteB;
            float a = (0.25f + layer * 0.12f) * (0.85f + 0.15f * Mathf.Sin(_t * 0.7f + phase));
            l.DrawColoredPolygon(Shapes.RoundRect(r, r.Size.X * 0.5f, 3), c with { A = a });
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
                    GlowTex.Draw(l, p, r * 5, c with { A = 0.3f * a });
                    l.DrawCircle(p, r * 0.6f, Shapes.Hdr(c, 1.5f) with { A = a }, true, -1, true);
                    break;
                case Motes.Bubbles:
                    p = Wrap(p0 + new Vector2(Mathf.Sin(_t * speed * 1.5f + phase) * 6, -_t * 22 * speed));
                    l.DrawArc(p, r * 1.6f, 0, Mathf.Tau, 16, c with { A = 0.3f }, 0.8f, true);
                    l.DrawCircle(p + new Vector2(-r * 0.5f, -r * 0.5f), r * 0.35f, c with { A = 0.45f }, true, -1, true);
                    break;
                case Motes.Snow:
                    p = Wrap(p0 + new Vector2(Mathf.Sin(_t * speed + phase) * 18 + _t * 8, _t * 24 * speed));
                    GlowTex.Draw(l, p, r * 1.6f, c with { A = 0.55f });
                    break;
                case Motes.Fireflies:
                    p = Wrap(p0 + new Vector2(Mathf.Sin(_t * 0.6f * speed + phase) * 30, Mathf.Cos(_t * 0.45f * speed + phase * 2) * 20));
                    a = Mathf.Max(0, Mathf.Sin(_t * 2 * speed + phase));
                    GlowTex.Draw(l, p, r * 6, c with { A = 0.35f * a });
                    l.DrawCircle(p, r * 0.6f, Shapes.Hdr(c, 1.4f) with { A = a }, true, -1, true);
                    break;
                default:
                    // Drifting dust: soft specks catching the light.
                    p = Wrap(p0 + new Vector2(_t * 6 * speed, Mathf.Sin(_t * 0.3f + phase) * 10));
                    a = 0.5f + 0.5f * Mathf.Sin(_t * 0.7f * speed + phase);
                    GlowTex.Draw(l, p, r * 0.35f + 2, c with { A = 0.18f + 0.25f * a });
                    break;
            }
        }
    }

    private Vector2 Wrap(Vector2 p) => new(Mathf.PosMod(p.X + 40, Size.X + 80) - 40, Mathf.PosMod(p.Y + 40, Size.Y + 80) - 40);
}
