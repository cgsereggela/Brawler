using System;
using System.Collections.Generic;
using Godot;

namespace Brawler.Fx;

/// <summary>Smooth vector shape builders. Every shape is a polygon, so it scales cleanly and gets 2D MSAA.</summary>
public static class Shapes
{
    private static readonly Dictionary<(int, int), Vector2[]> UnitCircles = new();

    /// <summary>Unit circle points, cached per segment count.</summary>
    private static Vector2[] Unit(int segments)
    {
        if (UnitCircles.TryGetValue((segments, 0), out var pts)) return pts;
        pts = new Vector2[segments];
        for (int i = 0; i < segments; i++) pts[i] = Vector2.FromAngle(i * Mathf.Tau / segments);
        UnitCircles[(segments, 0)] = pts;
        return pts;
    }

    public static Vector2[] Ellipse(Vector2 c, float rx, float ry, int segments = 24)
    {
        var u = Unit(segments);
        var pts = new Vector2[segments];
        for (int i = 0; i < segments; i++) pts[i] = c + new Vector2(u[i].X * rx, u[i].Y * ry);
        return pts;
    }

    public static Vector2[] RoundRect(Rect2 r, float radius, int cornerSegments = 5)
    {
        // Keep corners from meeting: a radius of exactly half the size would create duplicate points, which
        // breaks triangulation.
        radius = Mathf.Min(radius, Mathf.Min(r.Size.X, r.Size.Y) * 0.5f - 0.02f);
        if (radius <= 0.05f) return new[] { r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y) };
        var pts = new Vector2[cornerSegments * 4 + 4];
        var centers = new[]
        {
            new Vector2(r.End.X - radius, r.Position.Y + radius),
            new Vector2(r.End.X - radius, r.End.Y - radius),
            new Vector2(r.Position.X + radius, r.End.Y - radius),
            new Vector2(r.Position.X + radius, r.Position.Y + radius),
        };
        int k = 0;
        for (int c = 0; c < 4; c++)
        {
            float a0 = -Mathf.Pi / 2 + c * Mathf.Pi / 2;
            for (int i = 0; i <= cornerSegments; i++)
                pts[k++] = centers[c] + Vector2.FromAngle(a0 + i * (Mathf.Pi / 2) / cornerSegments) * radius;
        }
        return pts;
    }

    /// <summary>A pill between two points.</summary>
    public static Vector2[] Capsule(Vector2 a, Vector2 b, float radius, int segments = 8)
    {
        var dir = (b - a).Normalized();
        if (dir == Vector2.Zero) dir = Vector2.Right;
        float ang = dir.Angle();
        var pts = new Vector2[(segments + 1) * 2];
        for (int i = 0; i <= segments; i++)
        {
            pts[i] = b + Vector2.FromAngle(ang - Mathf.Pi / 2 + i * Mathf.Pi / segments) * radius;
            pts[segments + 1 + i] = a + Vector2.FromAngle(ang + Mathf.Pi / 2 + i * Mathf.Pi / segments) * radius;
        }
        return pts;
    }

    public static Vector2[] Star(Vector2 c, float outer, float inner, int points, float rotation = 0)
    {
        var pts = new Vector2[points * 2];
        for (int i = 0; i < points * 2; i++)
            pts[i] = c + Vector2.FromAngle(rotation + i * Mathf.Pi / points) * (i % 2 == 0 ? outer : inner);
        return pts;
    }

    public static Vector2[] Offset(Vector2[] pts, Vector2 by)
    {
        var o = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++) o[i] = pts[i] + by;
        return o;
    }

    /// <summary>Fill a polygon with an outline drawn as a closed antialiased line.</summary>
    public static void Fill(CanvasItem ci, Vector2[] pts, Color fill, Color? outline = null, float width = 1f)
    {
        if (outline is { } oc)
        {
            ci.DrawColoredPolygon(pts, oc);
            DrawClosed(ci, pts, oc, width * 2f);
        }
        ci.DrawColoredPolygon(pts, fill);
    }

    public static void DrawClosed(CanvasItem ci, Vector2[] pts, Color color, float width)
    {
        var loop = new Vector2[pts.Length + 1];
        Array.Copy(pts, loop, pts.Length);
        loop[^1] = pts[0];
        ci.DrawPolyline(loop, color, width, true);
    }

    /// <summary>Vertical gradient fill: colors are interpolated by y between the polygon's top and bottom.</summary>
    public static void Gradient(CanvasItem ci, Vector2[] pts, Color top, Color bottom)
    {
        float y0 = float.MaxValue, y1 = float.MinValue;
        foreach (var p in pts) { y0 = Mathf.Min(y0, p.Y); y1 = Mathf.Max(y1, p.Y); }
        var cols = new Color[pts.Length];
        for (int i = 0; i < pts.Length; i++) cols[i] = top.Lerp(bottom, y1 > y0 ? (pts[i].Y - y0) / (y1 - y0) : 0);
        ci.DrawPolygon(pts, cols);
    }

    /// <summary>Same color with its RGB pushed above 1 so the HDR glow picks it up.</summary>
    public static Color Hdr(Color c, float boost) => new(c.R * boost, c.G * boost, c.B * boost, c.A);
}

