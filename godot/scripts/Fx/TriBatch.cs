using System;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// Collects many small shapes as triangles and draws them with one RenderingServer call per few thousand vertices,
/// instead of one engine call per shape (each costs far more than the triangles it draws). Shapes are collected in
/// fixed-size arrays; a flush copies them into reused arrays a size up from what was drawn (unused index slots are
/// degenerate triangles), so it allocates nothing and Godot only copies about what was drawn. Shapes keep the order they
/// were added in. Drawn without line feathering; the project's 2D MSAA smooths the edges.
/// </summary>
public sealed class TriBatch
{
    private const int MaxVerts = 4096, MaxIndices = MaxVerts * 3;

    private readonly Vector2[] _pts = new Vector2[MaxVerts];
    private readonly Color[] _cols = new Color[MaxVerts];
    private readonly Vector2[] _uvs;
    private readonly int[] _idx = new int[MaxIndices];
    private int _n, _ni;
    private CanvasItem _ci;
    private Rid _tex;
    private Transform2D _xform = Transform2D.Identity;
    private bool _xf;
    private float _xx = 1, _xy, _yx, _yy = 1, _ox, _oy; // _xform's numbers, for V

    // Scratch for triangulating and outlining one polygon at a time (polygons longer than this are skipped).
    private const int MaxPoly = 256;
    private static readonly int[] Clip = new int[MaxPoly];
    private static readonly int[] Tris = new int[(MaxPoly - 2) * 3];
    private static readonly Vector2[] Normals = new Vector2[MaxPoly];
    private static readonly Vector2[] Edges = new Vector2[MaxPoly];
    private static readonly Color[] Shades = new Color[MaxPoly];

    // What a flush hands Godot, per size bucket: 64 << k vertices and 192 << k indices, k = 0..6 (up to the max).
    private const int Buckets = 7;
    private readonly Vector2[][] _outPts = new Vector2[Buckets][], _outUvs = new Vector2[Buckets][];
    private readonly Color[][] _outCols = new Color[Buckets][];
    private readonly int[][] _outIdx = new int[Buckets][];
    private readonly int[] _outUsed = new int[Buckets]; // indices written into each bucket last time

    /// <param name="textured">Every shape samples a texture (given to <see cref="Begin"/>) over its 0..1 UV square.</param>
    public TriBatch(bool textured = false)
    {
        if (textured) _uvs = new Vector2[MaxVerts];
    }

    /// <summary>Starts a frame's drawing into <paramref name="ci"/> (call from its _Draw).</summary>
    public void Begin(CanvasItem ci, Texture2D texture = null)
    {
        _ci = ci;
        _tex = texture?.GetRid() ?? default;
        _n = _ni = 0;
        Transform = Transform2D.Identity;
        Feather = 0;
        SoftLines = false;
    }

    /// <summary>
    /// Width of the soft edge on circles, like the one Godot gives antialiased circles (in the same units as the
    /// points, so it scales with <see cref="Transform"/>). 0 = hard edges, left to MSAA.
    /// </summary>
    public float Feather;

    /// <summary>
    /// Draw lines, strokes and outlines the way Godot's antialiased lines look: solid across about half their width
    /// and fading out to nearly twice it (measured from screenshots of the same art drawn both ways). Off = hard edges.
    /// </summary>
    public bool SoftLines;

    private const float SoftCore = 0.55f, SoftReach = 1.8f;

    /// <summary>Applied to every point added from now on, like <see cref="CanvasItem.DrawSetTransformMatrix"/>.</summary>
    public Transform2D Transform
    {
        get => _xform;
        set
        {
            _xform = value;
            _xf = value != Transform2D.Identity;
            (_xx, _xy, _yx, _yy, _ox, _oy) = (value.X.X, value.X.Y, value.Y.X, value.Y.Y, value.Origin.X, value.Origin.Y);
        }
    }

    private void Room(int verts, int indices)
    {
        if (_n + verts > MaxVerts || _ni + indices > MaxIndices) Flush();
    }

    /// <summary>
    /// Test hook: while true, every flush folds the exact bits of its vertices, colors and indices into <see cref="Hash"/>,
    /// so a change meant to leave the picture alone (a speed-up) can be checked to draw exactly the same (--artbench).
    /// </summary>
    public static bool Hashing;
    public static ulong Hash = 14695981039346656037UL;

