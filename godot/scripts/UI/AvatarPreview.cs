using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>Draws a player character in a UI layout at an integer zoom. Optionally idles, blinks and looks at the mouse.</summary>
public partial class AvatarPreview : Control
{
    public Appearance Look = Appearance.Default();
    public int ColorIdx;
    public int Zoom;
    public bool Alive;    // blink, look at the mouse, bob the halo
    public bool ShowGun = true;

    private float _t, _blink, _nextBlink = 2.5f;

    public AvatarPreview(Appearance look, int colorIdx, int zoom = 1, bool alive = false)
    {
        Look = look;
        ColorIdx = colorIdx;
        Zoom = zoom;
        Alive = alive;
        // Room for the tallest hat above the head and the gun in front.
        CustomMinimumSize = new Vector2(30, 32) * zoom;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public AvatarPreview() { }

    public override void _Process(double delta)
    {
        if (!Alive || !IsVisibleInTree()) return;
        float dt = (float)delta;
        _t += dt;
        _blink -= dt;
        if (_blink < -_nextBlink)
        {
            _blink = 0.12f;
            _nextBlink = 2f + (_t * 7.3f) % 2.5f;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var center = (Size * 0.5f + new Vector2(-2, 5) * Zoom).Round();
        var aim = new Vector2(1, 0.25f).Normalized();
        if (Alive)
        {
            var toMouse = GetLocalMousePosition() - center;
            if (toMouse.LengthSquared() > 4) aim = toMouse.Normalized();
        }
        var pose = new AvatarPose
        {
            Body = Palette.Player(ColorIdx),
            Look = Look,
            Facing = aim.X < 0 ? -1 : 1,
            Aim = aim,
            GunAim = new Vector2(aim.X < 0 ? -1 : 1, 0), // level, so it never covers the face
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
