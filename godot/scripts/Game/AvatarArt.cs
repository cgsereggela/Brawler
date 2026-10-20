using System;
using System.Collections.Generic;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>Everything needed to draw one frame of a character. Built by the in-game Avatar and by UI previews.</summary>
public struct AvatarPose
{
    public Color Body;
    public Appearance Look;
    public int Facing;        // +1 right, -1 left
    public Vector2 Aim;       // unit vector, world space
    public Vector2 GunAim;    // where the gun points; zero = same as Aim
    public float Scale;       // Stat.Size
    public float Time;        // seconds; drives idle breathing, halo bob, propeller, visor scan
    public float WalkPhase;   // advances with distance walked
    public bool Grounded, Moving, Blink, Hurt;
    public bool Gun;
    public GunLook GunLook;
    public float Kick;        // 0..1 recoil
    public float Flash;       // 0..1 muzzle flash
}

/// <summary>
/// Vector art for players: body, face parts, hair, hats and the gun, built from smooth polygons so they stay crisp
/// at any resolution. Sprite space faces right with the body centered on (0,0) and covering x -7..7, y -9..9
/// (feet at the bottom). Option indices follow the name lists in <see cref="Appearance"/>.
/// </summary>
public static class AvatarArt
{
    private static readonly Color Ink = new("#17141f");
    private static readonly Color White = new("#f7f7fb"), OffWhite = new("#dcdcea");
    private static readonly Color Red = new("#ef4256"), DarkRed = new("#a3213a");
    private static readonly Color Pink = new("#ff8fb8"), DarkPink = new("#d9608a");
    private static readonly Color Orange = new("#ffa033"), DarkOrange = new("#c4620f");
    private static readonly Color Gold = new("#ffd23d"), DarkGold = new("#c98f12"), PaleYellow = new("#fff3a0");
    private static readonly Color Blue = new("#3d9bff"), Metal = new("#aab2cc"), DarkMetal = new("#646c8c");
    private static readonly Color Bone = new("#f1e6c8"), HatBlack = new("#262433"), HatGrey = new("#4a4862");
    private static readonly Color Visor = new("#3de6e0");
    private static readonly Color GunLight = new("#9aa3c2"), GunMid = new("#5a6182"), GunDark = new("#353a52");

    // Top of each hair style and hat in sprite space, for placing the HP bar.
    private static readonly float[] HairTop = { -9.5f, -11.3f, -16.2f, -16.4f, -11.5f, -11.8f, -11.3f, -18.8f };
    private static readonly float[] HatTop = { -9.5f, -14.6f, -21.6f, -18.5f, -17.7f, -22f, -9.5f, -19f, -17.4f, -16.4f, -22.6f };

    /// <summary>Topmost point used by this look (hair and hat), for placing the HP bar.</summary>
    public static float TopY(Appearance look)
        => Mathf.Min(HairTop[Math.Clamp(look.Hair, 0, HairTop.Length - 1)], HatTop[Math.Clamp(look.Hat, 0, HatTop.Length - 1)]);

    /// <summary>
    /// Muzzle position relative to the body center, matching what Draw renders with no recoil. The geometry lives in
    /// <see cref="GunRig"/> (engine-independent) so the host's bullet origin and the art always agree.
    /// </summary>
    public static Vector2 Muzzle(Vector2 aim, int facing, float scale, GunLook g)
    {
        var m = GunRig.Muzzle(new System.Numerics.Vector2(aim.X, aim.Y), facing, scale, g);
        return new Vector2(m.X, m.Y);
    }

    private static System.Numerics.Vector2 N(Vector2 v) => new(v.X, v.Y);
    private static Vector2 G(System.Numerics.Vector2 v) => new(v.X, v.Y);

    /// <summary>Sprite-space aim the gun follows: GunAim if set, else Aim, else straight ahead.</summary>
    private static System.Numerics.Vector2 SpriteGunAim(AvatarPose p, int facing)
    {
        var aim = p.GunAim.LengthSquared() > 0.01f ? p.GunAim : p.Aim.LengthSquared() > 0.01f ? p.Aim : new Vector2(facing, 0);
        return GunRig.SpriteAim(N(aim), facing);
    }

    /// <summary>
    /// Gun space to canvas space: rotate to the gun angle and move to the hand (sprite space, see <see cref="GunRig"/>),
    /// then mirror by facing and scale like the body, around <paramref name="bodyOrigin"/>. Mirroring after the rotation
    /// keeps the grip hanging down on both sides.
    /// </summary>
    private static Transform2D GunXform(Vector2 bodyOrigin, Vector2 hand, float angle, float scale, int facing)
    {
        float cs = Mathf.Cos(angle), sn = Mathf.Sin(angle);
        return new Transform2D(new Vector2(facing * cs, sn) * scale, new Vector2(-facing * sn, cs) * scale,
            bodyOrigin + new Vector2(facing * hand.X, hand.Y) * scale);
    }

    // =====================================================================
    // Drawing
    // =====================================================================

    private sealed class Ctx
    {
        public CanvasItem Ci;
        public AvatarPose P;
        public Color Body, Hair, Hat;
        public Color H(Color c) => P.Hurt ? Colors.White : c;
    }

    [ThreadStatic] private static Ctx _ctx;