/// <summary>A soft radial glow texture, drawn tinted (and usually additively) for lights, bokeh and halos.</summary>
public static class GlowTex
{
    private static Texture2D _tex;

    public static Texture2D Tex => _tex ??= Build();

    private static Texture2D Build()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 1));
        g.SetColor(1, new Color(1, 1, 1, 0));
        g.AddPoint(0.35f, new Color(1, 1, 1, 0.45f));
        return new GradientTexture2D
        {
            Gradient = g, Width = 64, Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
        };
    }

    public static void Draw(CanvasItem ci, Vector2 center, float radius, Color color)
        => ci.DrawTextureRect(Tex, new Rect2(center - new Vector2(radius, radius), new Vector2(radius, radius) * 2), false, color);
}

/// <summary>The UI and in-world fonts: a clean system sans (Bahnschrift on Windows) with sensible fallbacks.</summary>
public static class Fonts
{
    private static readonly string[] Names = { "Bahnschrift", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "Sans-Serif" };
    private static Font _regular, _bold, _heavy, _shout;

    public static Font Regular => _regular ??= Make(400, 0);
    public static Font Bold => _bold ??= Make(700, 0.4f);
    public static Font Heavy => _heavy ??= Make(800, 0.9f);

    /// <summary>
    /// Heavy with a little air between letters, for big popup words. Emboldening widens every glyph, so at popup sizes
    /// neighbours touch (the E and T in DELETED).
    /// </summary>
    public static Font Shout => _shout ??= MakeShout();

    private static Font MakeShout()
    {
        var v = Make(800, 0.9f);
        v.OpentypeFeatures = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("kern"), 0 } };
        v.SetSpacing(TextServer.SpacingType.Glyph, 2);
        return v;
    }

    private static FontVariation Make(int weight, float embolden)
    {
        var sys = new SystemFont
        {
            FontNames = Names,
            FontWeight = weight,
            Antialiasing = TextServer.FontAntialiasing.Gray,
            Hinting = TextServer.Hinting.Light,
            SubpixelPositioning = TextServer.SubpixelPositioning.Auto,
        };
        // Bahnschrift is a variable font: ask for the weight directly, and embolden a little in case the
        // fallback font has no bold face.
        var v = new FontVariation { BaseFont = sys, VariationEmbolden = embolden };
        v.VariationOpentype = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("weight"), weight } };
        return v;
    }
}

/// <summary>A node that draws through a callback, optionally with additive blending (for glows).</summary>
public partial class DrawLayer : Node2D
{
    public Action<DrawLayer> OnDraw;

    public DrawLayer() { }

    public DrawLayer(Action<DrawLayer> onDraw, bool additive = false)
    {
        OnDraw = onDraw;
        if (additive) Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
    }

    public override void _Draw() => OnDraw?.Invoke(this);
}
