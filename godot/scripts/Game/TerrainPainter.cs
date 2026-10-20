using System;
using System.Collections.Generic;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Paints an arena's solid ground and platforms into raster textures, in a hand-painted style: mottled stone that is
/// lit along its top and sinks into shadow with depth, cracks and strata, a theme cap (moss, snow, frosting...) and an
/// uneven ink outline. Baked once per battle at <see cref="Scale"/> raster pixels per world pixel; purely cosmetic.
/// </summary>
public static class TerrainPainter
{
    public const float Scale = 2f;

    /// <summary>Ground shapes in world space: platforms and floor pieces, and the arena's border walls.</summary>
    public readonly record struct GroundShapes(List<Vector2[]> Ground, List<Rect2> Border);

    /// <summary>
    /// The painted ground (alpha outside it) and, for themes with glowing veins, an additive glow layer (else null).
    /// Pure CPU work on its inputs, so it can run on a worker thread; call <see cref="PaintNoise.Warm"/> first.
    /// </summary>
    public static (Raster Body, Raster Glow) Bake(GroundShapes shapes, ArenaTheme t, int seed)
    {
        var r = new Raster(MapLayout.Width, MapLayout.Height, Scale);
        var mask = r.NewMask();
        foreach (var poly in shapes.Ground) r.FillPolygon(mask, poly);
        var border = r.NewMask();
        foreach (var b in shapes.Border) r.FillRect(border, b);
        for (int i = 0; i < mask.Length; i++) mask[i] = Math.Max(mask[i], border[i]);

        var din = r.Distance(mask, inside: true);
        var dout = r.Distance(mask, inside: false);
        var top = r.DepthFromTop(mask);

        // Each arena gets its own patch of the noise tiles.
        float ox = NoiseTile.Hash(seed, 1, 5) * 256, oy = NoiseTile.Hash(seed, 2, 5) * 256;
        var ink = Ink(t);
        var lit = t.Block.Lerp(t.Edge, 0.22f).Lightened(0.28f);
        var deep = Ink(t).Lerp(t.Block, 0.35f);
        var rim = t.Block.Lerp(t.Edge, 0.7f).Lightened(0.25f);
        var (capCol, capDepth, capDrip) = Cap(t);
        bool veins = t.Deco == Deco.Cracks;
        Raster glow = veins ? new Raster(MapLayout.Width, MapLayout.Height, Scale) : null;
        float s = Scale;


        System.Threading.Tasks.Parallel.For(0, r.H, y =>
        {
        for (int x = 0; x < r.W; x++)
        {
            int i = y * r.W + x;
            float m = mask[i];
            float wx = x / s + ox, wy = y / s + oy;

            // Ink outline: a stroke of uneven width just outside the shape.
            float width = 1.1f + 0.9f * PaintNoise.Detail.At(wx * 2.5f, wy * 2.5f);
            if (m < 1)
            {
                float d = dout[i] / s;
                if (d < width + 0.6f) r.Blend(i, ink, 1 - Mathf.SmoothStep(width - 0.5f, width + 0.5f, d));
            }
            if (m <= 0) continue;

            float td = top[i] / s, edge = din[i] / s;
            float broad = PaintNoise.Fbm.At(wx * 0.8f, wy * 0.8f);
            float detail = PaintNoise.Detail.At(wx * 1.6f, wy * 1.6f);
            float vein = PaintNoise.Ridge.At(wx * 1.1f, wy * 1.1f);

            // Lit top fading into shadow with depth (wobbly, so the light line isn't ruled).
            float k = Mathf.SmoothStep(1, 30, td + (broad - 0.5f) * 14);
            var col = lit.Lerp(deep, k);
            // Mottled stone and soft strata running with the surface.
            float strata = Mathf.Sin((td + broad * 9) * 0.5f);
            col = col.Darkened(Math.Clamp(0.16f * (0.5f - detail) + (strata > 0.82f ? 0.08f : 0), -0.1f, 0.25f));
            col = col.Lightened(Math.Max(0, broad - 0.62f) * 0.25f);
            // Cracks.
            if (vein > 0.9f && td > 3) col = col.Darkened(0.4f * Mathf.SmoothStep(0.9f, 0.97f, vein));
            if (t.Deco == Deco.Neon && td > 2.5f) col = Bricks(col, x / s, td);
            // Edges sink into shadow; the arena border is darker still, so the playfield reads first.
            col = col.Darkened(0.32f * (1 - Mathf.SmoothStep(0, 5, edge)));
            if (border[i] > 0.5f) col = col.Darkened(0.35f);
            // A thin rim of light along the top, then the theme's cap (moss, snow, frosting) over it.
            col = col.Lerp(rim, 0.85f * (1 - Mathf.SmoothStep(0.8f, 3.6f, td)));
            if (capCol is { } cap && border[i] < 0.5f)
            {
                float depth = capDepth * (0.6f + 0.8f * PaintNoise.Detail.At(wx * 0.9f, 7)) +
                              capDrip * Math.Max(0, PaintNoise.Detail.At(wx * 0.55f, 31) - 0.62f) * 4;
                if (td < depth)
                {
                    var c = cap.Lerp(cap.Darkened(0.35f), td / Math.Max(1, depth)).Lightened(td < 1.2f ? 0.18f : 0);
                    col = col.Lerp(c, 1 - Mathf.SmoothStep(depth - 0.8f, depth, td));
                }
            }
            // Ink also bites a little way inside, so the stroke straddles the edge.
            col = col.Lerp(ink, 1 - Mathf.SmoothStep(0.2f, 0.9f, edge));
            col = col.Darkened((PaintNoise.Grain.At(wx * 3, wy * 3) - 0.5f) * 0.08f);
            r.Blend(i, col with { A = 1 }, m);

            if (glow != null && vein > 0.88f && td > 4 && edge > 2)
            {
                float g = Mathf.SmoothStep(0.88f, 0.96f, vein) * Mathf.SmoothStep(4, 10, td);
                glow.Set(i, t.Edge with { A = g * 0.9f });
            }
        }
        });
        return (r, glow);
    }

