using System;
using System.Collections.Generic;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// A square, seamlessly tiling field of fractal value noise (0..1), sampled with wrap-around bilinear filtering.
/// The painters read a few of these instead of evaluating noise per pixel, which keeps a full-arena bake cheap.
/// </summary>
public sealed class NoiseTile
{
    public const int N = 256;
    private readonly float[] _v = new float[N * N];

    /// <param name="freq">lattice cells across the tile for the first octave</param>
    /// <param name="ridged">fold each octave around its middle, giving thin vein-like valleys (cracks, strata)</param>
    public NoiseTile(int seed, int freq, int octaves, float gain = 0.5f, bool ridged = false)
    {
        float amp = 1, total = 0;
        for (int o = 0; o < octaves; o++)
        {
            int p = freq << o;
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float fx = (float)x * p / N, fy = (float)y * p / N;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = Smooth(fx - x0), ty = Smooth(fy - y0);
                int x1 = (x0 + 1) % p, y1 = (y0 + 1) % p;
                float a = Hash(x0, y0, seed + o), b = Hash(x1, y0, seed + o), c = Hash(x0, y1, seed + o), d = Hash(x1, y1, seed + o);
                float v = Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
                if (ridged) v = 1 - Mathf.Abs(v * 2 - 1);
                _v[y * N + x] += v * amp;
            }
            total += amp;
            amp *= gain;
        }
        // Stretch to the full 0..1 range so thresholds mean the same thing for every tile.
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in _v) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        for (int i = 0; i < _v.Length; i++) _v[i] = (_v[i] - lo) / Math.Max(1e-5f, hi - lo);
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    public static float Hash(int x, int y, int s)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + s * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    /// <summary>Sample at (x, y) in tile pixels; wraps, so any coordinate is fine.</summary>
    public float At(float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float tx = x - x0, ty = y - y0;
        x0 &= N - 1;
        y0 &= N - 1;
        int x1 = (x0 + 1) & (N - 1), y1 = (y0 + 1) & (N - 1);
        float a = _v[y0 * N + x0], b = _v[y0 * N + x1], c = _v[y1 * N + x0], d = _v[y1 * N + x1];
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    /// <summary>This tile as a repeating grayscale texture (for shaders: paper grain and brush texture).</summary>
    public ImageTexture ToTexture()
    {
        var bytes = new byte[N * N];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Clamp(_v[i] * 255f, 0, 255);
        return ImageTexture.CreateFromImage(Image.CreateFromData(N, N, false, Image.Format.L8, bytes));
    }
}

/// <summary>The shared noise tiles, built once on first use.</summary>
public static class PaintNoise
{
    private static NoiseTile _fbm, _detail, _ridge, _grain;
    private static ImageTexture _grainTex;

    /// <summary>Broad cloudy blotches (4 cells across, 5 octaves).</summary>
    public static NoiseTile Fbm => _fbm ??= new NoiseTile(11, 4, 5);
    /// <summary>Medium mottling for stone and paint texture.</summary>
    public static NoiseTile Detail => _detail ??= new NoiseTile(23, 16, 3, 0.55f);
    /// <summary>Ridged noise: values near 1 trace thin connected veins.</summary>
    public static NoiseTile Ridge => _ridge ??= new NoiseTile(37, 6, 4, 0.5f, ridged: true);
    /// <summary>Fine grain, like canvas or rough paper.</summary>
    public static NoiseTile Grain => _grain ??= new NoiseTile(53, 64, 2, 0.6f);

    /// <summary>Build every tile now, before painting on several threads at once.</summary>
    public static void Warm() { _ = Fbm; _ = Detail; _ = Ridge; _ = Grain; }

    public static ImageTexture GrainTexture => _grainTex ??= Grain.ToTexture();
}

/// <summary>
/// A small CPU raster canvas for procedural painting: antialiased polygon masks, distance fields, blur and
/// premultiplied-free RGBA float pixels that become an <see cref="ImageTexture"/>. Coordinates passed in are world
/// pixels; <see cref="Scale"/> sets how many raster pixels each world pixel gets.
/// </summary>
public sealed class Raster
{
    public readonly int W, H;
    public readonly float Scale;
    public readonly float[] R, G, B, A;