    public static void Draw(CanvasItem ci, Vector2 origin, AvatarPose p)
    {
        var look = p.Look ?? Appearance.Default();
        float s = p.Scale <= 0 ? 1 : p.Scale;
        int facing = p.Facing < 0 ? -1 : 1;
        var c = _ctx ??= new Ctx();
        c.Ci = ci;
        c.P = p;
        c.Body = p.Hurt ? Colors.White : p.Body;
        c.Hair = Palette.Dye(look.HairColor);
        c.Hat = Palette.Dye(look.HatColor);

        // Walk cycle: feet alternate and the body bobs with the stride. Idle: a slow breath.
        bool walking = p.Grounded && p.Moving;
        float phase = walking ? p.WalkPhase * Mathf.Pi * 0.5f : 0;
        float stride = walking ? Mathf.Sin(phase) : 0;
        float bob = walking ? Mathf.Abs(Mathf.Sin(phase)) * 0.9f : !p.Grounded ? -0.6f : 0;
        float breathe = p.Grounded && !p.Moving ? Mathf.Sin(p.Time * 2.4f) * 0.25f : 0;

        var scale = new Vector2(facing * s, s);
        ci.DrawSetTransform(origin, 0, scale);
        var shoe = c.H(p.Body.Darkened(0.45f));
        var shoeLine = p.Body.Darkened(0.68f);
        Vector2 back = !p.Grounded ? new(-3.6f, 7.2f) : new(-3.2f + stride * 1.6f, 7.9f - Mathf.Max(0, -stride) * 1.1f);
        Vector2 front = !p.Grounded ? new(3.4f, 7.0f) : new(2.9f - stride * 1.6f, 7.9f - Mathf.Max(0, stride) * 1.1f);
        Shapes.Fill(ci, Shapes.Ellipse(back, 2.7f, 1.7f, 16), shoe.Darkened(0.1f), shoeLine, 0.5f);
        Shapes.Fill(ci, Shapes.Ellipse(front, 2.7f, 1.7f, 16), shoe, shoeLine, 0.5f);

        var bodyOrigin = origin + new Vector2(0, (bob - breathe) * s);
        ci.DrawSetTransform(bodyOrigin, 0, scale);
        if (p.Gun) DrawArms(c, facing); // behind the body, in body (sprite) space, so they ride along with it
        DrawHairBack(c, look.Hair);
        DrawBody(c, breathe);
        DrawEyes(c, look.Eyes, facing);
        DrawNose(c, look.Nose);
        DrawMouth(c, look.Mouth);
        DrawHair(c, look.Hair);
        DrawHat(c, look.Hat);

        if (p.Gun) DrawGun(c, bodyOrigin, s, facing);
        ci.DrawSetTransformMatrix(Transform2D.Identity);
    }

