using System;
using Brawler.Fx;
using Godot;

namespace Brawler.Game;

// Trails: an earned cosmetic (see Logic/Career.cs) that follows a moving player. Every peer draws it from where the
// avatar has been, so it needs nothing on the wire beyond the look, and it never touches the sim.
public partial class Avatar
{
    private const int TrailLen = TrailArt.Length;
    private const float TrailStep = 1f / 30;
    private readonly Vector2[] _trail = new Vector2[TrailLen];
    private int _trailHead, _trailCount;
    private float _trailT;

    private Vector2 TrailAt(int i) => _trail[((_trailHead - 1 - i) % TrailLen + TrailLen) % TrailLen];

    private void TrailTick(float dt)
    {
        if (Player.Info.Look.Trail == 0) return;
        _trailT += dt;
        if (_trailT < TrailStep) return;
        _trailT = 0;
        if (_trailCount > 0)
        {
            float moved = TrailAt(0).DistanceTo(GlobalPosition);
            if (moved > 80) _trailCount = 0; // a respawn or a blink: start over rather than streak across the arena
            else if (moved < 0.5f)
            {
                _trailCount--; // standing still, it catches up and fades away
                return;
            }
        }
        _trail[_trailHead] = GlobalPosition;
        _trailHead = (_trailHead + 1) % TrailLen;
        _trailCount = Math.Min(TrailLen, _trailCount + 1);
    }

    private void DrawTrail(float size)
    {
        int style = Player.Info.Look.Trail;
        if (style == 0 || _trailCount < 2) return;
        var pts = new Vector2[_trailCount];
        for (int i = 0; i < _trailCount; i++) pts[i] = TrailAt(i) - GlobalPosition + new Vector2(0, 3 * size);
        TrailArt.Draw(this, style, pts, size, Player.Color, _animT);
    }
}

/// <summary>Draws a trail through points (newest first, in the canvas item's space), for avatars and the editor preview.</summary>
public static class TrailArt
{
    /// <summary>How many points a full trail has.</summary>
    public const int Length = 14;

    public static void Draw(CanvasItem ci, int style, Vector2[] pts, float size, Color body, float time)
    {
        for (int i = 1; i < pts.Length; i++)
        {
            float f = i / (float)Length, fade = 1 - f;
            var p = pts[i];
            // A little sideways wobble, different for each point but steady as it ages.
            float wob = Mathf.Sin(i * 2.3f + time * 5f);
            switch (style)
            {
                case 1: // Sparks: bright flecks in the player's color, scattering as they age.
                    ci.DrawCircle(p + new Vector2(wob * f * 9, Mathf.Cos(i * 1.7f) * f * 7) * size, (2.6f * fade + 0.4f) * size,
                        Shapes.Hdr(body.Lightened(0.4f), 1.6f) with { A = fade });
                    break;
                case 2: // Bubbles: rings that float up and grow.
                    ci.DrawArc(p + new Vector2(wob * 2, -f * 10) * size, (2f + f * 3.5f) * size, 0, Mathf.Tau, 14,
                        new Color(0.75f, 0.95f, 1f, 0.8f * fade), 0.9f * size);
                    break;
                case 3: // Rainbow: a ribbon through the colors.
                    ci.DrawLine(pts[i - 1], p, Color.FromHsv((i * 0.07f + time * 0.3f) % 1f, 0.6f, 1f, 0.85f * fade), 6.5f * fade * size);
                    break;
                case 4: // Flames: embers that flicker, rise and cool from yellow to red.
                {
                    var col = new Color(1f, 0.85f, 0.35f).Lerp(new Color(0.9f, 0.25f, 0.1f), f);
                    ci.DrawCircle(p + new Vector2(wob * 1.5f, -f * 8) * size, (3.6f * fade + 0.6f) * size * (0.85f + 0.15f * wob),
                        Shapes.Hdr(col, 1.5f) with { A = 0.85f * fade });
                    break;
                }
                case 5: // Stars: little twinkling stars left in the air.
                {
                    if (i % 2 == 1) break;
                    float r = (3f + 0.7f * Mathf.Sin(time * 9 + i)) * fade * size;
                    var star = new Vector2[10];
                    for (int k = 0; k < 10; k++)
                    {
                        float a = -Mathf.Pi / 2 + k * Mathf.Pi / 5 + i * 0.4f;
                        star[k] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (k % 2 == 0 ? r : r * 0.45f);
                    }
                    ci.DrawColoredPolygon(star, Shapes.Hdr(new Color(1f, 0.95f, 0.7f), 1.5f) with { A = fade });
                    break;
                }
            }
        }
    }
}