    public Raster(float worldW, float worldH, float scale)
    {
        Scale = scale;
        W = Math.Max(1, (int)MathF.Ceiling(worldW * scale));
        H = Math.Max(1, (int)MathF.Ceiling(worldH * scale));
        R = new float[W * H];
        G = new float[W * H];
        B = new float[W * H];
        A = new float[W * H];
    }

    /// <summary>Blend a color over pixel i with coverage k (0..1), "over" compositing on straight alpha.</summary>
    public void Blend(int i, Color c, float k)
    {
        float a = c.A * Math.Clamp(k, 0, 1);
        if (a <= 0) return;
        float outA = a + A[i] * (1 - a);
        if (outA <= 1e-5f) return;
        float wNew = a / outA, wOld = 1 - wNew;
        R[i] = c.R * wNew + R[i] * wOld;
        G[i] = c.G * wNew + G[i] * wOld;
        B[i] = c.B * wNew + B[i] * wOld;
        A[i] = outA;
    }

    public void Set(int i, Color c)
    {
        R[i] = c.R;
        G[i] = c.G;
        B[i] = c.B;
        A[i] = c.A;
    }

    public ImageTexture ToTexture()
    {
        var bytes = new byte[W * H * 4];
        for (int i = 0, j = 0; i < R.Length; i++, j += 4)
        {
            bytes[j] = ToByte(R[i]);
            bytes[j + 1] = ToByte(G[i]);
            bytes[j + 2] = ToByte(B[i]);
            bytes[j + 3] = ToByte(A[i]);
        }
        return ImageTexture.CreateFromImage(Image.CreateFromData(W, H, false, Image.Format.Rgba8, bytes));
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    // ---------- masks ----------

    /// <summary>A coverage mask (0..1 per raster pixel) the size of this raster.</summary>
    public float[] NewMask() => new float[W * H];

    /// <summary>
    /// Add a polygon (world coordinates) to a mask with antialiased edges: four sub-scanlines per row and exact
    /// horizontal coverage at span ends. Overlapping polygons merge (max).
    /// </summary>
    public void FillPolygon(float[] mask, IReadOnlyList<Vector2> pts, float value = 1)
    {
        if (pts.Count < 3) return;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in pts) { minY = Math.Min(minY, p.Y * Scale); maxY = Math.Max(maxY, p.Y * Scale); }
        int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(H - 1, (int)MathF.Ceiling(maxY));
        var row = new float[W + 2];
        var xs = new List<float>(16);
        for (int y = y0; y <= y1; y++)
        {
            Array.Clear(row);
            bool any = false;
            for (int sub = 0; sub < 4; sub++)
            {
                float sy = y + (sub + 0.5f) * 0.25f;
                xs.Clear();
                for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                {
                    float ay = pts[j].Y * Scale, by = pts[i].Y * Scale;
                    if ((ay <= sy) == (by <= sy)) continue;
                    float ax = pts[j].X * Scale, bx = pts[i].X * Scale;
                    xs.Add(ax + (sy - ay) / (by - ay) * (bx - ax));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    float a = Math.Clamp(xs[k], 0, W), b = Math.Clamp(xs[k + 1], 0, W);
                    if (b <= a) continue;
                    any = true;
                    int ia = (int)a, ib = (int)b;
                    if (ia == ib) { row[ia] += (b - a) * 0.25f; continue; }
                    row[ia] += (ia + 1 - a) * 0.25f;
                    for (int x = ia + 1; x < ib; x++) row[x] += 0.25f;
                    if (ib < W) row[ib] += (b - ib) * 0.25f;
                }
            }
            if (!any) continue;
            int o = y * W;
            for (int x = 0; x < W; x++)
                if (row[x] > 0) mask[o + x] = Math.Max(mask[o + x], Math.Min(1, row[x]) * value);
        }
    }

