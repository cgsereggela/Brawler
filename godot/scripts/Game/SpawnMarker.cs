using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Points out your own character as a battle starts, so you know where to look: rings close in on you and a bouncing
/// arrow with YOU hangs over your head through the countdown, fading out shortly after the fight begins.
/// </summary>
public partial class SpawnMarker : Node2D
{
    public GameManager Game;

    /// <summary>How long the marker stays once the fight has begun, fading over the last part.</summary>
    private const float Linger = 1.4f, Fade = 0.5f;

    private float _t = 99; // seconds since the countdown began
    private float _clock;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _clock += dt;
        if (Game.Phase == Phase.Countdown) _t = GameManager.CountdownTime - Game.CountdownLeft;
        else if (Game.Phase == Phase.Fighting) _t += dt;
        else _t = 99;
        bool show = _t < GameManager.CountdownTime + Linger && Me() != null && !Game.KillCam.Playing;
        if (show != Visible) Visible = show;
        if (show) QueueRedraw();
    }

    private Avatar Me() => Game.LocalMatchPlayer?.Avatar is { } a && IsInstanceValid(a) && a.Alive ? a : null;

    public override void _Draw()
    {
        if (Me() is not { } me) return;
        float alpha = Mathf.Clamp((GameManager.CountdownTime + Linger - _t) / Fade, 0, 1);
        var col = Palette.Player(me.Player.Info.ColorIdx).Lightened(0.25f);
        var at = me.GlobalPosition;
        // Rings that shrink onto you, a new one every 0.45 s, through the countdown only.
        if (Game.Phase == Phase.Countdown)
            for (int i = 0; i < 2; i++)
            {
                float k = (_clock / 0.45f + i * 0.5f) % 1f;
                float r = Mathf.Lerp(80, 16, k);
                DrawArc(at, r, 0, Mathf.Tau, 48, Shapes.Hdr(col, 1.3f) with { A = alpha * k * 0.85f }, 2.5f);
            }

        // The arrow over your head (above the HP bar), bobbing, with YOU above it (clear of a Headhunter crown).
        const float s = 1.6f;
        float size = me.S[Stat.Size];
        float top = AvatarArt.TopY(me.Player.Info.Look) * size - (Game.HeadSlot == me.Player.Info.Slot ? 16 : 0);
        var tip = at + new Vector2(0, top - 10 - Mathf.Abs(Mathf.Sin(_clock * 6)) * 6);
        var arrow = new[] { tip, tip + new Vector2(-7, -8) * s, tip + new Vector2(-2.5f, -8) * s, tip + new Vector2(-2.5f, -15) * s,
            tip + new Vector2(2.5f, -15) * s, tip + new Vector2(2.5f, -8) * s, tip + new Vector2(7, -8) * s };
        GlowTex.Draw(this, tip + new Vector2(0, -8 * s), 16 * s, col with { A = 0.4f * alpha });
        var outline = new Vector2[arrow.Length + 1];
        arrow.CopyTo(outline, 0);
        outline[^1] = arrow[0];
        DrawPolyline(outline, new Color(0.05f, 0.03f, 0.1f, 0.9f * alpha), 3f);
        DrawColoredPolygon(arrow, Shapes.Hdr(col, 1.3f) with { A = alpha });
        var label = tip + new Vector2(-30, -15 * s - 6);
        DrawStringOutline(Fonts.Heavy, label, "YOU", HorizontalAlignment.Center, 60, 15, 4, new Color(0.05f, 0.03f, 0.1f, 0.9f * alpha));
        DrawString(Fonts.Heavy, label, "YOU", HorizontalAlignment.Center, 60, 15, Colors.White with { A = alpha });
    }
}
