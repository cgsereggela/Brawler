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
    public float WalkPhase;   // stride cycle in radians (one full cycle = two steps), advanced with distance walked
    public bool Grounded, Moving, Blink, Hurt;
    // Leg animation (cosmetic). All optional: left at 0, Grounded + Moving give a full walk and !Grounded a full air pose.
    public float Walk;        // 0..1 stride strength (eases in and out with speed)
    public int MoveDir;       // world direction of travel: -1 left, +1 right (0 = forward)
    public float Air;         // 0..1 blend from the ground pose to the air pose
    public float VelY;        // world vertical speed (px/s): rising tucks a knee, falling dangles the legs
    public float Wall;        // 0..1 wall-slide blend
    public int WallSide;      // world side the wall is on: -1 left, +1 right
    public float GroundDrop;  // sprite units the ground sits below the hitbox bottom under the body's center (slopes)
    public float GroundSlope; // ground dy/dx in world space (positive = descending to the right)
    public float Land;        // 0..1 landing squash, decays after touching down
    public bool Gun;
    public GunLook GunLook;
    public float Kick;        // 0..1 recoil
    public float Flash;       // 0..1 muzzle flash
}

/// <summary>Gun shape parameters. Every gun is built from these, so cards can later swap in a different look.</summary>
public readonly record struct GunLook(int Receiver = 5, int Barrel = 6, int BarrelThick = 2, int Mag = 0, bool Scope = false, int Stock = 0)
{
    // Spelled out: new() on a record struct skips the primary constructor and gives all zeros.
    public static readonly GunLook Default = new(Receiver: 5, Barrel: 6, BarrelThick: 2, Mag: 0, Scope: false, Stock: 0);
    public int MuzzleX => Receiver + Barrel - 1;
}