    public void FillRect(float[] mask, Rect2 r, float value = 1)
        => FillPolygon(mask, new[] { r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y) }, value);

    public void FillEllipse(float[] mask, Vector2 c, float rx, float ry, float value = 1)
        => FillPolygon(mask, Shapes.Ellipse(c, rx, ry, 20), value);

    /// <summary>Box blur a mask in place (separable, radius in raster pixels), for soft edges and fog.</summary>
    public void Blur(float[] m, int radius)
    {
        if (radius <= 0) return;
        var tmp = new float[m.Length];
        float inv = 1f / (radius * 2 + 1);
        for (int y = 0; y < H; y++)
        {
            int o = y * W;
            float sum = 0;
            for (int x = -radius; x <= radius; x++) sum += m[o + Math.Clamp(x, 0, W - 1)];
            for (int x = 0; x < W; x++)
            {
                tmp[o + x] = sum * inv;
                sum += m[o + Math.Min(W - 1, x + radius + 1)] - m[o + Math.Max(0, x - radius)];
            }
        }
        for (int x = 0; x < W; x++)
        {
            float sum = 0;
            for (int y = -radius; y <= radius; y++) sum += tmp[Math.Clamp(y, 0, H - 1) * W + x];
            for (int y = 0; y < H; y++)
            {
                m[y * W + x] = sum * inv;
                sum += tmp[Math.Min(H - 1, y + radius + 1) * W + x] - tmp[Math.Max(0, y - radius) * W + x];
            }
        }
    }

    /// <summary>
    /// Approximate Euclidean distance (raster pixels) from every pixel to the nearest pixel on the other side of the
    /// mask's 0.5 level: inside pixels get their depth into the shape, outside pixels their distance from it.
    /// Two-pass 3-4 chamfer, good to a few percent.
    /// </summary>
    public float[] Distance(float[] mask, bool inside)
    {
        const float Big = 1e6f, D1 = 1f, D2 = 1.41421f;
        var d = new float[W * H];
        for (int i = 0; i < d.Length; i++) d[i] = (mask[i] >= 0.5f) == inside ? Big : 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = y * W + x;
            if (d[i] == 0) continue;
            float v = d[i];
            if (x > 0) v = Math.Min(v, d[i - 1] + D1);
            if (y > 0)
            {
                v = Math.Min(v, d[i - W] + D1);
                if (x > 0) v = Math.Min(v, d[i - W - 1] + D2);
                if (x < W - 1) v = Math.Min(v, d[i - W + 1] + D2);
            }
            d[i] = v;
        }
        for (int y = H - 1; y >= 0; y--)
        for (int x = W - 1; x >= 0; x--)
        {
            int i = y * W + x;
            if (d[i] == 0) continue;
            float v = d[i];
            if (x < W - 1) v = Math.Min(v, d[i + 1] + D1);
            if (y < H - 1)
            {
                v = Math.Min(v, d[i + W] + D1);
                if (x < W - 1) v = Math.Min(v, d[i + W + 1] + D2);
                if (x > 0) v = Math.Min(v, d[i + W - 1] + D2);
            }
            d[i] = v;
        }
        // Sub-pixel correction from the antialiased coverage, so edges stay smooth.
        for (int i = 0; i < d.Length; i++)
            if (d[i] > 0 && d[i] < 1.5f) d[i] = Math.Max(0.01f, d[i] - 0.5f + (inside ? mask[i] - 0.5f : 0.5f - mask[i]));
        return d;
    }

    /// <summary>
    /// How far each pixel is below the top of the shape in its column (raster pixels; 0 outside), so surfaces get a
    /// lit top and darken with depth whatever their outline.
    /// </summary>
    public float[] DepthFromTop(float[] mask)
    {
        var d = new float[W * H];
        for (int x = 0; x < W; x++)
        {
            float run = 0;
            for (int y = 0; y < H; y++)
            {
                int i = y * W + x;
                float m = mask[i];
                if (m < 0.5f) { run = 0; continue; }
                run = run == 0 ? 0.5f : run + 1;
                d[i] = run;
            }
        }
        return d;
    }
}
