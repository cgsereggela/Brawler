using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// How a portal ring looks: an upright oval (or a flat one, for a ring facing up or down) with a swirl turning
/// inside it, a bright rim and sparks drifting in at its front, so you can see which side takes you through. Both
/// rings of a pair share a colour; each pair in an arena has its own.
/// </summary>
public static class PortalArt
{
    private static readonly Color[] PairColors = { new("#b77dff"), new("#ff9a3c"), new("#4fe0a0"), new("#ff6fb5") };

    public static Color ColorOf(int pair) => PairColors[(pair % PairColors.Length + PairColors.Length) % PairColors.Length];

    private static Vector2 N(PortalRing r) => new(r.Normal.X, r.Normal.Y);
    private static Vector2 T(PortalRing r) => new(r.Tangent.X, r.Tangent.Y);

    /// <summary>Points round the oval, <paramref name="scale"/> of its full size.</summary>
    private static Vector2[] Oval(PortalRing r, float scale, int n = 20, float depth = 5)
    {
        var c = new Vector2(r.X, r.Y);
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.Tau / n;
            pts[i] = c + T(r) * Mathf.Cos(a) * PortalRing.Half * scale + N(r) * Mathf.Sin(a) * depth * scale;
        }
        return pts;
    }

    /// <summary>The ring's body: a dark well with a swirl turning in it.</summary>
    public static void Base(CanvasItem ci, PortalRing r, Color col, float t)
    {
        ci.DrawColoredPolygon(Oval(r, 1.08f), new Color("#0b0c14") with { A = 0.9f });
        ci.DrawColoredPolygon(Oval(r, 0.92f), col.Darkened(0.7f));
        var c = new Vector2(r.X, r.Y);
        for (int k = 0; k < 2; k++)
        {
            var arc = new Vector2[9];
            for (int i = 0; i < arc.Length; i++)
            {
                float a = t * 3.2f + k * Mathf.Pi + i * 0.32f;
                float s = 0.75f - i * 0.05f;
                arc[i] = c + T(r) * Mathf.Cos(a) * PortalRing.Half * s + N(r) * Mathf.Sin(a) * 4 * s;
            }
            ci.DrawPolyline(arc, col.Darkened(0.25f), 1.4f, true);
        }
    }

    /// <summary>The bright parts, for the glow layer: the rim, a haze in front and sparks drifting in.</summary>
    public static void Glow(CanvasItem ci, PortalRing r, Color col, float t)
    {
        var c = new Vector2(r.X, r.Y);
        Shapes.DrawClosed(ci, Oval(r, 1f, 24), Shapes.Hdr(col, 1.7f), 1.6f);
        GlowTex.Draw(ci, c + N(r) * 5, 24, col with { A = 0.28f + 0.08f * Mathf.Sin(t * 4) });
        for (int k = 0; k < 4; k++)
        {
            float u = Mathf.PosMod(t * 0.9f + k / 4f, 1f);
            float along = Mathf.Sin(k * 2.4f + 1.3f) * PortalRing.Half * 0.7f;
            var p = c + T(r) * along * (1 - u * 0.5f) + N(r) * (18 * (1 - u) + 1);
            ci.DrawCircle(p, 1.1f, Shapes.Hdr(col, 1.4f) with { A = u });
        }
    }
}
