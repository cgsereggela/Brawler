using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>Draws a player character in a UI layout. Optionally idles, blinks, looks at the mouse and stands on a glowing pedestal.</summary>
public partial class AvatarPreview : Control
{
    public Appearance Look = Appearance.Default();
    public int ColorIdx;
    public float Zoom;
    public bool Alive;    // blink, breathe, look at the mouse
    public bool ShowGun = true;
    public bool Pedestal;

    private float _t, _blink, _nextBlink = 2.5f;

    public AvatarPreview(Appearance look, int colorIdx, float zoom = 1, bool alive = false)
    {
        Look = look;
        ColorIdx = colorIdx;
        Zoom = zoom;
        Alive = alive;
        Material = PaintShaders.Grain;
        // Room for the tallest hat above the head and the gun in front.
        CustomMinimumSize = new Vector2(32, 36) * zoom;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public AvatarPreview() { }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        float dt = (float)delta;
        _t += dt;
        if (Alive)
        {
            _blink -= dt;
            if (_blink < -_nextBlink)
            {
                _blink = 0.12f;
                _nextBlink = 2f + (_t * 7.3f) % 2.5f;
            }
        }
        if (Alive || Pedestal) QueueRedraw();
    }

    public override void _Draw()
    {
        var center = Size * 0.5f + new Vector2(-1.5f, 6.5f) * Zoom;
        var body = Palette.Player(ColorIdx);
        if (Pedestal)
        {
            var feet = center + new Vector2(1.5f, 9.6f) * Zoom;
            GlowTex.Draw(this, feet, 18 * Zoom, body with { A = 0.35f + 0.08f * Mathf.Sin(_t * 2) });
            DrawColoredPolygon(Shapes.Ellipse(feet, 13 * Zoom, 3 * Zoom, 32), new Color(0, 0, 0, 0.35f));
            Shapes.DrawClosed(this, Shapes.Ellipse(feet, 13 * Zoom, 3 * Zoom, 40), Shapes.Hdr(body.Lightened(0.3f), 1.2f) with { A = 0.8f }, Mathf.Max(1, Zoom * 0.35f));
        }
        var aim = new Vector2(1, 0.25f).Normalized();
        if (Alive)
        {
            var toMouse = GetLocalMousePosition() - center;
            if (toMouse.LengthSquared() > 4) aim = toMouse.Normalized();
        }
        var pose = new AvatarPose
        {
            Body = body,
            Look = Look,
            Facing = aim.X < 0 ? -1 : 1,
            Aim = aim,
            Scale = Zoom,
            Time = _t,
            Grounded = true,
            Blink = _blink > 0,
            Gun = ShowGun,
            GunLook = GunLook.Default,
        };
        AvatarArt.Draw(this, center, pose);
    }
}