    /// <summary>The near-black ink of outlines, tinted toward the theme's night sky.</summary>
    public static Color Ink(ArenaTheme t) => t.SkyTop.Darkened(0.55f) with { A = 1 };

    /// <summary>Theme cap: its color, typical depth and how far it drips (world px). Null for bare rock.</summary>
    private static (Color? Col, float Depth, float Drip) Cap(ArenaTheme t) => t.Deco switch
    {
        Deco.Grass => (new Color("#7d9150"), 2.6f, 2.5f),
        Deco.Mushrooms => (t.Block.Lerp(new Color("#9fbf5a"), 0.6f), 2.8f, 4f),
        Deco.Weeds => (t.Block.Lerp(new Color("#5fa79a"), 0.6f), 2.4f, 3f),
        Deco.Icicles => (new Color("#dce8f4"), 3.2f, 1.5f),
        Deco.Sprinkles => (new Color("#f2cfe0"), 3.4f, 5f),
        Deco.Cracks => (t.Block.Darkened(0.35f), 2f, 0f),
        _ => (null, 0, 0),
    };

    /// <summary>Masonry: courses every 6 px down from the surface, joints staggered per course.</summary>
    private static Color Bricks(Color col, float x, float td)
    {
        float course = (td - 2.5f) / 6f;
        int row = (int)course;
        float fy = course - row;
        float fx = ((x + (row % 2) * 7) / 14f) % 1f;
        bool mortar = fy < 0.12f || fx < 0.06f;
        return mortar ? col.Darkened(0.3f) : col.Lightened((NoiseTile.Hash((int)((x + (row % 2) * 7) / 14f), row, 3) - 0.5f) * 0.12f);
    }
}