    private static void Fold(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) Hash = (Hash ^ b) * 1099511628211UL;
    }

    /// <summary>Draws what has been added so far.</summary>
    public void Flush()
    {
        if (_ni == 0 || _ci == null) { _n = _ni = 0; return; }
        if (Hashing)
        {
            Fold(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_pts.AsSpan(0, _n)));
            Fold(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_cols.AsSpan(0, _n)));
            Fold(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_idx.AsSpan(0, _ni)));
        }
        int vb = Bucket(_n, 64), ib = Bucket(_ni, 192);
        var pts = _outPts[vb] ??= new Vector2[64 << vb];
        var cols = _outCols[vb] ??= new Color[64 << vb];
        _pts.AsSpan(0, _n).CopyTo(pts);
        _cols.AsSpan(0, _n).CopyTo(cols);
        Vector2[] uvs = null;
        if (_uvs != null)
        {
            uvs = _outUvs[vb] ??= new Vector2[64 << vb];
            _uvs.AsSpan(0, _n).CopyTo(uvs);
        }
        var idx = _outIdx[ib] ??= new int[192 << ib];
        _idx.AsSpan(0, _ni).CopyTo(idx);
        // Clear index slots this bucket used last time beyond this batch, so they stay degenerate (0,0,0).
        if (_outUsed[ib] > _ni) Array.Clear(idx, _ni, _outUsed[ib] - _ni);
        _outUsed[ib] = _ni;
        RenderingServer.CanvasItemAddTriangleArray(_ci.GetCanvasItem(), idx, pts, cols, uvs, null, null, _tex);
        _n = _ni = 0;
    }

    private static int Bucket(int count, int smallest)
    {
        int k = 0;
        while (smallest << k < count) k++;
        return k;
    }

    private int V(Vector2 p, Color c)
    {
        // _xform * p written out, in the same order Godot's operator does it (so the same bits), without its calls.
        _pts[_n] = _xf ? new Vector2(_xx * p.X + _yx * p.Y + _ox, _xy * p.X + _yy * p.Y + _oy) : p;
        _cols[_n] = c;
        return _n++;
    }

    private void T(int a, int b, int c)
    {
        _idx[_ni++] = a;
        _idx[_ni++] = b;
        _idx[_ni++] = c;
    }

    /// <summary>A convex polygon in one color.</summary>
    public void Fan(ReadOnlySpan<Vector2> pts, Color c)
    {
        if (pts.Length < 3) return;
        Room(pts.Length, (pts.Length - 2) * 3);
        int b = _n;
        foreach (var p in pts) V(p, c);
        for (int i = 1; i < pts.Length - 1; i++) T(b, b + i, b + i + 1);
    }

    /// <summary>A convex polygon with a color per point.</summary>
    public void Fan(ReadOnlySpan<Vector2> pts, ReadOnlySpan<Color> cols)
    {
        if (pts.Length < 3) return;
        Room(pts.Length, (pts.Length - 2) * 3);
        int b = _n;
        for (int i = 0; i < pts.Length; i++) V(pts[i], cols[i]);
        for (int i = 1; i < pts.Length - 1; i++) T(b, b + i, b + i + 1);
    }

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color col)
    {
        Room(3, 3);
        T(V(a, col), V(b, col), V(c, col));
    }

    /// <summary>A rotated <paramref name="w"/> x <paramref name="h"/> rectangle centered on <paramref name="c"/>.</summary>
    public void Quad(Vector2 c, float w, float h, float rot, Color col)
    {
        var x = Vector2.FromAngle(rot) * w * 0.5f;
        var y = new Vector2(-x.Y, x.X) * (h / Mathf.Max(w, 1e-4f));
        Room(4, 6);
        int b = V(c - x - y, col);
        V(c + x - y, col);
        V(c + x + y, col);
        V(c - x + y, col);
        T(b, b + 1, b + 2);
        T(b, b + 2, b + 3);
    }

    public void Line(Vector2 a, Vector2 b, float width, Color col)
    {
        if (SoftLines) { Stroke(stackalloc Vector2[] { a, b }, width, col); return; }
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        var n = new Vector2(-d.Y, d.X) / len * (width * 0.5f);
        Room(4, 6);
        int i = V(a - n, col);
        V(b - n, col);
        V(b + n, col);
        V(a + n, col);
        T(i, i + 1, i + 2);
        T(i, i + 2, i + 3);
    }

    public void Circle(Vector2 c, float r, Color col, int segments = 12)
    {
        // With a feather the edge fades across the radius instead of outside it, so the circle keeps its size and
        // dots smaller than the feather stay faint dots.
        float solid = Feather > 0 ? Mathf.Max(0, r - Feather * 0.5f) : r;
        if (Feather > 0) col.A *= Mathf.Min(1, 2 * r / Feather);
        Room(segments + 1, segments * 3);
        int center = V(c, col);
        for (int i = 0; i < segments; i++) V(c + Vector2.FromAngle(i * Mathf.Tau / segments) * solid, col);
        for (int i = 0; i < segments; i++) T(center, center + 1 + i, center + 1 + (i + 1) % segments);
        float outer = r + Feather * 0.5f;
        if (Feather > 0) Ring(c, (solid + outer) * 0.5f, outer - solid, col, col with { A = 0 }, segments);
    }

    /// <summary>A star (concave, so fanned from its center), like <see cref="Shapes.Star"/>.</summary>
    public void Star(Vector2 c, float outer, float inner, int points, float rotation, Color col)
    {
        int n = points * 2;
        Room(n + 1, n * 3);
        int center = V(c, col);
        for (int i = 0; i < n; i++) V(c + Vector2.FromAngle(rotation + i * Mathf.Pi / points) * (i % 2 == 0 ? outer : inner), col);
        for (int i = 0; i < n; i++) T(center, center + 1 + i, center + 1 + (i + 1) % n);
    }

    /// <summary>A circle outline of the given stroke width.</summary>
    public void Ring(Vector2 c, float r, float width, Color col, int segments = 48) => Ring(c, r, width, col, col, segments);

    /// <summary>A circle outline shading from <paramref name="inner"/> at its inside edge to <paramref name="outer"/>.</summary>
    public void Ring(Vector2 c, float r, float width, Color inner, Color outer, int segments)
    {
        float r0 = Mathf.Max(0, r - width * 0.5f), r1 = r + width * 0.5f;
        Room(segments * 2, segments * 6);
        int b = _n;
        for (int i = 0; i < segments; i++)
        {
            var u = Vector2.FromAngle(i * Mathf.Tau / segments);
            V(c + u * r0, inner);
            V(c + u * r1, outer);
        }
        for (int i = 0; i < segments; i++)
        {
            int a = b + i * 2, n = b + (i + 1) % segments * 2;
            T(a, a + 1, n + 1);
            T(a, n + 1, n);
        }
    }

    /// <summary>Any simple polygon, convex or not, in one color (what <see cref="CanvasItem.DrawColoredPolygon"/> draws).</summary>
    public void Polygon(ReadOnlySpan<Vector2> pts, Color c)
    {
        int tris = Triangulate(pts);
        if (tris == 0) return;
        Room(pts.Length, tris * 3);
        int b = _n;
        foreach (var p in pts) V(p, c);
        for (int i = 0; i < tris * 3; i++) _idx[_ni++] = b + Tris[i];
    }

    /// <summary>Any simple polygon with a color per point.</summary>
    public void Polygon(ReadOnlySpan<Vector2> pts, ReadOnlySpan<Color> cols)
    {
        int tris = Triangulate(pts);
        if (tris == 0) return;
        Room(pts.Length, tris * 3);
        int b = _n;
        for (int i = 0; i < pts.Length; i++) V(pts[i], cols[i]);
        for (int i = 0; i < tris * 3; i++) _idx[_ni++] = b + Tris[i];
    }

    /// <summary>A polygon shaded from <paramref name="top"/> to <paramref name="bottom"/> by y, like <see cref="Shapes.Gradient"/>.</summary>
    public void Gradient(ReadOnlySpan<Vector2> pts, Color top, Color bottom)
    {
        if (pts.Length > MaxPoly) return;
        float y0 = float.MaxValue, y1 = float.MinValue;
        foreach (var p in pts) { y0 = Mathf.Min(y0, p.Y); y1 = Mathf.Max(y1, p.Y); }
        for (int i = 0; i < pts.Length; i++) Shades[i] = top.Lerp(bottom, y1 > y0 ? (pts[i].Y - y0) / (y1 - y0) : 0);
        Polygon(pts, Shades.AsSpan(0, pts.Length));
    }

    /// <summary>
    /// A band <paramref name="width"/> wide around the outside of a polygon, for an outline under a fill drawn after it
    /// (the outer half of a <c>2 * width</c> stroke). It reaches a little inside too, so no seam shows against the fill.
    /// </summary>
    public void Outline(ReadOnlySpan<Vector2> pts, float width, Color c)
    {
        if (!EdgeNormals(pts, true)) return;
        var clear = c with { A = 0 };
        if (SoftLines) Band(pts, true, stackalloc float[] { -width * 0.25f, width * SoftCore, width * SoftReach }, stackalloc Color[] { c, c, clear });
        else Band(pts, true, stackalloc float[] { -width * 0.25f, width }, stackalloc Color[] { c, c });
    }

    /// <summary>A line through the points, <paramref name="width"/> wide and centered on them (DrawPolyline).</summary>
    public void Stroke(ReadOnlySpan<Vector2> pts, float width, Color c, bool closed = false)
    {
        if (!EdgeNormals(pts, closed)) return;
        float h = width * 0.5f;
        var clear = c with { A = 0 };
        if (SoftLines)
            Band(pts, closed, stackalloc float[] { -h * SoftReach, -h * SoftCore, h * SoftCore, h * SoftReach }, stackalloc Color[] { clear, c, c, clear });
        else Band(pts, closed, stackalloc float[] { -h, h }, stackalloc Color[] { c, c });
    }

    /// <summary>
    /// Strips along the points: one row of vertices per offset (along <see cref="Normals"/>) in its color, joined row
    /// to row, so a feathered edge is a last row whose color is fully transparent.
    /// </summary>
    private void Band(ReadOnlySpan<Vector2> pts, bool closed, ReadOnlySpan<float> offsets, ReadOnlySpan<Color> cols)
    {
        int n = pts.Length, rows = offsets.Length, segs = closed ? n : n - 1;
        Room(n * rows, segs * (rows - 1) * 6);
        int b = _n;
        for (int i = 0; i < n; i++)
            for (int r = 0; r < rows; r++)
                V(pts[i] + Normals[i] * offsets[r], cols[r]);
        for (int i = 0; i < segs; i++)
        {
            int a = b + i * rows, nx = b + (i + 1) % n * rows;
            for (int r = 0; r < rows - 1; r++)
            {
                T(a + r, a + r + 1, nx + r + 1);
                T(a + r, nx + r + 1, nx + r);
            }
        }
    }

    /// <summary>An arc from angle <paramref name="a0"/> to <paramref name="a1"/> (DrawArc).</summary>
    public void Arc(Vector2 center, float r, float a0, float a1, int segments, Color c, float width)
    {
        segments = Math.Clamp(segments, 1, MaxPoly - 1);
        Span<Vector2> pts = stackalloc Vector2[segments + 1];
        for (int i = 0; i <= segments; i++) pts[i] = center + Vector2.FromAngle(a0 + (a1 - a0) * i / segments) * r;
        Stroke(pts, width, c);
    }

    /// <summary>
    /// Fills <see cref="Normals"/> with each point's outward normal: the average of its edges' normals, lengthened at
    /// corners so the band keeps its width along the edges (capped, so sharp tips don't spike). Open lines keep the
    /// end segments' normals at the ends. False when there's nothing to draw.
    /// </summary>
    private static bool EdgeNormals(ReadOnlySpan<Vector2> pts, bool closed)
    {
        int n = pts.Length;
        if (n < 2 || n > MaxPoly) return false;
        // Outward is to the right of each edge on a positive-area loop, to the left on a negative one.
        float sign = closed && SignedArea(pts) < 0 ? -1 : 1;
        // Each edge's normal once (edge i runs from point i to the next), shared by the two points it joins.
        int edges = closed ? n : n - 1;
        for (int i = 0; i < edges; i++) Edges[i] = EdgeNormal(pts[i], pts[i + 1 < n ? i + 1 : 0], sign);
        for (int i = 0; i < n; i++)
        {
            bool hasPrev = closed || i > 0, hasNext = closed || i < n - 1;
            var prev = hasPrev ? Edges[i > 0 ? i - 1 : n - 1] : Vector2.Zero;
            var next = hasNext ? Edges[i] : Vector2.Zero;
            var sum = prev + next;
            if (sum.LengthSquared() < 1e-8f) { Normals[i] = hasNext ? next : prev; continue; }
            var m = sum.Normalized();
            float d = m.Dot(hasNext && next != Vector2.Zero ? next : prev);
            Normals[i] = m / Mathf.Max(d, 0.5f);
        }
        return true;
    }

    private static Vector2 EdgeNormal(Vector2 a, Vector2 b, float sign)
    {
        var d = b - a;
        float len = d.Length();
        return len < 1e-6f ? Vector2.Zero : new Vector2(d.Y, -d.X) / len * sign;
    }

    private static float SignedArea(ReadOnlySpan<Vector2> pts)
    {
        float a = 0;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++) a += pts[j].X * pts[i].Y - pts[i].X * pts[j].Y;
        return a * 0.5f;
    }

    /// <summary>
    /// Triangulates a simple polygon into <see cref="Tris"/> (point indices, three per triangle) and returns the
    /// triangle count. Convex polygons are fanned; others are ear-clipped.
    /// </summary>
    private static int Triangulate(ReadOnlySpan<Vector2> pts)
    {
        int n = pts.Length;
        if (n < 3 || n > MaxPoly) return 0;
        float sign = SignedArea(pts) < 0 ? -1 : 1;
        bool convex = true;
        for (int i = 0; i < n && convex; i++)
        {
            var a = pts[(i - 1 + n) % n];
            var b = pts[i];
            var c = pts[(i + 1) % n];
            if ((b - a).Cross(c - b) * sign < -1e-6f) convex = false;
        }
        int k = 0;
        if (convex)
        {
            for (int i = 1; i < n - 1; i++) { Tris[k++] = 0; Tris[k++] = i; Tris[k++] = i + 1; }
            return n - 2;
        }

        for (int i = 0; i < n; i++) Clip[i] = i;
        int left = n, at = 0, misses = 0;
        while (left > 3)
        {
            int ip = Clip[(at - 1 + left) % left], ic = Clip[at], inx = Clip[(at + 1) % left];
            Vector2 a = pts[ip], b = pts[ic], c = pts[inx];
            bool ear = (b - a).Cross(c - b) * sign > 1e-7f;
            for (int j = 0; ear && j < left; j++)
            {
                int q = Clip[j];
                if (q != ip && q != ic && q != inx && Inside(pts[q], a, b, c, sign)) ear = false;
            }
            // A full lap without an ear means a degenerate or self-touching outline: clip anyway rather than drop it.
            if (ear || misses >= left)
            {
                Tris[k++] = ip; Tris[k++] = ic; Tris[k++] = inx;
                for (int j = at; j < left - 1; j++) Clip[j] = Clip[j + 1];
                left--;
                if (at >= left) at = 0;
                misses = 0;
            }
            else
            {
                at = (at + 1) % left;
                misses++;
            }
        }
        Tris[k++] = Clip[0]; Tris[k++] = Clip[1]; Tris[k++] = Clip[2];
        return k / 3;
    }

    private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float sign)
        => (b - a).Cross(p - a) * sign >= 0 && (c - b).Cross(p - b) * sign >= 0 && (a - c).Cross(p - c) * sign >= 0;

    /// <summary>Textured batches: the whole texture stretched over a square of radius <paramref name="r"/>.</summary>
    public void Sprite(Vector2 c, float r, Color col)
    {
        Room(4, 6);
        int b = _n;
        V(c + new Vector2(-r, -r), col);
        V(c + new Vector2(r, -r), col);
        V(c + new Vector2(r, r), col);
        V(c + new Vector2(-r, r), col);
        _uvs[b] = new Vector2(0, 0);
        _uvs[b + 1] = new Vector2(1, 0);
        _uvs[b + 2] = new Vector2(1, 1);
        _uvs[b + 3] = new Vector2(0, 1);
        T(b, b + 1, b + 2);
        T(b, b + 2, b + 3);
    }
}
