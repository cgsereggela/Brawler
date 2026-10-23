using System.Collections.Generic;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// A small picture of an arena for the map lists: the theme's sky, the ground and platforms in its stone, and dots for
/// pads, hazards, saws, movers and spawns. Painted once on the CPU (see <see cref="Raster"/>) and kept per map code,
/// so a list of them is one textured rect each.
/// </summary>
public static class MapThumb
{
    /// <summary>Size in UI pixels; the texture has twice the pixels so it stays sharp when the window is scaled up.</summary>
    public static readonly Vector2 Size = new(64, 36);
    private const float Scale = 2 * 64f / MapLayout.Width;

    private static readonly Dictionary<string, ImageTexture> Cache = new();

    /// <summary>A thumbnail control for the arena, sized for a list row.</summary>
    public static TextureRect For(MapDesign d, string code = null)
    {
        var rect = new TextureRect
        {
            Texture = Texture(d, code),
            CustomMinimumSize = Size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        return rect;
    }

    public static ImageTexture Texture(MapDesign d, string code = null)
    {
        code ??= MapCode.Encode(d);
        if (Cache.TryGetValue(code, out var tex)) return tex;
        tex = Paint(d);
        if (Cache.Count > 200) Cache.Clear();
        Cache[code] = tex;
        return tex;
    }

    private static ImageTexture Paint(MapDesign d)
    {
        var map = d.Build(2);
        var t = ArenaTheme.Of(d.Theme);
        var r = new Raster(MapLayout.Width, MapLayout.Height, Scale);

        // Sky: top to middle to bottom, as the backdrop's gradient.
        for (int y = 0; y < r.H; y++)
        {
            float k = (float)y / (r.H - 1);
            var c = k < 0.55f ? t.SkyTop.Lerp(t.SkyMid, k / 0.55f) : t.SkyMid.Lerp(t.SkyBottom, (k - 0.55f) / 0.45f);
            for (int x = 0; x < r.W; x++) r.Set(y * r.W + x, c);
        }

        // Lanes and tracks first, so the ground and platforms sit over them.
        var faint = r.NewMask();
        foreach (var m in map.Movers) r.FillRect(faint, ToRect(m.Lane));
        Fill(r, faint, t.Edge with { A = 0.12f });

        var ground = r.NewMask();
        var tops = r.NewMask();
        foreach (var s in map.Solids)
        {
            r.FillRect(ground, ToRect(s));
            r.FillRect(tops, new Rect2(s.X, s.Y, s.W, 7));
        }
        foreach (var g in map.Ground)
        {
            if (g.Length < 2) continue;
            var pts = new List<Vector2> { new(g[0].X, MapLayout.Height) };
            foreach (var p in g) pts.Add(new Vector2(p.X, p.Y));
            pts.Add(new Vector2(g[^1].X, MapLayout.Height));
            r.FillPolygon(ground, pts);
            var top = new List<Vector2>();
            foreach (var p in g) top.Add(new Vector2(p.X, p.Y));
            for (int i = g.Length - 1; i >= 0; i--) top.Add(new Vector2(g[i].X, g[i].Y + 7));
            r.FillPolygon(tops, top);
        }
        foreach (var m in map.Movers)
        {
            r.FillRect(ground, new Rect2(m.X1, m.Y, m.W, m.H));
            r.FillRect(tops, new Rect2(m.X1, m.Y, m.W, 7));
        }
        foreach (var dr in map.Drifts)
        {
            r.FillRect(ground, ToRect(dr.Rect));
            r.FillRect(tops, new Rect2(dr.X, dr.Y, dr.W, 7));
        }
        Fill(r, ground, t.Block.Lightened(0.12f));
        for (int i = 0; i < tops.Length; i++) tops[i] *= ground[i];
        Fill(r, tops, t.Edge with { A = 0.85f });

        var glass = r.NewMask();
        foreach (var p in map.Props) r.FillRect(glass, ToRect(p.Rect));
        Fill(r, glass, new Color(0.75f, 0.92f, 1f, 0.45f));

        foreach (var h in map.Hazards)
            Mark(r, ToRect(h.Rect).Grow(3), h.Kind == HazardKind.Magma ? new Color("#ff7a3c") : new Color("#9cf05a"));
        foreach (var p in map.Pads) Mark(r, new Rect2(p.X1, p.Y - 8, p.X2 - p.X1, 10), Palette.Accent);
        foreach (var s in map.Saws) Dot(r, new Vector2((s.X1 + s.X2) / 2f, (s.Y1 + s.Y2) / 2f), s.Radius + 4, new Color("#e8e2d8"));
        for (int i = 0; i < map.Spawns.Count && i < d.Spawns.Count; i++) Dot(r, new Vector2(d.Spawns[i].X, d.Spawns[i].Y - 12), 12, Palette.Player(i));
        return r.ToTexture();
    }

    private static Rect2 ToRect(RectI r) => new(r.X, r.Y, r.W, r.H);

    private static void Fill(Raster r, float[] mask, Color c)
    {
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] > 0) r.Blend(i, c, mask[i]);
    }

    private static void Mark(Raster r, Rect2 rect, Color c)
    {
        var m = r.NewMask();
        r.FillRect(m, rect);
        Fill(r, m, c);
    }

    private static void Dot(Raster r, Vector2 at, float radius, Color c)
    {
        var m = r.NewMask();
        r.FillEllipse(m, at, radius, radius);
        Fill(r, m, c);
    }
}