/// <summary>
/// Vector art for players: body, face parts, hair, hats and the gun, built from smooth polygons so they stay crisp
/// at any resolution. Sprite space faces right with the hitbox centered on (0,0) and covering x -7..7, y -9..9: the
/// round body fills the top (y -9.5..BodyBottom) and two short legs reach down to the soles on y = 9. Option indices
/// follow the name lists in <see cref="Appearance"/>.
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

    /// <summary>Where the hand holds the gun, relative to the body center (world units, unrotated).</summary>
    public static Vector2 GunPivot(Vector2 aim, float scale) => (new Vector2(0, 3f) + aim * 5f) * scale;

    /// <summary>Muzzle position relative to the body center, matching what Draw renders with no recoil.</summary>
    public static Vector2 Muzzle(Vector2 aim, int facing, float scale, GunLook g)
        => GunXform(GunPivot(aim, scale), aim.Angle(), scale, facing) * new Vector2(g.MuzzleX + 1, -g.BarrelThick * 0.5f);

    /// <summary>Gun space to body space: mirror the gun's y when facing left, then rotate to the aim, then move to the hand.</summary>
    private static Transform2D GunXform(Vector2 pivot, float angle, float scale, int facing)
        => new(angle, new Vector2(scale, facing < 0 ? -scale : scale), 0, pivot);

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

        var rig = Solve(p, facing);
        var scale = new Vector2(facing * s, s);
        ci.DrawSetTransform(origin, 0, scale);
        DrawLegs(c, rig);

        var bodyAt = origin + new Vector2(rig.Body.X * facing, rig.Body.Y) * s;
        ci.DrawSetTransform(bodyAt + new Vector2(0, -rig.Breathe * s), 0, scale);
        DrawHairBack(c, look.Hair);
        DrawBody(c, rig.Breathe);
        DrawEyes(c, look.Eyes, facing);
        DrawNose(c, look.Nose);
        DrawMouth(c, look.Mouth);
        DrawHair(c, look.Hair);
        DrawHat(c, look.Hat);

        if (p.Gun) DrawGun(c, bodyAt, s, facing);
        ci.DrawSetTransformMatrix(Transform2D.Identity);
    }

    // ---------- body and legs ----------

    // The round body rides on two short legs. In sprite space the body spans y -9.5..BodyBottom and the soles rest
    // on y = 9, the bottom of the hitbox. Everything above the hips (body, face, hair, hat, gun) moves together by
    // BodyOffset, so add-ons that hang off the body (arms) should follow BodyOffset too.
    public const float BodyTop = -9.5f, BodyBottom = 3.4f;
    private const float HipX = 2.2f, HipY = 1.4f, SoleY = 9f, ShoeRx = 2.45f, ShoeRy = 1.4f, LegLen = 5.8f, LegR = 1.1f;
    private const float FootX = 2.5f, StrideX = 2.5f, StepLift = 2.2f;

    /// <summary>Middle of the body in sprite space before <see cref="BodyOffset"/> (faces right, unscaled).</summary>
    public static readonly Vector2 TorsoAnchor = new(0, (BodyTop + BodyBottom) * 0.5f);

    /// <summary>
    /// How far the upper body (and everything drawn on it, gun included) is moved this frame by the walk bob, breathing,
    /// landing squash, slopes and the wall-slide lean. World units relative to the draw origin (facing and scale applied).
    /// </summary>
    public static Vector2 BodyOffset(AvatarPose p)
    {
        int facing = p.Facing < 0 ? -1 : 1;
        float s = p.Scale <= 0 ? 1 : p.Scale;
        var rig = Solve(p, facing);
        return new Vector2(rig.Body.X * facing, rig.Body.Y - rig.Breathe) * s;
    }

    private struct Rig
    {
        public Vector2 Body;            // upper-body offset, sprite space
        public float Breathe;
        public Vector2 Back, Front;     // shoe centers, sprite space
        public float BackTilt, FrontTilt;
        public float BackToe, FrontToe; // which way each toe points (+1 forward)
    }

    /// <summary>Pose → body offset and foot placement. Pure function of the pose, so previews and BodyOffset agree.</summary>
    private static Rig Solve(in AvatarPose p, int facing)
    {
        float air = p.Air > 0 ? Mathf.Clamp(p.Air, 0, 1) : p.Grounded ? 0 : 1;
        float walk = (p.Walk > 0 ? Mathf.Clamp(p.Walk, 0, 1) : p.Grounded && p.Moving ? 1 : 0) * (1 - air);
        float wall = Mathf.Clamp(p.Wall, 0, 1) * air;
        float ground = 1 - air;
        int dir = (p.MoveDir < 0 ? -1 : 1) * facing; // sprite-space direction of travel
        float slope = Mathf.Clamp(p.GroundSlope, -1.2f, 1.2f) * facing;
        float drop = Mathf.Clamp(p.GroundDrop, 0, 8);
        float phase = p.WalkPhase;

        var rig = new Rig { BackToe = 1, FrontToe = 1 };
        rig.Breathe = ground * (1 - walk) * Mathf.Sin(p.Time * 2.4f) * 0.25f;

        // Ground: planted feet when idle; when walking each foot sweeps back along the ground (stance) and lifts
        // forward (swing), the two half a cycle apart. Each sole sits on the ground line under it, so on a ramp the
        // downhill foot reaches further down.
        (Vector2 at, float tilt) OnGround(float restX, float ph)
        {
            float x = restX + dir * StrideX * Mathf.Sin(ph) * walk;
            float lift = Mathf.Max(0, Mathf.Cos(ph)) * StepLift * walk;
            float under = Mathf.Clamp(drop + slope * x, -0.5f, 9f);
            float tilt = Mathf.Atan(slope) - dir * lift * 0.12f;
            return (new Vector2(x, SoleY - ShoeRy + under - lift), tilt);
        }
        var (gb, gbt) = OnGround(-FootX, phase);
        var (gf, gft) = OnGround(FootX, phase + Mathf.Pi);
        // Legs pass under the body at the top of the bob, the body is lowest with the feet spread.
        float bob = -walk * (0.2f + 0.9f * Mathf.Abs(Mathf.Cos(phase)));
        var body = new Vector2(0, bob + ground * (0.45f * drop + 1.6f * Mathf.Clamp(p.Land, 0, 1)));

        // Air: rising tucks the front knee up, the apex is neutral, falling lets both legs dangle and kick.
        float t = Mathf.Clamp(p.VelY / 260f, -1, 1);
        Vector2 ab = new(-2.4f, 7.3f), af = new(2.9f, 6.6f);
        float abt = 0.25f, aft = 0.1f;
        if (t < 0)
        {
            ab = ab.Lerp(new Vector2(-2.2f, 7.9f), -t);
            af = af.Lerp(new Vector2(3.3f, 5.0f), -t);
            abt += 0.35f * -t;
            body.Y += air * 0.6f * t;
        }
        else
        {
            float kick = Mathf.Sin(p.Time * 13f) * 0.5f * t;
            ab = ab.Lerp(new Vector2(-3.1f, 8.4f + kick), t);
            af = af.Lerp(new Vector2(2.6f, 8.0f - kick), t);
            abt += 0.2f * t;
            aft += 0.25f * t;
        }

        rig.Back = gb.Lerp(ab, air);
        rig.Front = gf.Lerp(af, air);
        rig.BackTilt = Mathf.Lerp(gbt, abt, air);
        rig.FrontTilt = Mathf.Lerp(gft, aft, air);

        // Wall slide: soles flat against the wall (feet turned up it), body leaning away.
        if (wall > 0)
        {
            int side = (p.WallSide < 0 ? -1 : 1) * facing; // +1 = wall in front of the face
            var high = new Vector2(side * 5.4f, 4.9f);
            var low = new Vector2(side * 4.6f, 8.0f);
            float up = -side * Mathf.Pi * 0.5f;
            if (side > 0)
            {
                rig.Front = rig.Front.Lerp(high, wall);
                rig.Back = rig.Back.Lerp(low, wall);
            }
            else
            {
                rig.Back = rig.Back.Lerp(high, wall);
                rig.Front = rig.Front.Lerp(low, wall);
                rig.BackToe = rig.FrontToe = -1;
            }
            rig.FrontTilt = Mathf.Lerp(rig.FrontTilt, up, wall);
            rig.BackTilt = Mathf.Lerp(rig.BackTilt, up, wall);
            body += new Vector2(-side * 0.9f, -0.3f) * wall;
        }
        rig.Body = body;
        return rig;
    }

    private static void DrawLegs(Ctx c, Rig rig)
    {
        var b = c.P.Body;
        var line = c.P.Hurt ? Colors.White : b.Darkened(0.68f);
        Leg(c, rig, -HipX, rig.Back, rig.BackTilt, rig.BackToe, c.H(b.Darkened(0.3f)), c.H(b.Darkened(0.55f)), line);
        Leg(c, rig, HipX, rig.Front, rig.FrontTilt, rig.FrontToe, c.H(b.Darkened(0.16f)), c.H(b.Darkened(0.45f)), line);
    }

    private static void Leg(Ctx c, Rig rig, float hipX, Vector2 foot, float tilt, float toe, Color leg, Color shoe, Color line)
    {
        var ci = c.Ci;
        var hip = rig.Body + new Vector2(hipX, HipY - rig.Breathe);
        var rot = Vector2.FromAngle(tilt);
        var up = new Vector2(rot.Y, -rot.X); // the shoe's "up", away from the sole
        var ankle = foot + up * (ShoeRy * 0.45f) - rot * (toe * 0.5f);
        // Two-bone leg: when the foot is closer than the leg is long, the knee bends forward.
        var d = ankle - hip;
        float len = d.Length();
        var knee = (hip + ankle) * 0.5f;
        if (len < LegLen && len > 0.01f)
        {
            var perp = new Vector2(-d.Y, d.X) / len;
            if (perp.X < 0) perp = -perp;
            knee += perp * Mathf.Sqrt(LegLen * LegLen * 0.25f - len * len * 0.25f);
        }
        Shapes.Fill(ci, Shapes.Capsule(hip, knee, LegR), leg, line, 0.45f);
        Shapes.Fill(ci, Shapes.Capsule(knee, ankle, LegR * 0.95f), leg, line, 0.45f);

        // Shoe: a rounded sole-down oval, toe pushed forward, rotated to the ground (or wall) under it.
        var pts = Shapes.Ellipse(new Vector2(toe * 0.55f, 0), ShoeRx, ShoeRy, 16);
        for (int i = 0; i < pts.Length; i++)
        {
            var q = pts[i];
            if (q.Y > 0) q.Y *= 0.7f; // flatter sole
            pts[i] = foot + rot * q.X + up * -q.Y;
        }
        Shapes.Fill(ci, pts, shoe, line, 0.5f);
        ci.DrawLine(foot + rot * (toe * -0.6f) + up * 0.75f, foot + rot * (toe * 1.6f) + up * 0.75f, new Color(1, 1, 1, 0.25f), 0.4f, true);
    }

    private static void DrawBody(Ctx c, float breathe)
    {
        var body = Shapes.RoundRect(new Rect2(-7f - breathe * 0.4f, BodyTop, 14f + breathe * 0.8f, BodyBottom - BodyTop), 6.2f, 6);
        Shapes.Fill(c.Ci, body, c.Body, c.P.Hurt ? Colors.White : c.P.Body.Darkened(0.6f), 0.9f);
        Shapes.Gradient(c.Ci, body, c.H(c.P.Body.Lightened(0.16f)), c.H(c.P.Body.Darkened(0.24f)));
        // Glossy highlight and a soft belly shade.
        c.Ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-3.4f, -6.4f), 2.4f, 1.3f, 14), new Color(1, 1, 1, 0.38f));
        c.Ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-4.6f, -4.6f), 0.7f, 0.6f, 10), new Color(1, 1, 1, 0.45f));
        c.Ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(0.6f, BodyBottom - 1.6f), 5f, 1.4f, 18), new Color(0, 0, 0, 0.08f));
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
        new(-8.6f, 2.2f), new(-8.8f, -6f), new(-7.5f, -9.2f), new(-4.7f, -11f), new(0f, -11.5f), new(4.7f, -11f), new(7.3f, -9f), new(8.1f, -6.4f),
        new(6.2f, -7.2f), new(4.5f, -6.6f), new(3f, -7.6f), new(1.2f, -6.9f), new(-0.8f, -7.8f), new(-3f, -7.2f), new(-5f, -6.4f),
        new(-5.7f, -2f), new(-5.5f, 2.4f), new(-7f, 3.6f),
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
                ci.DrawLine(new Vector2(-7.4f, -4f), new Vector2(-7.2f, 1.4f), shine, 0.5f, true);
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

    // ---------- gun ----------

    private static void DrawGun(Ctx c, Vector2 origin, float s, int facing)
    {
        var ci = c.Ci;
        var p = c.P;
        var aim = p.GunAim.LengthSquared() > 0.01f ? p.GunAim.Normalized()
            : p.Aim.LengthSquared() > 0.01f ? p.Aim.Normalized() : new Vector2(facing, 0);
        float angle = aim.Angle() - p.Kick * 0.35f * facing;
        var pivot = origin + GunPivot(aim, s) - aim * (p.Kick * 2.5f * s);
        // Flip the gun's own y (before rotating) when facing left so the grip always hangs down. Built as an explicit
        // matrix: scaling after the rotation would mirror the aim angle instead and point the barrel up/down-swapped.
        ci.DrawSetTransformMatrix(GunXform(pivot, angle, s, facing));

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

        // The hand on the grip, in the body color.
        Shapes.Fill(ci, Shapes.Ellipse(new Vector2(0.6f, 0.2f), 1.45f, 1.35f, 14), c.Body, p.Hurt ? Colors.White : p.Body.Darkened(0.6f), 0.5f);
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