    private static void DrawBody(Ctx c, float breathe)
    {
        var body = Shapes.RoundRect(new Rect2(-7f - breathe * 0.4f, -9.5f, 14f + breathe * 0.8f, 16.6f), 6.2f, 6);
        Shapes.Fill(c.Ci, body, c.Body, c.P.Hurt ? Colors.White : c.P.Body.Darkened(0.6f), 0.9f);
        Shapes.Gradient(c.Ci, body, c.H(c.P.Body.Lightened(0.16f)), c.H(c.P.Body.Darkened(0.24f)));
        // Glossy highlight and a soft belly shade.
        c.Ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-3.4f, -6.4f), 2.4f, 1.3f, 14), new Color(1, 1, 1, 0.38f));
        c.Ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-4.6f, -4.6f), 0.7f, 0.6f, 10), new Color(1, 1, 1, 0.45f));
        c.Ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(0.6f, 4.6f), 5.4f, 1.8f, 18), new Color(0, 0, 0, 0.08f));
    }

    // ---------- eyes ----------

    private static void DrawEyes(Ctx c, int style, int facing)
    {
        var ci = c.Ci;
        var p = c.P;
        float ax = Mathf.Clamp(p.Aim.X * facing, -1, 1), ay = Mathf.Clamp(p.Aim.Y, -1, 1);
        var look = new Vector2(ax * 0.55f, ay * 0.6f);
        var white = c.H(White);
        Vector2 l = new(-1.9f, -4.4f), r = new(3.1f, -4.4f);

        void Eye(Vector2 at, float rx, float ry, float pupil)
        {
            if (p.Blink)
            {
                ci.DrawArc(at + new Vector2(0, -1.2f), rx * 0.95f, 0.35f, Mathf.Pi - 0.35f, 10, Ink, 0.8f, true);
                return;
            }
            ci.DrawColoredPolygon(Shapes.Ellipse(at, rx + 0.45f, ry + 0.45f, 18), Ink);
            ci.DrawColoredPolygon(Shapes.Ellipse(at, rx, ry, 18), white);
            var pp = at + look * new Vector2(rx - pupil * 0.7f, ry - pupil * 0.7f);
            ci.DrawCircle(pp, pupil, Ink, true, -1, true);
            ci.DrawCircle(pp + new Vector2(-pupil * 0.35f, -pupil * 0.4f), pupil * 0.36f, White, true, -1, true);
        }

        switch (style)
        {
            case 0: // Round
                Eye(l, 1.75f, 2.1f, 1.05f);
                Eye(r, 1.75f, 2.1f, 1.05f);
                break;
            case 1: // Big
                Eye(l + new Vector2(-0.4f, -0.2f), 2.25f, 2.7f, 1.4f);
                Eye(r + new Vector2(0.3f, -0.2f), 2.25f, 2.7f, 1.4f);
                break;
            case 2: // Dots
                foreach (var e in new[] { l + new Vector2(0.2f, 0.3f), r + new Vector2(0, 0.3f) })
                {
                    if (p.Blink) { ci.DrawLine(e + new Vector2(-1, 0.4f), e + new Vector2(1, 0.4f), Ink, 0.8f, true); continue; }
                    ci.DrawColoredPolygon(Shapes.Ellipse(e + look * 0.6f, 0.95f, 1.2f, 12), Ink);
                    ci.DrawCircle(e + look * 0.6f + new Vector2(-0.3f, -0.45f), 0.32f, White, true, -1, true);
                }
                break;
            case 3: // Sleepy
                foreach (var e in new[] { l + new Vector2(0, 0.5f), r + new Vector2(0, 0.5f) })
                {
                    if (p.Blink) { ci.DrawArc(e + new Vector2(0, -0.9f), 1.7f, 0.3f, Mathf.Pi - 0.3f, 10, Ink, 0.8f, true); continue; }
                    ci.DrawColoredPolygon(Shapes.Ellipse(e, 2.2f, 1.95f, 18), Ink);
                    ci.DrawColoredPolygon(Shapes.Ellipse(e, 1.8f, 1.55f, 18), white);
                    ci.DrawCircle(e + new Vector2(look.X * 0.9f, 0.4f), 0.95f, Ink, true, -1, true);
                    // Heavy lid over the top half.
                    ci.DrawColoredPolygon(new[] { e + new Vector2(-2.4f, 0.05f), e + new Vector2(-2.4f, -2.3f), e + new Vector2(2.4f, -2.3f), e + new Vector2(2.4f, 0.05f) },
                        c.H(p.Body.Darkened(0.12f)));
                    ci.DrawLine(e + new Vector2(-2.2f, 0.05f), e + new Vector2(2.2f, 0.05f), Ink, 0.75f, true);
                }
                break;
            case 4: // Angry
                Eye(l, 1.75f, 2.0f, 1.0f);
                Eye(r, 1.75f, 2.0f, 1.0f);
                ci.DrawColoredPolygon(Shapes.Capsule(new Vector2(-4.0f, -7.4f), new Vector2(-0.6f, -6.2f), 0.55f), Ink);
                ci.DrawColoredPolygon(Shapes.Capsule(new Vector2(1.8f, -6.2f), new Vector2(5.0f, -7.5f), 0.55f), Ink);
                break;
            case 5: // Visor
            {
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-5f, -6.6f, 12.4f, 4.4f), 2.2f), Ink);
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-4.2f, -5.9f, 10.8f, 3f), 1.5f), p.Hurt ? Colors.White : Shapes.Hdr(Visor, 1.25f));
                float u = Mathf.PingPong(p.Time * 1.4f, 1f);
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-3.8f + u * 8.2f, -5.6f, 1.8f, 2.4f), 0.9f), Shapes.Hdr(new Color("#d8fffd"), 1.6f));
                ci.DrawLine(new Vector2(-3.4f, -5.3f), new Vector2(1f, -5.3f), new Color(1, 1, 1, 0.45f), 0.4f, true);
                break;
            }
            case 6: // Cyclops
                Eye(new Vector2(0.9f, -4.6f), 3f, 3.1f, 1.6f);
                break;
            case 7: // Shades
            {
                ci.DrawLine(new Vector2(-4.5f, -5.4f), new Vector2(-7.3f, -5.8f), Ink, 0.7f, true);
                ci.DrawLine(new Vector2(0.2f, -5.3f), new Vector2(1.4f, -5.3f), Ink, 0.8f, true);
                foreach (float x in new[] { -4.5f, 1.3f })
                {
                    var lens = Shapes.RoundRect(new Rect2(x, -6.1f, 4.8f, 3.3f), 1.3f);
                    Shapes.Fill(ci, lens, HatBlack, Ink, 0.5f);
                    Shapes.Gradient(ci, lens, new Color("#3d3b58"), new Color("#141220"));
                    ci.DrawLine(new Vector2(x + 1.1f, -3.4f), new Vector2(x + 2.6f, -5.6f), new Color(1, 1, 1, 0.5f), 0.5f, true);
                }
                break;
            }
            case 8: // Happy
                ci.DrawArc(l + new Vector2(0, 0.6f), 1.5f, Mathf.Pi + 0.2f, Mathf.Tau - 0.2f, 12, Ink, 0.9f, true);
                ci.DrawArc(r + new Vector2(0, 0.6f), 1.5f, Mathf.Pi + 0.2f, Mathf.Tau - 0.2f, 12, Ink, 0.9f, true);
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-4.2f, -1.4f), 1.4f, 0.8f, 12), Pink with { A = 0.6f });
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(5.4f, -1.4f), 1.2f, 0.8f, 12), Pink with { A = 0.6f });
                break;
        }
    }

    // ---------- nose ----------

    private static void DrawNose(Ctx c, int style)
    {
        var ci = c.Ci;
        var b = c.P.Body;
        switch (style)
        {
            case 1: // Dot
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(3.8f, -1.7f), 0.75f, 0.55f, 10), c.H(b.Darkened(0.38f)));
                break;
            case 2: // Button
                Shapes.Fill(ci, Shapes.Ellipse(new Vector2(3.7f, -1.6f), 1.3f, 1.15f, 14), c.H(b.Lightened(0.14f)), b.Darkened(0.4f), 0.45f);
                ci.DrawCircle(new Vector2(3.3f, -2f), 0.35f, new Color(1, 1, 1, 0.6f), true, -1, true);
                break;
            case 3: // Long
                Shapes.Fill(ci, Shapes.Capsule(new Vector2(3f, -2f), new Vector2(8.6f, -1.3f), 1.1f), c.H(b.Lightened(0.1f)), b.Darkened(0.45f), 0.45f);
                ci.DrawCircle(new Vector2(7.6f, -0.9f), 0.35f, b.Darkened(0.5f), true, -1, true);
                break;
            case 4: // Pig
                Shapes.Fill(ci, Shapes.Ellipse(new Vector2(3.4f, -1.4f), 2.1f, 1.5f, 16), c.H(Pink), DarkPink, 0.45f);
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(2.6f, -1.4f), 0.45f, 0.65f, 8), c.H(DarkPink.Darkened(0.2f)));
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(4.2f, -1.4f), 0.45f, 0.65f, 8), c.H(DarkPink.Darkened(0.2f)));
                break;
            case 5: // Beak
                Shapes.Fill(ci, new[] { new Vector2(3.2f, -3.1f), new Vector2(9.4f, -1.6f), new Vector2(3.2f, -0.1f) }, c.H(Orange), DarkOrange.Darkened(0.2f), 0.45f);
                ci.DrawColoredPolygon(new[] { new Vector2(3.2f, -1.6f), new Vector2(9.4f, -1.6f), new Vector2(3.2f, -0.1f) }, c.H(DarkOrange));
                break;
            case 6: // Clown
                Shapes.Fill(ci, Shapes.Ellipse(new Vector2(3.9f, -1.6f), 1.8f, 1.8f, 16), c.H(Red), DarkRed, 0.45f);
                ci.DrawCircle(new Vector2(3.3f, -2.3f), 0.55f, new Color(1, 1, 1, 0.7f), true, -1, true);
                break;
        }
    }

    // ---------- mouth ----------

    private static readonly Vector2[] GrinShape = BuildGrin();

    private static Vector2[] BuildGrin()
    {
        var pts = new List<Vector2>();
        for (int i = 0; i <= 12; i++)
        {
            float a = i * Mathf.Pi / 12;
            pts.Add(new Vector2(1.5f + Mathf.Cos(a) * 2.8f, Mathf.Sin(a) * 2.4f));
        }
        return pts.ToArray();
    }

    private static readonly Vector2[] MustacheShape =
    {
        new(1.4f, -1f), new(3.4f, -1.3f), new(5.3f, -0.4f), new(6.1f, 0.9f), new(4.6f, 0.2f), new(2.6f, 0.5f), new(1.4f, 0.1f),
        new(0.2f, 0.5f), new(-1.8f, 0.2f), new(-3.3f, 0.9f), new(-2.5f, -0.4f), new(-0.6f, -1.3f),
    };

    private static void DrawMouth(Ctx c, int style)
    {
        var ci = c.Ci;
        switch (style)
        {
            case 0: // Smile
                ci.DrawArc(new Vector2(1.4f, -0.6f), 2.4f, 0.5f, Mathf.Pi - 0.5f, 14, Ink, 0.85f, true);
                break;
            case 1: // Flat
                ci.DrawLine(new Vector2(-0.3f, 1.3f), new Vector2(3.2f, 1.3f), Ink, 0.85f, true);
                break;
            case 2: // Open
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(1.4f, 1.1f), 1.4f, 1.7f, 16), Ink);
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(1.4f, 2.05f), 0.95f, 0.6f, 12), c.H(Red));
                break;
            case 3: // Grin
                Shapes.Fill(ci, GrinShape, c.H(White), Ink, 0.5f);
                ci.DrawLine(new Vector2(-1f, 0.9f), new Vector2(4f, 0.9f), Ink with { A = 0.6f }, 0.35f, true);
                ci.DrawLine(new Vector2(1.5f, 0f), new Vector2(1.5f, 2.3f), Ink with { A = 0.4f }, 0.3f, true);
                break;
            case 4: // Frown
                ci.DrawArc(new Vector2(1.4f, 3.1f), 2.3f, Mathf.Pi + 0.55f, Mathf.Tau - 0.55f, 14, Ink, 0.85f, true);
                break;
            case 5: // Fangs
                ci.DrawArc(new Vector2(1.4f, -3f), 3.8f, 1.05f, Mathf.Pi - 1.05f, 14, Ink, 0.85f, true);
                foreach (float fx in new[] { 0.1f, 2.7f })
                    Shapes.Fill(ci, new[] { new Vector2(fx - 0.6f, 0.45f), new Vector2(fx + 0.6f, 0.45f), new Vector2(fx, 2.1f) }, c.H(White), Ink, 0.3f);
                break;
            case 6: // Tongue
                ci.DrawArc(new Vector2(1.4f, -0.6f), 2.4f, 0.5f, Mathf.Pi - 0.5f, 14, Ink, 0.85f, true);
                Shapes.Fill(ci, Shapes.Capsule(new Vector2(2.4f, 1.4f), new Vector2(2.5f, 2.6f), 0.95f), c.H(Pink), DarkPink, 0.35f);
                ci.DrawLine(new Vector2(2.45f, 1.6f), new Vector2(2.5f, 2.6f), DarkPink, 0.3f, true);
                break;
            case 7: // Mustache
                ci.DrawLine(new Vector2(0.6f, 1.9f), new Vector2(2.3f, 1.9f), Ink, 0.7f, true);
                Shapes.Fill(ci, MustacheShape, c.H(c.Hair), c.Hair.Darkened(0.45f), 0.45f);
                break;
            case 8: // Cat
                ci.DrawArc(new Vector2(0.4f, 0.4f), 1.05f, 0.15f, Mathf.Pi - 0.15f, 10, Ink, 0.8f, true);
                ci.DrawArc(new Vector2(2.5f, 0.4f), 1.05f, 0.15f, Mathf.Pi - 0.15f, 10, Ink, 0.8f, true);
                break;
        }
    }

    // ---------- hair (color = hair dye) ----------

    private static readonly Vector2[] Hairline =
    {
        new(6.2f, -7.2f), new(4.5f, -6.6f), new(3f, -7.6f), new(1.2f, -6.9f), new(-0.8f, -7.8f), new(-3f, -7.2f), new(-5.2f, -6.4f), new(-6.2f, -3f),
    };

    private static Vector2[] Cap(params Vector2[] top)
    {
        var pts = new Vector2[top.Length + Hairline.Length];
        top.CopyTo(pts, 0);
        Hairline.CopyTo(pts, top.Length);
        return pts;
    }

    private static readonly Vector2[] ShortHair = Cap(
        new(-7.8f, -2.5f), new(-8.1f, -6f), new(-7.1f, -9f), new(-4.6f, -10.8f), new(0f, -11.3f), new(4.6f, -10.8f), new(7.1f, -9f), new(7.9f, -6.6f));

    private static readonly Vector2[] SpikyHair = Cap(
        new(-7.8f, -3f), new(-8.2f, -7f), new(-10.4f, -10.2f), new(-6.4f, -10.4f), new(-6.2f, -14.4f), new(-3f, -11.8f), new(-0.8f, -16.2f),
        new(1.2f, -11.8f), new(4f, -15.4f), new(4.8f, -11.2f), new(8.6f, -12.6f), new(7.4f, -9f), new(9.2f, -7.8f), new(7.8f, -6.6f));

    private static readonly Vector2[] Mohawk =
    {
        new(-5f, -9.2f), new(-4.6f, -12.2f), new(-3.2f, -14.8f), new(-1f, -16.4f), new(1.6f, -16.4f), new(3.8f, -14.9f), new(4.8f, -12.2f),
        new(5.2f, -9.4f), new(2f, -10.4f), new(-2f, -10.4f),
    };

    private static readonly Vector2[] LongHair =
    {
        new(-8.6f, 4.2f), new(-8.8f, -6f), new(-7.5f, -9.2f), new(-4.7f, -11f), new(0f, -11.5f), new(4.7f, -11f), new(7.3f, -9f), new(8.1f, -6.4f),
        new(6.2f, -7.2f), new(4.5f, -6.6f), new(3f, -7.6f), new(1.2f, -6.9f), new(-0.8f, -7.8f), new(-3f, -7.2f), new(-5f, -6.4f),
        new(-5.7f, -2f), new(-5.5f, 4.6f), new(-7f, 5.8f),
    };

    private static readonly Vector2[] BowlHair =
    {
        new(-8.1f, -2.8f), new(-8.4f, -7f), new(-7.1f, -9.7f), new(-4.1f, -11.4f), new(0f, -11.8f), new(4.1f, -11.4f), new(7.1f, -9.7f),
        new(8.4f, -7f), new(8.4f, -6.1f), new(-5.6f, -6.1f), new(-6.4f, -2.8f),
    };

    private static readonly (Vector2 C, float R)[] AfroPuffs =
    {
        (new(0f, -11.2f), 5.2f), (new(-4.6f, -10.2f), 4.3f), (new(4.4f, -10.6f), 4.2f), (new(-7f, -6.6f), 3.4f), (new(7.6f, -8f), 2.8f),
        (new(-2.6f, -13.8f), 3.8f), (new(2.8f, -13.8f), 3.7f), (new(0f, -15.4f), 3.3f), (new(-6.4f, -12.4f), 2.8f), (new(6.6f, -12.4f), 2.6f),
    };

    private static void DrawHairBack(Ctx c, int style)
    {
        if (style != 6) return;
        // Ponytail swings a little behind the head.
        float sw = Mathf.Sin(c.P.Time * 3f) * 0.6f;
        var tail = new[]
        {
            new Vector2(-6f, -8.4f), new Vector2(-9.8f, -10f), new Vector2(-12.6f, -8f + sw * 0.3f), new Vector2(-13.2f + sw, -4.2f),
            new Vector2(-12f + sw, -0.4f), new Vector2(-10.8f + sw * 0.6f, -3.6f), new Vector2(-9.8f, -6.2f), new Vector2(-7.2f, -5.6f),
        };
        Shapes.Fill(c.Ci, tail, c.H(c.Hair), c.Hair.Darkened(0.5f), 0.8f);
        c.Ci.DrawLine(new Vector2(-10.6f, -8.6f), new Vector2(-12.2f + sw, -4f), c.H(c.Hair.Lightened(0.3f)) with { A = 0.7f }, 0.5f, true);
    }

    private static void DrawHair(Ctx c, int style)
    {
        var ci = c.Ci;
        var main = c.H(c.Hair);
        var line = c.Hair.Darkened(0.5f);
        var shine = c.H(c.Hair.Lightened(0.35f)) with { A = 0.75f };
        switch (style)
        {
            case 1:
                Shapes.Fill(ci, ShortHair, main, line, 0.8f);
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-2f, -9.7f), 2.8f, 0.75f, 12), shine);
                break;
            case 2:
                Shapes.Fill(ci, SpikyHair, main, line, 0.8f);
                ci.DrawLine(new Vector2(-1f, -14.6f), new Vector2(-1.4f, -11f), shine, 0.6f, true);
                ci.DrawLine(new Vector2(3.6f, -13.8f), new Vector2(3f, -11f), shine, 0.5f, true);
                break;
            case 3:
                Shapes.Fill(ci, Mohawk, main, line, 0.8f);
                ci.DrawLine(new Vector2(-1.8f, -14.6f), new Vector2(-3.2f, -11f), shine, 0.6f, true);
                break;
            case 4:
                Shapes.Fill(ci, LongHair, main, line, 0.8f);
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-2f, -9.9f), 2.8f, 0.75f, 12), shine);
                ci.DrawLine(new Vector2(-7.4f, -4f), new Vector2(-7.2f, 3f), shine, 0.5f, true);
                break;
            case 5:
                Shapes.Fill(ci, BowlHair, main, line, 0.8f);
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-1.5f, -10.1f), 3.4f, 0.8f, 12), shine);
                for (float x = -3.6f; x < 8f; x += 2.4f)
                    ci.DrawLine(new Vector2(x, -7.8f), new Vector2(x + 0.2f, -6.3f), line with { A = 0.5f }, 0.35f, true);
                break;
            case 6:
                Shapes.Fill(ci, ShortHair, main, line, 0.8f);
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-2f, -9.7f), 2.8f, 0.75f, 12), shine);
                Shapes.Fill(ci, Shapes.Ellipse(new Vector2(-7.4f, -7.4f), 1.1f, 1.4f, 12), c.H(Red), DarkRed, 0.4f);
                break;
            case 7:
                foreach (var (pc, r) in AfroPuffs) ci.DrawCircle(pc, r + 0.8f, line, true, -1, true);
                foreach (var (pc, r) in AfroPuffs) ci.DrawCircle(pc, r, main, true, -1, true);
                foreach (var (pc, r) in AfroPuffs) ci.DrawArc(pc, r * 0.7f, Mathf.Pi * 1.1f, Mathf.Pi * 1.45f, 6, shine with { A = 0.45f }, 0.5f, true);
                break;
        }
    }

    // ---------- hats (color = hat dye) ----------

    private static Vector2[] Dome(Vector2 c, float rx, float ry, int segments = 16)
    {
        var pts = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float a = Mathf.Pi + i * Mathf.Pi / segments;
            pts[i] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }
        return pts;
    }

    private static void DrawHat(Ctx c, int style)
    {
        var ci = c.Ci;
        float t = c.P.Time;
        var dye = c.H(c.Hat);
        var dyeLine = c.Hat.Darkened(0.5f);
        var dyeLight = c.H(c.Hat.Lightened(0.3f));
        switch (style)
        {
            case 1: // Cap
            {
                Shapes.Fill(ci, Shapes.Capsule(new Vector2(2.5f, -9.7f), new Vector2(11.8f, -9.3f), 1.15f), c.H(c.Hat.Darkened(0.25f)), dyeLine, 0.5f);
                var dome = Dome(new Vector2(0, -9.4f), 7.7f, 4.8f);
                Shapes.Fill(ci, dome, dye, dyeLine, 0.7f);
                Shapes.Gradient(ci, dome, dyeLight, c.H(c.Hat.Darkened(0.15f)));
                ci.DrawLine(new Vector2(0.6f, -14.1f), new Vector2(1.8f, -9.6f), dyeLine with { A = 0.5f }, 0.4f, true);
                ci.DrawCircle(new Vector2(0.4f, -14.2f), 0.75f, dyeLine, true, -1, true);
                break;
            }
            case 2: // Top hat
            {
                var rim = HatGrey.Lightened(0.25f);
                Shapes.Fill(ci, Shapes.RoundRect(new Rect2(-5f, -21.4f, 10f, 11.6f), 1.2f), c.H(HatBlack), rim, 0.6f);
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-5f, -13.6f, 10f, 2.4f), 0.4f), dye);
                ci.DrawLine(new Vector2(-3.6f, -20f), new Vector2(-3.6f, -14.2f), c.H(HatGrey), 0.8f, true);
                Shapes.Fill(ci, Shapes.Capsule(new Vector2(-8.4f, -10.3f), new Vector2(8.4f, -10.3f), 1.2f), c.H(HatBlack), rim, 0.6f);
                break;
            }
            case 3: // Beanie
            {
                var dome = Dome(new Vector2(0, -9.6f), 7.8f, 6.2f);
                Shapes.Fill(ci, dome, dye, dyeLine, 0.7f);
                Shapes.Gradient(ci, dome, dyeLight, dye);
                Shapes.Fill(ci, Shapes.RoundRect(new Rect2(-8.3f, -11.2f, 16.6f, 3.2f), 1.4f), c.H(c.Hat.Darkened(0.15f)), dyeLine, 0.6f);
                for (float x = -6.6f; x < 7.5f; x += 1.7f)
                    ci.DrawLine(new Vector2(x, -10.6f), new Vector2(x, -8.6f), dyeLine with { A = 0.45f }, 0.35f, true);
                ci.DrawCircle(new Vector2(0, -16.2f), 2.3f, dyeLine, true, -1, true);
                ci.DrawCircle(new Vector2(0, -16.2f), 1.8f, c.H(c.Hat.Lightened(0.55f)), true, -1, true);
                break;
            }
            case 4: // Crown
            {
                var crown = new[]
                {
                    new Vector2(-6f, -9.8f), new Vector2(-6.6f, -15.6f), new Vector2(-3.3f, -12.7f), new Vector2(0f, -16.9f),
                    new Vector2(3.3f, -12.7f), new Vector2(6.6f, -15.6f), new Vector2(6f, -9.8f),
                };
                Shapes.Fill(ci, crown, c.H(Gold), DarkGold.Darkened(0.3f), 0.6f);
                Shapes.Gradient(ci, crown, c.H(PaleYellow), c.H(Gold));
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-6f, -11.6f, 12f, 1.8f), 0.4f), c.H(DarkGold));
                foreach (var tip in new[] { new Vector2(-6.6f, -15.6f), new Vector2(0, -16.9f), new Vector2(6.6f, -15.6f) })
                    ci.DrawCircle(tip, 0.8f, c.H(PaleYellow), true, -1, true);
                Shapes.Fill(ci, Shapes.Ellipse(new Vector2(0, -12.9f), 1.2f, 1.4f, 12), dye, dyeLine, 0.35f);
                ci.DrawCircle(new Vector2(-0.4f, -13.4f), 0.35f, new Color(1, 1, 1, 0.8f), true, -1, true);
                break;
            }
            case 5: // Party
            {
                var tip = new Vector2(1f, -20.4f);
                Vector2 bl = new(-5f, -10.2f), br = new(5f, -10.2f);
                Shapes.Fill(ci, new[] { bl, tip, br }, dye, dyeLine, 0.6f);
                foreach (var (a0, a1) in new[] { (0.22f, 0.36f), (0.55f, 0.69f) })
                    ci.DrawColoredPolygon(new[] { bl.Lerp(tip, a0), bl.Lerp(tip, a1), br.Lerp(tip, a1), br.Lerp(tip, a0) }, c.H(White));
                ci.DrawCircle(tip, 1.6f, c.H(PaleYellow), true, -1, true);
                ci.DrawCircle(tip + new Vector2(-0.4f, -0.4f), 0.5f, Colors.White, true, -1, true);
                break;
            }
            case 6: // Headband
            {
                float w1 = Mathf.Sin(t * 6f) * 0.9f, w2 = Mathf.Sin(t * 6f + 1.3f) * 0.9f;
                Shapes.Fill(ci, Shapes.Capsule(new Vector2(-7.4f, -8.2f), new Vector2(-12.2f, -6.6f + w1), 0.85f), c.H(c.Hat.Darkened(0.15f)), dyeLine, 0.4f);
                Shapes.Fill(ci, Shapes.Capsule(new Vector2(-7.4f, -7.8f), new Vector2(-11.4f, -4.4f + w2), 0.85f), c.H(c.Hat.Darkened(0.25f)), dyeLine, 0.4f);
                Shapes.Fill(ci, Shapes.RoundRect(new Rect2(-7.7f, -9.4f, 15.4f, 2.5f), 1f), dye, dyeLine, 0.5f);
                ci.DrawLine(new Vector2(-6.5f, -8.9f), new Vector2(6.5f, -8.9f), dyeLight with { A = 0.6f }, 0.4f, true);
                break;
            }
            case 7: // Viking
            {
                var hornL = new[]
                {
                    new Vector2(-6.2f, -10.8f), new Vector2(-9.6f, -12.2f), new Vector2(-11.6f, -15.4f), new Vector2(-11.6f, -18.8f),
                    new Vector2(-10.1f, -16f), new Vector2(-7.7f, -14.2f), new Vector2(-5.4f, -13.2f),
                };
                var hornR = new Vector2[hornL.Length];
                for (int i = 0; i < hornL.Length; i++) hornR[i] = new Vector2(-hornL[i].X, hornL[i].Y);
                Shapes.Fill(ci, hornL, c.H(Bone), Bone.Darkened(0.5f), 0.55f);
                Shapes.Fill(ci, hornR, c.H(Bone), Bone.Darkened(0.5f), 0.55f);
                var dome = Dome(new Vector2(0, -9.6f), 7.8f, 5.6f);
                Shapes.Fill(ci, dome, dye, dyeLine, 0.7f);
                Shapes.Gradient(ci, dome, dyeLight, dye);
                Shapes.Fill(ci, Shapes.RoundRect(new Rect2(-8.2f, -11.2f, 16.4f, 2.3f), 0.9f), c.H(Metal), DarkMetal.Darkened(0.3f), 0.5f);
                for (float x = -6f; x <= 6f; x += 3f) ci.DrawCircle(new Vector2(x, -10.05f), 0.4f, c.H(DarkMetal), true, -1, true);
                break;
            }
            case 8: // Halo
            {
                float lift = Mathf.Sin(t * 2.6f) * 0.6f;
                var ring = Shapes.Ellipse(new Vector2(0, -14.8f + lift), 5.6f, 1.7f, 28);
                Shapes.DrawClosed(ci, ring, c.P.Hurt ? Colors.White : Shapes.Hdr(Gold, 1.7f), 1.1f);
                Shapes.DrawClosed(ci, ring, c.H(PaleYellow) with { A = 0.8f }, 0.4f);
                break;
            }
            case 9: // Propeller
            {
                var dome = Dome(new Vector2(0, -9.6f), 7f, 4.4f);
                Shapes.Fill(ci, dome, dye, dyeLine, 0.7f);
                Shapes.Gradient(ci, dome, dyeLight, dye);
                ci.DrawLine(new Vector2(0, -13.8f), new Vector2(0, -15.4f), Ink, 0.8f, true);
                float spin = Mathf.Cos(t * 22f);
                float bw = Mathf.Max(0.4f, 2.8f * Mathf.Abs(spin));
                Shapes.Fill(ci, Shapes.Ellipse(new Vector2(-2.8f * spin, -15.6f), bw, 0.7f, 12), c.H(Red), DarkRed, 0.35f);
                Shapes.Fill(ci, Shapes.Ellipse(new Vector2(2.8f * spin, -15.6f), bw, 0.7f, 12), c.H(Blue), Blue.Darkened(0.4f), 0.35f);
                ci.DrawCircle(new Vector2(0, -15.6f), 0.6f, c.H(Gold), true, -1, true);
                break;
            }
            case 10: // Chef
            {
                var line = OffWhite.Darkened(0.45f);
                var puffs = new[] { (new Vector2(-3.8f, -16.4f), 3.6f), (new Vector2(3.8f, -16.4f), 3.6f), (new Vector2(0, -18f), 4f) };
                foreach (var (pc, r) in puffs) ci.DrawCircle(pc, r + 0.6f, line, true, -1, true);
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-6.1f, -16.6f, 12.2f, 6.4f), 0.8f), line);
                foreach (var (pc, r) in puffs) ci.DrawCircle(pc, r, c.H(White), true, -1, true);
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-5.5f, -16.6f, 11f, 5.8f), 0.6f), c.H(White));
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-5.6f, -12.2f, 11.2f, 1.6f), 0.4f), dye);
                ci.DrawLine(new Vector2(-1.8f, -16f), new Vector2(-1.6f, -13f), line with { A = 0.5f }, 0.35f, true);
                ci.DrawLine(new Vector2(1.8f, -16f), new Vector2(1.6f, -13f), line with { A = 0.5f }, 0.35f, true);
                break;
            }
        }
    }

    // ---------- arms ----------
    // Two stubby arms in the body color, from the shoulders (GunRig.FrontShoulder / BackShoulder, sprite space) to the
    // hands on the grip and the foregrip. Drawn before the body, so the body hides their roots and they can never
    // cover the face; only the part reaching out past the body shows.

    private const float ArmRadius = 1.05f, ArmOutline = 0.55f;

    private static void DrawArms(Ctx c, int facing)
    {
        var p = c.P;
        var g = p.GunLook == default ? GunLook.Default : p.GunLook;
        var (hand, angle) = GunRig.Pose(SpriteGunAim(p, facing), p.Kick);
        var grip = G(GunRig.ToSprite(new System.Numerics.Vector2(0.6f, 0.2f), hand, angle));
        var fore = G(GunRig.ToSprite(g.ForegripLocal, hand, angle));
        var line = p.Hurt ? Colors.White : p.Body.Darkened(0.6f);
        Shapes.Fill(c.Ci, Shapes.Capsule(G(GunRig.BackShoulder), fore, ArmRadius), c.H(p.Body.Darkened(0.22f)), line, ArmOutline);
        Shapes.Fill(c.Ci, Shapes.Capsule(G(GunRig.FrontShoulder), grip, ArmRadius), c.H(p.Body.Darkened(0.08f)), line, ArmOutline);
    }

    // ---------- gun ----------

    private static void DrawGun(Ctx c, Vector2 bodyOrigin, float s, int facing)
    {
        var ci = c.Ci;
        var p = c.P;
        var (hand, angle) = GunRig.Pose(SpriteGunAim(p, facing), p.Kick);
        ci.DrawSetTransformMatrix(GunXform(bodyOrigin, G(hand), angle, s, facing));

        var g = p.GunLook == default ? GunLook.Default : p.GunLook;
        const float ow = 0.45f;
        float muzzle = g.MuzzleX + 1, bt = g.BarrelThick;

        if (g.Stock > 0)
            Shapes.Fill(ci, Shapes.RoundRect(new Rect2(-1.2f - g.Stock, -2.5f, g.Stock + 0.8f, 2.4f), 0.8f), c.H(GunDark), Ink, ow);
        Shapes.Fill(ci, new[] { new Vector2(-0.3f, -0.2f), new Vector2(2f, -0.2f), new Vector2(1.5f, 3.3f), new Vector2(-0.8f, 3.3f) }, c.H(GunDark), Ink, ow);
        if (g.Mag > 0)
            Shapes.Fill(ci, Shapes.RoundRect(new Rect2(3f, -0.4f, 1.9f, g.Mag + 0.6f), 0.5f), c.H(GunMid), Ink, ow);
        Shapes.Fill(ci, Shapes.RoundRect(new Rect2(g.Receiver - 0.8f, -bt, muzzle - g.Receiver + 0.8f, bt), bt * 0.35f), c.H(GunMid), Ink, ow);
        ci.DrawLine(new Vector2(g.Receiver, -bt + 0.35f), new Vector2(muzzle - 1f, -bt + 0.35f), c.H(GunLight), 0.4f, true);
        Shapes.Fill(ci, Shapes.RoundRect(new Rect2(muzzle - 1.3f, -bt - 0.35f, 1.4f, bt + 0.7f), 0.4f), c.H(GunDark), Ink, ow);
        var receiver = Shapes.RoundRect(new Rect2(-1.2f, -3.3f, g.Receiver + 0.6f, 3.6f), 1.1f);
        Shapes.Fill(ci, receiver, c.H(GunMid), Ink, ow);
        Shapes.Gradient(ci, receiver, c.H(GunLight), c.H(GunDark));
        ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(0.4f, -2.2f, g.Receiver - 1.6f, 0.9f), 0.45f), c.H(p.Body));
        if (g.Scope)
        {
            Shapes.Fill(ci, Shapes.RoundRect(new Rect2(0.8f, -5.5f, g.Receiver - 1.4f, 1.9f), 0.8f), c.H(GunDark), Ink, ow);
            ci.DrawCircle(new Vector2(g.Receiver - 0.8f, -4.55f), 0.55f, c.H(Shapes.Hdr(Visor, 1.3f)), true, -1, true);
        }

        // The hands on the grip and the foregrip, in the body color (the arms behind the body reach these).
        var handLine = p.Hurt ? Colors.White : p.Body.Darkened(0.6f);
        var fore = G(g.ForegripLocal);
        Shapes.Fill(ci, Shapes.Ellipse(fore, 1.3f, 1.2f, 14), c.H(p.Body.Darkened(0.12f)), handLine, 0.5f);
        Shapes.Fill(ci, Shapes.Ellipse(new Vector2(0.6f, 0.2f), 1.45f, 1.35f, 14), c.Body, handLine, 0.5f);
        ci.DrawCircle(new Vector2(0.2f, -0.5f), 0.5f, new Color(1, 1, 1, 0.4f), true, -1, true);

        if (p.Flash > 0)
        {
            var at = new Vector2(muzzle + 0.6f, -bt * 0.5f);
            float k = p.Flash;
            GlowTex.Draw(ci, at + new Vector2(2, 0), 6f + 6f * k, PaleYellow with { A = 0.7f * k });
            ci.DrawColoredPolygon(Shapes.Star(at + new Vector2(1.6f * k, 0), 2.2f + 3.4f * k, 1f + 0.6f * k, 5, k * 2), Shapes.Hdr(PaleYellow, 2.2f) with { A = Mathf.Min(1, k * 1.6f) });
            ci.DrawCircle(at + new Vector2(1f, 0), 1.2f * k + 0.4f, Shapes.Hdr(Colors.White, 2.5f), true, -1, true);
        }
    }
}
