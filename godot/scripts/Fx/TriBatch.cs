using System;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// Collects many small shapes as triangles and draws them with one RenderingServer call per few thousand vertices,
/// instead of one engine call per shape (each costs far more than the triangles it draws). The arrays are fixed-size
/// and reused: unused index slots are degenerate triangles, so a flush allocates nothing. Shapes keep the order they
/// were added in. Drawn without line feathering; the project's 2D MSAA smooths the edges.
/// </summary>
public sealed class TriBatch
{
    private const int MaxVerts = 4096, MaxIndices = MaxVerts * 3;

    private readonly Vector2[] _pts = new Vector2[MaxVerts];
    private readonly Color[] _cols = new Color[MaxVerts];
    private readonly Vector2[] _uvs;
    private readonly int[] _idx = new int[MaxIndices];
    private int _n, _ni, _usedIdx;
    private CanvasItem _ci;
    private Rid _tex;

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
    }

    private void Room(int verts, int indices)
    {
        if (_n + verts > MaxVerts || _ni + indices > MaxIndices) Flush();
    }

    /// <summary>Draws what has been added so far.</summary>
    public void Flush()
    {
        if (_ni == 0 || _ci == null) { _n = _ni = 0; return; }
        // Clear index slots used last time beyond this batch, so they stay degenerate (0,0,0).
        if (_usedIdx > _ni) Array.Clear(_idx, _ni, _usedIdx - _ni);
        _usedIdx = _ni;
        RenderingServer.CanvasItemAddTriangleArray(_ci.GetCanvasItem(), _idx, _pts, _cols, _uvs, null, null, _tex);
        _n = _ni = 0;
    }

    private int V(Vector2 p, Color c)
    {
        _pts[_n] = p;
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
        Room(segments + 1, segments * 3);
        int center = V(c, col);
        for (int i = 0; i < segments; i++) V(c + Vector2.FromAngle(i * Mathf.Tau / segments) * r, col);
        for (int i = 0; i < segments; i++) T(center, center + 1 + i, center + 1 + (i + 1) % segments);
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
    public void Ring(Vector2 c, float r, float width, Color col, int segments = 48)
    {
        float r0 = Mathf.Max(0, r - width * 0.5f), r1 = r + width * 0.5f;
        Room(segments * 2, segments * 6);
        int b = _n;
        for (int i = 0; i < segments; i++)
        {
            var u = Vector2.FromAngle(i * Mathf.Tau / segments);
            V(c + u * r0, col);
            V(c + u * r1, col);
        }
        for (int i = 0; i < segments; i++)
        {
            int a = b + i * 2, n = b + (i + 1) % segments * 2;
            T(a, a + 1, n + 1);
            T(a, n + 1, n);
        }
    }

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
