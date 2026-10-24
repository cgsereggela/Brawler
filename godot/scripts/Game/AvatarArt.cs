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
    public Vector2 ArmSway;   // sprite units the elbow trails behind its rest spot (springy follow-through; 0 = at rest)
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
    private static readonly Color White = new("#ece6d8"), OffWhite = new("#c9c2b4");
    private static readonly Color Red = new("#9a3f42"), DarkRed = new("#5e2428");
    private static readonly Color Pink = new("#c08495"), DarkPink = new("#7d4d5c");
    private static readonly Color Orange = new("#c27e45"), DarkOrange = new("#7d4a22");
    private static readonly Color Gold = new("#c2a35c"), DarkGold = new("#7f6630"), PaleYellow = new("#e3d6a6");
    private static readonly Color Blue = new("#4d6d9a"), Metal = new("#8b8f99"), DarkMetal = new("#4e525e");
    private static readonly Color Bone = new("#f1e6c8"), HatBlack = new("#1e1c24"), HatGrey = new("#45424e");
    private static readonly Color Visor = new("#3de6e0");
    private static readonly Color GunLight = new("#8f8b85"), GunMid = new("#55525a"), GunDark = new("#2c2a31");

    // Top of each hair style and hat in sprite space, for placing the HP bar.
    private static readonly float[] HairTop = { -9.5f, -11.3f, -16.2f, -16.4f, -11.5f, -11.8f, -11.3f, -18.8f };
    private static readonly float[] HatTop = { -9.5f, -14.6f, -21.6f, -18.5f, -17.7f, -22f, -9.5f, -19f, -17.4f, -16.4f, -22.6f, -24.5f, -12.5f, -16.5f, -16.8f };
    // How far each hat sits below where it is drawn, so it is worn over the top of the mask rather than perched on it.
    private static readonly float[] HatDrop = { 0, 2.2f, 2.2f, 2f, 2.2f, 2.2f, 1.2f, 2f, 1f, 2.2f, 2.2f, 2.2f, 1f, 1.5f, 2.2f };

    /// <summary>Topmost point used by this look (hair and hat), for placing the HP bar.</summary>
    public static float TopY(Appearance look)
    {
        int hat = Math.Clamp(look.Hat, 0, HatTop.Length - 1), hair = Math.Clamp(look.Hair, 0, HairTop.Length - 1);
        float hairTop = Covers(hat) || (hair == 3 && HidesMohawk(hat)) ? Mathf.Max(HairTop[hair], -11.8f) : HairTop[hair]; // a covering hat tucks tall hair away
        return Mathf.Min(hairTop, HatTop[hat] + HatDrop[hat] - HatLift(hat, hair));
    }

    /// <summary>
    /// Hats that sit over the whole crown. Under one of these, hair that would stand up through it is tucked in
    /// instead: a mohawk is hidden, spikes stick out at the sides and an afro puffs out under the brim.
    /// </summary>
    private static bool Covers(int hat) => hat is 1 or 2 or 3 or 7 or 9 or 10 or 11 or 14;

    /// <summary>Hats a mohawk hides under: the covering ones, and the crown and party hat, which would only perch on it.</summary>
    private static bool HidesMohawk(int hat) => Covers(hat) || hat is 4 or 5;

    /// <summary>
    /// How far a hat that sits on top of the head (crown, party hat, halo, horns) rises to sit on tall hair rather
    /// than sink into it, in sprite units.
    /// </summary>
    private static float HatLift(int hat, int hair) => (hat, hair) switch
    {
        (4, 7) => 6f, (4, 2) => 1.5f,   // Crown on an afro, spikes
        (5, 7) => 6.5f, (5, 2) => 2f,   // Party hat
        (8, _) => Mathf.Max(0, -14.8f + HatDrop[8] - (HairTop[Math.Clamp(hair, 0, HairTop.Length - 1)] - 1.6f)), // Halo floats clear of the hair
        (13, 7) => 2f,                                     // Horns poke out of an afro
        _ => 0,
    };

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

    /// <summary>
    /// One character's drawing state. Every shape goes into one triangle batch (with the draw transform applied to
    /// its points), so a character costs one draw call instead of about a hundred and fifty; only the glows, which
    /// need a texture, are drawn on their own, in order.
    /// </summary>
    private sealed class Ctx
    {
        public CanvasItem Ci;
        public readonly TriBatch B = new();
        public AvatarPose P;
        public Color Body, Hair, Hat;
        public Color Mask, MaskShade; // the bone mask, tinted toward the player's color
        public Color H(Color c) => P.Hurt ? Colors.White : c;

        public void SetTransform(Vector2 at, float rotation, Vector2 scale) => B.Transform = new Transform2D(rotation, scale, 0, at);
        public void SetMatrix(Transform2D t) => B.Transform = t;
        public void Poly(Vector2[] pts, Color col) => B.Polygon(pts, col);
        public void Gradient(Vector2[] pts, Color top, Color bottom) => B.Gradient(pts, top, bottom);
        public void Line(Vector2 a, Vector2 b, Color col, float width) => B.Line(a, b, width, col);
        public void Closed(Vector2[] pts, Color col, float width) => B.Stroke(pts, width, col, closed: true);
        public void Arc(Vector2 center, float r, float a0, float a1, int segments, Color col, float width) => B.Arc(center, r, a0, a1, segments, col, width);
        public void Circle(Vector2 center, float r, Color col) => B.Circle(center, r, col, Mathf.Clamp(Mathf.CeilToInt(r * 5), 8, 28));

        /// <summary>A filled polygon with an outline <paramref name="width"/> wide around it, like <see cref="Shapes.Fill"/>.</summary>
        public void Fill(Vector2[] pts, Color fill, Color outline, float width)
        {
            B.Outline(pts, width, outline);
            if (fill.A < 0.999f) B.Polygon(pts, outline); // Shapes.Fill lays the outline color under the whole shape
            B.Polygon(pts, fill);
        }

        public void Glow(Vector2 at, float radius, Color col)
        {
            B.Flush();
            Ci.DrawSetTransformMatrix(B.Transform);
            GlowTex.Draw(Ci, at, radius, col);
            Ci.DrawSetTransformMatrix(Transform2D.Identity);
        }
    }

    [ThreadStatic] private static Ctx _ctx;

    /// <summary>
    /// The soft edge on round bits (sprite units), matching the feather Godot gave antialiased circles when they were
    /// drawn one by one. Lines and outlines get Godot's soft line edges too (TriBatch.SoftLines): that is what gives
    /// characters their soft dark rim.
    /// </summary>
    private const float CircleFeather = 0.5f;

    public static void Draw(CanvasItem ci, Vector2 origin, AvatarPose p)
    {
        var look = p.Look ?? Appearance.Default();
        float s = p.Scale <= 0 ? 1 : p.Scale;
        int facing = p.Facing < 0 ? -1 : 1;
        var c = _ctx ??= new Ctx();
        c.Ci = ci;
        c.B.Begin(ci);
        c.B.Feather = CircleFeather;
        c.B.SoftLines = true;
        c.P = p;
        c.Body = p.Hurt ? Colors.White : p.Body;
        c.Hair = Palette.Dye(look.HairColor);
        c.Hat = Palette.Dye(look.HatColor);
        // A wash of the player's color over the bone, so masks tell players apart as well as cloaks.
        c.Mask = MaskBone.Lerp(p.Body, 0.15f);
        c.MaskShade = MaskShade.Lerp(p.Body, 0.1f);

        var rig = Solve(p, facing);
        var scale = new Vector2(facing * s, s);
        c.SetTransform(origin, 0, scale);
        DrawLegs(c, rig);

        // Everything above the hips (arms, body, face, hair, hat, gun) is drawn around the animated torso origin.
        var bodyOrigin = origin + BodyOffset(p);
        c.SetTransform(bodyOrigin, 0, scale);
        if (p.Gun) DrawArm(c, facing); // behind the body, in body (sprite) space, so it rides along with it
        DrawHairBack(c, look.Hair);
        DrawBody(c, rig.Breathe);
        DrawEyes(c, look.Eyes, facing);
        DrawMouth(c, look.Mouth, bodyOrigin, scale);
        int hat = Math.Clamp(look.Hat, 0, HatDrop.Length - 1);
        DrawHair(c, look.Hair, hat);
        // Hats are drawn with their brim on the crown; HatDrop pulls each one down so it fits over the mask, and HatLift
        // raises the ones that sit on top of the head onto tall hair.
        c.SetTransform(bodyOrigin + new Vector2(0, (HatDrop[hat] - HatLift(hat, look.Hair)) * s), 0, scale);
        DrawHat(c, look.Hat);
        c.SetTransform(bodyOrigin, 0, scale);

        if (p.Gun) DrawGun(c, bodyOrigin, s, facing);
        c.B.Flush();
    }

    // ---------- body and legs ----------

    // The round body rides on two short legs. In sprite space the body spans y -9.5..BodyBottom and the soles rest
    // on y = 9, the bottom of the hitbox. Everything above the hips (body, face, hair, hat, gun) moves together by
    // BodyOffset, so add-ons that hang off the body (the arm) should follow BodyOffset too.
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

    /// <summary>Pose â†’ body offset and foot placement. Pure function of the pose, so previews and BodyOffset agree.</summary>
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
        var body = new Vector2(0, bob + ground * (0.3f * drop + 1.6f * Mathf.Clamp(p.Land, 0, 1)));

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
        // Thin dark legs under the cloak, and darker boots.
        var cloak = Cloak(c.P.Body);
        var boot = Hollow.Lerp(cloak, 0.3f);
        Leg(c, rig, -HipX, rig.Back, rig.BackTilt, rig.BackToe, c.H(Shade(cloak.Darkened(0.3f), 0.35f)), c.H(boot), Ink);
        Leg(c, rig, HipX, rig.Front, rig.FrontTilt, rig.FrontToe, c.H(Shade(cloak.Darkened(0.15f), 0.2f)), c.H(Lit(boot, 0.12f)), Ink);
    }

    private static void Leg(Ctx c, Rig rig, float hipX, Vector2 foot, float tilt, float toe, Color leg, Color shoe, Color line)
    {
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
        c.Fill(Shapes.Capsule(hip, knee, LegR), leg, line, 0.45f);
        c.Fill(Shapes.Capsule(knee, ankle, LegR * 0.95f), leg, line, 0.45f);

        // Shoe: a rounded sole-down oval, toe pushed forward, rotated to the ground (or wall) under it.
        var pts = Shapes.Ellipse(new Vector2(toe * 0.55f, 0), ShoeRx, ShoeRy, 16);
        for (int i = 0; i < pts.Length; i++)
        {
            var q = pts[i];
            if (q.Y > 0) q.Y *= 0.7f; // flatter sole
            pts[i] = foot + rot * q.X + up * -q.Y;
        }
        c.Fill(pts, shoe, line, 0.5f);
        c.Line(foot + rot * (toe * -0.6f) + up * 0.75f, foot + rot * (toe * 1.6f) + up * 0.75f, new Color(1, 1, 1, 0.3f), 0.4f);
        c.Line(hip.Lerp(knee, 0.35f) + new Vector2(0.45f, 0), hip.Lerp(knee, 0.8f) + new Vector2(0.45f, 0), c.H(Lit(leg, 0.35f)) with { A = 0.5f }, 0.35f); // thigh highlight
        c.Line(foot + rot * (toe * 1.3f) + up * 0.1f, foot + rot * (toe * 1.3f) + up * 1.2f, line with { A = 0.4f }, 0.35f); // toe cap seam
    }

    // A pale bone mask over a dark cloak, in the manner of a hollow little bug knight. The cloak takes a dark shade of
    // the player's color and a collar in the full color, so players stay easy to tell apart; the mask is the same for
    // everyone and carries the face. The mask spans y -9.5..MaskBottom, the cloak hangs from under it to a ragged hem.
    private const float MaskBottom = 1.35f;
    private static readonly Color MaskBone = new("#ece6d8"), MaskShade = new("#aaa396"), MaskDeep = new("#7d776d");
    private static readonly Color Hollow = new("#0c0a10");

    /// <summary>The cloak's color for a player color: darker and a little greyer, like dyed cloth in shadow.</summary>
    private static Color Cloak(Color body) => body.Lerp(new Color("#4a4652"), 0.15f).Darkened(0.3f);

    private static readonly Vector2[] MaskOutline = BuildMask();

    /// <summary>The mask's outline in sprite space (for the app icon).</summary>
    public static Vector2[] MaskShape() => (Vector2[])MaskOutline.Clone();

    public static readonly Color InkColor = Ink, HollowColor = Hollow, MaskBoneColor = MaskBone, MaskShadeColor = MaskShade;

    /// <summary>A wide dome tapering to a short, narrower jaw.</summary>
    private static Vector2[] BuildMask()
    {
        const float cy = -3.3f, rx = 6.7f, ry = 6.2f;
        var pts = new Vector2[32];
        for (int i = 0; i < pts.Length; i++)
        {
            var u = Vector2.FromAngle(i * Mathf.Tau / pts.Length);
            float y = u.Y * ry, x = u.X * rx;
            if (y > 0)
            {
                float t = y / ry;
                y *= (MaskBottom - cy) / ry;
                x *= 1 - 0.2f * t * t;
            }
            pts[i] = new Vector2(x, cy + y);
        }
        return pts;
    }

    private static readonly Vector2[] CloakOutline =
    {
        new(-4.6f, -1.6f), new(4.8f, -1.6f), new(6.6f, 1.8f), new(7.3f, 4.1f), new(5.3f, 3.3f), new(3.5f, 4.4f),
        new(1.3f, 3.5f), new(-1.1f, 4.5f), new(-3.3f, 3.4f), new(-5.4f, 4.3f), new(-7.2f, 3.7f), new(-6.6f, 1.4f),
    };

    private static void DrawBody(Ctx c, float breathe)
    {
        var p = c.P;
        var cloak = Cloak(p.Body);
        // Breathing swells the cloak a little at the sides.
        var cl = new Vector2[CloakOutline.Length];
        for (int i = 0; i < cl.Length; i++) cl[i] = CloakOutline[i] * new Vector2(1 + breathe * 0.06f, 1);
        c.Fill(cl, c.H(cloak), Ink, 0.9f);
        c.Gradient(cl, c.H(Lit(cloak, 0.2f)), c.H(Shade(cloak, 0.5f)));
        // The back (away from the light) falls into shadow, the front edge catches a little light.
        c.Poly(new[] { new Vector2(-4.6f, -1.2f), new Vector2(-2.6f, -1.2f), new Vector2(-3.4f, 3.2f), new Vector2(-5.4f, 4.1f), new Vector2(-7f, 3.5f), new Vector2(-6.4f, 1.4f) }, c.H(Shade(cloak, 0.6f)) with { A = 0.45f });
        c.Line(new Vector2(6.1f, 1.6f), new Vector2(6.8f, 3.6f), c.H(Lit(cloak, 0.45f)) with { A = 0.6f }, 0.4f);
        // A few folds, curved by the cloth's weight.
        var fold = Ink with { A = 0.35f };
        c.Line(new Vector2(-2.4f, 0.6f), new Vector2(-3.1f, 3.6f), fold, 0.4f);
        c.Line(new Vector2(2.6f, 0.8f), new Vector2(3.3f, 3.9f), fold, 0.4f);
        c.Line(new Vector2(0.2f, 1.2f), new Vector2(0.1f, 3.4f), fold with { A = 0.22f }, 0.35f);
        c.Line(new Vector2(-1.7f, 0.9f), new Vector2(-1.5f, 3.2f), c.H(Lit(cloak, 0.4f)) with { A = 0.3f }, 0.35f);
        // The collar in the player's own color.
        var collar = Shapes.RoundRect(new Rect2(-6.2f, -1.2f, 12.6f, 2.4f), 1.2f);
        var collarCol = p.Body.Lerp(c.MaskShade, 0.12f);
        c.Fill(collar, c.H(Shade(collarCol, 0.25f)), Ink, 0.55f);
        c.Gradient(collar, c.H(Lit(collarCol, 0.3f)), c.H(Shade(collarCol, 0.35f)));
        c.Line(new Vector2(-5f, -0.6f), new Vector2(5.2f, -0.6f), c.H(Lit(p.Body, 0.5f)) with { A = 0.55f }, 0.35f);
        c.Circle(new Vector2(0.4f, 0.55f), 0.55f, c.H(Gold.Darkened(0.2f))); // clasp
        c.Circle(new Vector2(0.3f, 0.4f), 0.3f, c.H(PaleYellow));

        // The mask: bone white lit from the upper front. A copy of the outline pulled toward the lit corner sits on top
        // of a shadow-colored base, so the shadow runs along the back and the jaw like the mask's own curve.
        c.Fill(MaskOutline, c.H(Shade(c.MaskShade, 0.2f)), Ink, 1.0f);
        c.Gradient(LitCopy(MaskOutline, 0.1f), c.H(Lit(c.Mask, 0.15f)), c.H(c.Mask.Lerp(c.MaskShade, 0.45f)));
        // A brow sheen that follows the top-front contour.
        var sheen = new List<Vector2>();
        for (int i = 22; i <= 29; i++) sheen.Add(new Vector2(0, -3.3f) + (MaskOutline[i] - new Vector2(0, -3.3f)) * 0.86f);
        for (int i = 0; i + 1 < sheen.Count; i++)
        {
            float k = Mathf.Sin((i + 0.5f) / (sheen.Count - 1) * Mathf.Pi);
            c.Line(sheen[i], sheen[i + 1], c.P.Hurt ? Colors.White : new Color(1, 1, 1, 0.12f + 0.3f * k), 0.5f + 0.5f * k);
        }
    }

    // ---------- eyes ----------
    // Hollow black eye holes in the mask. They lean a little toward the aim, so you can see where someone looks.

    private static void DrawEyes(Ctx c, int style, int facing)
    {
        var p = c.P;
        float ax = Mathf.Clamp(p.Aim.X * facing, -1, 1), ay = Mathf.Clamp(p.Aim.Y, -1, 1);
        var look = new Vector2(ax * 0.35f, ay * 0.35f);
        var hole = c.H(Hollow);
        Vector2 l = new(-2.1f, -4.3f), r = new(2.8f, -4.3f);

        // A hollow: a tall oval, optionally tilted, squashed shut on a blink.
        void Eye(Vector2 at, float rx, float ry, float tilt = 0)
        {
            if (p.Blink) ry = Mathf.Min(ry, 0.35f);
            var pts = Shapes.Ellipse(Vector2.Zero, rx, ry, 18);
            var rot = Vector2.FromAngle(tilt);
            for (int i = 0; i < pts.Length; i++)
                pts[i] = at + look + new Vector2(pts[i].X * rot.X - pts[i].Y * rot.Y, pts[i].X * rot.Y + pts[i].Y * rot.X);
            c.Poly(pts, hole);
            if (!p.Blink && !p.Hurt && Mathf.Min(rx, ry) > 1f) // a small glint, so the hollow looks wet rather than flat
                c.Circle(at + look + new Vector2(rx * 0.38f, -ry * 0.42f), Mathf.Min(rx, ry) * 0.24f, new Color(1, 1, 1, 0.5f));
        }

        // Covers the top of a hollow with mask (a lid, a scowl), and carves its edge.
        void Lid(Vector2 a, Vector2 b, float above)
        {
            // Keep the top corners inside the mask, so a scowl never pokes past a bald head.
            Vector2 Top(Vector2 q)
            {
                float edge = -3.3f - 6.2f * Mathf.Sqrt(Mathf.Max(0, 1 - Mathf.Pow(q.X / 6.7f, 2)));
                return new Vector2(q.X, Mathf.Max(q.Y - above, edge + 0.7f));
            }
            c.Poly(new[] { a, b, Top(b), Top(a) }, c.H(c.Mask.Darkened(0.04f)));
            c.Line(a, b, Ink, 0.6f);
        }

        switch (style)
        {
            case 0: // Round
                Eye(l, 1.5f, 2.3f);
                Eye(r, 1.5f, 2.3f);
                break;
            case 1: // Big
                Eye(l + new Vector2(-0.3f, -0.2f), 1.95f, 2.85f);
                Eye(r + new Vector2(0.2f, -0.2f), 1.95f, 2.85f);
                break;
            case 2: // Dots
                Eye(l + new Vector2(0.2f, 0.3f), 0.85f, 1.2f);
                Eye(r + new Vector2(0, 0.3f), 0.85f, 1.2f);
                break;
            case 3: // Sleepy
                foreach (var e in new[] { l + new Vector2(0, 0.4f), r + new Vector2(0, 0.4f) })
                {
                    Eye(e, 1.7f, 2f);
                    if (!p.Blink) Lid(e + look + new Vector2(-2.1f, -0.2f), e + look + new Vector2(2.1f, -0.2f), 2.4f);
                }
                break;
            case 4: // Angry: hollows tilted in under a carved scowl.
                Eye(l, 1.5f, 2.2f, -0.3f);
                Eye(r, 1.5f, 2.2f, 0.3f);
                if (!p.Blink)
                {
                    Lid(l + look + new Vector2(-1.9f, -2.4f), l + look + new Vector2(1.7f, -0.9f), 1.6f);
                    Lid(r + look + new Vector2(-1.7f, -0.9f), r + look + new Vector2(1.9f, -2.4f), 1.6f);
                }
                break;
            case 5: // Visor: one dark slit with two pale lights drifting along it.
            {
                c.Poly(Shapes.RoundRect(new Rect2(-4.8f, -5.6f, 11f, p.Blink ? 0.8f : 2.6f), 1.2f), hole);
                if (p.Blink) break;
                float u = Mathf.PingPong(p.Time * 0.9f, 1f) * 1.6f;
                foreach (float x in new[] { -2.2f, 2.8f })
                {
                    var at = new Vector2(x + u + look.X, -4.3f);
                    c.Glow(at, 2.2f, new Color("#bfe3ff") with { A = 0.5f });
                    c.Circle(at, 0.55f, p.Hurt ? Colors.White : Shapes.Hdr(new Color("#d8efff"), 1.4f));
                }
                break;
            }
            case 6: // Cyclops
                Eye(new Vector2(0.6f, -4.5f), 2.3f, 3.1f);
                break;
            case 7: // Shades: a dark band across the mask.
            {
                var band = Shapes.RoundRect(new Rect2(-5.4f, -6.0f, 11.6f, 3.2f), 1f);
                c.Poly(band, hole);
                c.Gradient(band, c.H(Hollow.Lerp(new Color("#2f3a5c"), 0.45f)), hole);
                c.Poly(new[] { new Vector2(-4.6f, -5.7f), new Vector2(-2.2f, -5.7f), new Vector2(-3.4f, -3.4f), new Vector2(-5.4f, -3.4f) }, new Color(1, 1, 1, 0.1f));
                c.Line(new Vector2(-4.4f, -5.2f), new Vector2(-1.8f, -5.2f), new Color(1, 1, 1, 0.3f), 0.5f);
                c.Line(new Vector2(0.9f, -4.4f), new Vector2(2.1f, -5.5f), new Color(1, 1, 1, 0.18f), 0.4f);
                c.Line(new Vector2(-7.3f, -5.6f), new Vector2(-5.3f, -5.2f), Ink, 0.7f);
                break;
            }
            case 8: // Happy: crescents curving up.
                foreach (var e in new[] { l + new Vector2(0, 0.4f), r + new Vector2(0, 0.4f) })
                {
                    var pts = new List<Vector2>();
                    for (int i = 0; i <= 10; i++) pts.Add(e + look + Vector2.FromAngle(Mathf.Pi + 0.25f + i * (Mathf.Pi - 0.5f) / 10) * 1.9f);
                    for (int i = 10; i >= 0; i--) pts.Add(e + look + new Vector2(0, 0.5f) + Vector2.FromAngle(Mathf.Pi + 0.45f + i * (Mathf.Pi - 0.9f) / 10) * 1.3f);
                    c.Poly(pts.ToArray(), hole);
                }
                break;
        }
    }

    // ---------- mouth ----------
    // Carved hollows in the mask, like the eyes: filled shapes thick enough to read at play size, never hairlines.
    // Drawn 1.4 px higher than the old cartoon mouths so they sit above the collar. Mouth space: x 1.4 is the middle.

    /// <summary>
    /// A curved slit, thickest in the middle and rounded off at the ends: <paramref name="sag"/> &gt; 0 bows down (a
    /// smile), &lt; 0 bows up (a frown).
    /// </summary>
    private static Vector2[] Crescent(float cx, float cy, float halfWidth, float sag, float thick)
    {
        const int n = 12;
        var pts = new Vector2[(n + 1) * 2];
        for (int i = 0; i <= n; i++)
        {
            float u = -1 + 2f * i / n, y = cy + sag * (1 - u * u);
            float half = thick * 0.5f * Mathf.Sqrt(Mathf.Max(0, 1 - u * u)) + 0.14f;
            pts[i] = new Vector2(cx + u * halfWidth, y - half);
            pts[pts.Length - 1 - i] = new Vector2(cx + u * halfWidth, y + half);
        }
        return pts;
    }

    private static readonly Vector2[] TongueShape = Smooth(new[]
    {
        new Vector2(0.6f, 0.85f), new Vector2(2.2f, 0.85f), new Vector2(2.5f, 1.95f), new Vector2(1.95f, 2.85f),
        new Vector2(0.85f, 2.85f), new Vector2(0.3f, 1.95f),
    }, 2);

    private static readonly Vector2[] SmileShape = Crescent(1.4f, 0.2f, 2.3f, 0.9f, 0.85f);
    private static readonly Vector2[] FrownShape = Crescent(1.4f, 1.6f, 2.1f, -0.85f, 0.8f);
    private static readonly Vector2[] FangSmileShape = Crescent(1.4f, 0.3f, 2.4f, 0.7f, 0.75f);
    private static readonly Vector2[] TongueSmileShape = Crescent(1.4f, 0.1f, 2.2f, 0.9f, 1.15f);
    private static readonly Vector2[] GrinShape = BuildGrin();

    /// <summary>A wide D: flat along the top, round underneath.</summary>
    private static Vector2[] BuildGrin()
    {
        var pts = new List<Vector2>();
        for (int i = 0; i <= 14; i++)
        {
            float a = i * Mathf.Pi / 14;
            pts.Add(new Vector2(1.4f + Mathf.Cos(a) * 2.6f, -0.2f + Mathf.Sin(a) * 2f));
        }
        return pts.ToArray();
    }

    private static readonly Vector2[] MustacheShape =
    {
        new(1.4f, -1f), new(3.4f, -1.3f), new(5.3f, -0.4f), new(6.1f, 0.9f), new(4.6f, 0.2f), new(2.6f, 0.5f), new(1.4f, 0.1f),
        new(0.2f, 0.5f), new(-1.8f, 0.2f), new(-3.3f, 0.9f), new(-2.5f, -0.4f), new(-0.6f, -1.3f),
    };

    private static void DrawMouth(Ctx c, int style, Vector2 bodyOrigin, Vector2 scale)
    {
        c.SetTransform(bodyOrigin + new Vector2(0, -1.4f * scale.Y), 0, scale);
        var hole = c.H(Hollow);
        var tongue = c.H(new Color("#b0606f"));
        switch (style)
        {
            case 0: // Smile
                c.Poly(SmileShape, hole);
                break;
            case 1: // Flat
                c.Poly(Shapes.Capsule(new Vector2(0.1f, 1f), new Vector2(2.9f, 1f), 0.45f), hole);
                break;
            case 2: // Open: a round "oh" with the tongue showing at the bottom.
                c.Poly(Shapes.Ellipse(new Vector2(1.4f, 0.9f), 1.3f, 1.5f, 18), hole);
                c.Poly(Shapes.Ellipse(new Vector2(1.4f, 1.75f), 0.85f, 0.5f, 14), tongue);
                c.Poly(Shapes.Ellipse(new Vector2(1.6f, 1.6f), 0.4f, 0.18f, 8), c.H(tongue.Lightened(0.3f)));
                c.Line(new Vector2(0.4f, -0.4f), new Vector2(0.9f, -0.5f), new Color(1, 1, 1, 0.3f), 0.3f); // lip highlight
                break;
            case 3: // Grin: a wide D with two white buck teeth hanging from the top edge, side by side.
            {
                c.Poly(GrinShape, hole);
                foreach (float x in new[] { 0.55f, 1.45f })
                {
                    var tooth = Shapes.RoundRect(new Rect2(x, -0.2f, 0.7f, 1.3f), 0.28f);
                    c.Poly(tooth, c.H(OffWhite));
                    c.Poly(new[] { new Vector2(x, -0.2f), new Vector2(x + 0.7f, -0.2f), new Vector2(x + 0.7f, 0.3f), new Vector2(x, 0.3f) }, c.H(OffWhite)); // flat top, flush with the mouth
                    c.Gradient(Shapes.RoundRect(new Rect2(x + 0.05f, -0.2f, 0.6f, 1.15f), 0.24f), c.H(White.Lightened(0.25f)), c.H(White));
                    c.Gradient(new[] { new Vector2(x + 0.05f, -0.2f), new Vector2(x + 0.65f, -0.2f), new Vector2(x + 0.65f, 0.3f), new Vector2(x + 0.05f, 0.3f) }, c.H(White.Lightened(0.25f)), c.H(White.Lightened(0.15f)));
                }
                break;
            }
            case 4: // Frown
                c.Poly(FrownShape, hole);
                break;
            case 5: // Fangs: a smile with two long bone fangs hanging from it.
                c.Poly(FangSmileShape, hole);
                foreach (float fx in new[] { 0.3f, 2.5f })
                {
                    float top = 0.3f + 0.7f * (1 - Mathf.Pow((fx - 1.4f) / 2.4f, 2)) - 0.2f;
                {
                    var fang = new[] { new Vector2(fx - 0.5f, top), new Vector2(fx + 0.5f, top), new Vector2(fx + 0.05f, top + 1.5f) };
                    c.Fill(fang, c.H(c.MaskShade), Ink, 0.3f);
                    c.Gradient(fang, c.H(Bone.Lightened(0.2f)), c.H(c.Mask));
                    c.Line(new Vector2(fx + 0.15f, top + 0.1f), new Vector2(fx + 0.1f, top + 0.8f), new Color(1, 1, 1, 0.6f), 0.22f);
                }
                }
                break;
            case 6: // Tongue: an open smile with a rounded, grooved tongue lolling out over the lip, in the middle.
            {
                c.Poly(TongueSmileShape, hole);
                c.Poly(Shapes.Ellipse(new Vector2(1.4f, 1.05f), 1.1f, 0.35f, 10), Ink with { A = 0.45f }); // shadow it casts on the mask
                c.Fill(TongueShape, c.H(new Color("#8e4455")), Ink, 0.35f);
                c.Gradient(Inset(TongueShape, 0.15f), c.H(Lit(new Color("#c9707f"), 0.2f)), c.H(new Color("#a85565")));
                c.Line(new Vector2(1.4f, 1.15f), new Vector2(1.4f, 2.4f), c.H(new Color("#7d3a48")), 0.28f); // centre groove
                c.Line(new Vector2(0.95f, 1.5f), new Vector2(0.95f, 1.9f), c.H(Lit(new Color("#c9707f"), 0.4f)) with { A = 0.6f }, 0.25f);
                c.Poly(Shapes.Ellipse(new Vector2(1.85f, 1.55f), 0.22f, 0.4f, 8), c.P.Hurt ? Colors.White : new Color(1, 1, 1, 0.45f));
                break;
            }
            case 7: // Mustache (hair dye) over a small smile.
                c.Poly(Crescent(1.4f, 1.6f, 1.2f, 0.35f, 0.55f), hole);
                var stache = Shapes.Offset(MustacheShape, new Vector2(0, 0.4f));
                c.Fill(stache, c.H(Shade(c.Hair, 0.25f)), c.Hair.Darkened(0.55f), 0.45f);
                c.Gradient(stache, c.H(Lit(c.Hair, 0.3f)), c.H(Shade(c.Hair, 0.25f)));
                c.Line(new Vector2(3.4f, -0.5f), new Vector2(5.2f, 0.2f), c.Hair.Darkened(0.5f) with { A = 0.5f }, 0.3f);
                c.Line(new Vector2(-0.6f, -0.5f), new Vector2(-2.2f, 0.2f), c.Hair.Darkened(0.5f) with { A = 0.5f }, 0.3f);
                c.Line(new Vector2(0.4f, -0.9f), new Vector2(3f, -0.9f), c.H(Lit(c.Hair, 0.5f)) with { A = 0.7f }, 0.35f);
                break;
            case 8: // Cat: a little nose over a "w".
                c.Poly(new[] { new Vector2(0.9f, -0.5f), new Vector2(1.9f, -0.5f), new Vector2(1.4f, 0.15f) }, hole);
                c.Poly(Crescent(0.45f, 0.5f, 0.95f, 0.5f, 0.55f), hole);
                c.Poly(Crescent(2.35f, 0.5f, 0.95f, 0.5f, 0.55f), hole);
                break;
        }
        c.SetTransform(bodyOrigin, 0, scale);
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

    // Under a hat that covers the crown (see Covers): spikes only stick out at the sides, below the brim.
    private static readonly Vector2[] SpikyTucked = Cap(
        new(-7.8f, -2.5f), new(-8.3f, -4.2f), new(-10.6f, -4.6f), new(-8.5f, -6.2f), new(-10.4f, -8.2f), new(-7.6f, -8.3f),
        new(-6.8f, -10.4f), new(0f, -11.3f), new(6.8f, -10.4f), new(8.2f, -9.6f), new(10.6f, -9.4f), new(8.4f, -7.8f), new(9.6f, -6.4f),
        new(7.6f, -6.6f));

    // Under a covering hat an afro puffs out round the sides instead of standing up through it.
    private static readonly (Vector2 C, float R)[] AfroTucked =
    {
        (new(0f, -8.4f), 2.8f), (new(-4.4f, -7.8f), 3f), (new(4.4f, -8f), 2.8f), (new(-7.4f, -6.4f), 3.4f), (new(-8f, -3.4f), 2.6f),
        (new(7.6f, -7.6f), 2.8f),
    };

    private static void DrawHairBack(Ctx c, int style)
    {
        if (style != 6) return;
        // Ponytail swings a little behind the head.
        float sw = Mathf.Sin(c.P.Time * 3f) * 0.6f;
        var tail = Smooth(new[]
        {
            new Vector2(-6f, -8.4f), new Vector2(-9.8f, -10.2f), new Vector2(-12.8f, -8f + sw * 0.3f), new Vector2(-13.6f + sw, -4.2f),
            new Vector2(-12.4f + sw, -0.2f), new Vector2(-10.8f + sw * 0.6f, -3.6f), new Vector2(-9.8f, -6.2f), new Vector2(-7.2f, -5.6f),
        }, 1);
        c.Fill(tail, c.H(Shade(c.Hair, 0.3f)), c.Hair.Darkened(0.5f), 0.8f);
        c.Gradient(tail, c.H(Lit(c.Hair, 0.2f)), c.H(Shade(c.Hair, 0.25f)));
        var strand = c.Hair.Darkened(0.5f) with { A = 0.5f };
        c.Line(new Vector2(-9.6f, -8.6f), new Vector2(-12f + sw, -2.2f), strand, 0.4f);
        c.Line(new Vector2(-8.4f, -7f), new Vector2(-10.6f + sw * 0.6f, -3.4f), strand, 0.35f);
        c.Line(new Vector2(-10.8f, -8.8f), new Vector2(-12.6f + sw, -4.6f), c.H(Lit(c.Hair, 0.5f)) with { A = 0.7f }, 0.5f);
    }

    /// <summary>A hair mass: shadow-tinted base with the dark rim, then a light-to-shadow gradient over it.</summary>
    private static void HairMass(Ctx c, Vector2[] pts, float top = 0.3f, float bottom = 0.2f)
    {
        c.Fill(pts, c.H(Shade(c.Hair, 0.3f)), c.Hair.Darkened(0.5f), 0.8f);
        c.Gradient(pts, c.H(Lit(c.Hair, top)), c.H(Shade(c.Hair, bottom)));
    }

    private static void DrawHair(Ctx c, int style, int hat)
    {
        bool underHat = Covers(hat);
        var main = c.H(c.Hair);
        var line = c.Hair.Darkened(0.5f);
        var shine = c.H(Lit(c.Hair, 0.55f)) with { A = 0.75f };
        var strand = line with { A = 0.45f };
        switch (style)
        {
            case 1:
                HairMass(c, ShortHair);
                c.Poly(Shapes.Ellipse(new Vector2(-2f, -9.7f), 3f, 0.75f, 12), shine);
                c.Line(new Vector2(0.4f, -11f), new Vector2(1.6f, -8.2f), strand, 0.4f);
                c.Line(new Vector2(-4.4f, -10.2f), new Vector2(-3.4f, -7.6f), strand, 0.4f);
                c.Line(new Vector2(4.4f, -10.1f), new Vector2(4.2f, -7.4f), strand, 0.4f);
                break;
            case 2 when underHat:
                HairMass(c, SpikyTucked);
                c.Line(new Vector2(-9.2f, -7.6f), new Vector2(-7.4f, -7.6f), strand, 0.4f);
                c.Line(new Vector2(9.4f, -8.6f), new Vector2(7.8f, -8.4f), strand, 0.4f);
                break;
            case 2:
                HairMass(c, SpikyHair);
                c.Line(new Vector2(-1f, -14.8f), new Vector2(-1.4f, -11f), shine, 0.6f);
                c.Line(new Vector2(3.8f, -13.8f), new Vector2(3.2f, -11f), shine, 0.5f);
                c.Line(new Vector2(-5.4f, -12.2f), new Vector2(-5.2f, -10.4f), shine, 0.45f);
                c.Line(new Vector2(-3.2f, -11.2f), new Vector2(-2.6f, -9.2f), strand, 0.4f);
                c.Line(new Vector2(1.6f, -11.4f), new Vector2(1.8f, -9.2f), strand, 0.4f);
                break;
            case 3 when HidesMohawk(hat): // shaved sides: nothing shows under the hat
                break;
            case 3:
                HairMass(c, Mohawk);
                c.Line(new Vector2(-1.8f, -14.8f), new Vector2(-3.2f, -11f), shine, 0.6f);
                c.Line(new Vector2(1.6f, -14.6f), new Vector2(1.9f, -10.8f), strand, 0.4f);
                c.Line(new Vector2(-0.2f, -15.6f), new Vector2(-0.3f, -11f), strand, 0.35f);
                break;
            case 4:
                HairMass(c, LongHair, 0.3f, 0.3f);
                c.Poly(Shapes.Ellipse(new Vector2(-2f, -9.9f), 3f, 0.75f, 12), shine);
                c.Line(new Vector2(-7.4f, -4f), new Vector2(-7.2f, 1.4f), shine, 0.5f);
                c.Line(new Vector2(-6.4f, -5.4f), new Vector2(-6.4f, 1.8f), strand, 0.4f);
                c.Line(new Vector2(0.4f, -11f), new Vector2(1.6f, -8.2f), strand, 0.4f);
                c.Line(new Vector2(-4.4f, -10.2f), new Vector2(-3.4f, -7.6f), strand, 0.4f);
                break;
            case 5:
                HairMass(c, BowlHair);
                c.Poly(Shapes.Ellipse(new Vector2(-1.5f, -10.1f), 3.6f, 0.8f, 12), shine);
                for (float x = -3.6f; x < 8f; x += 2.4f)
                    c.Line(new Vector2(x, -7.8f), new Vector2(x + 0.2f, -6.3f), strand, 0.4f);
                c.Line(new Vector2(-5.6f, -6.1f), new Vector2(8.4f, -6.1f), line with { A = 0.7f }, 0.5f);
                break;
            case 6:
                HairMass(c, ShortHair);
                c.Poly(Shapes.Ellipse(new Vector2(-2f, -9.7f), 3f, 0.75f, 12), shine);
                c.Line(new Vector2(0.4f, -11f), new Vector2(1.6f, -8.2f), strand, 0.4f);
                c.Line(new Vector2(-4.6f, -10.2f), new Vector2(-6.2f, -8.4f), strand, 0.4f);
                c.Fill(Shapes.Ellipse(new Vector2(-7.4f, -7.4f), 1.15f, 1.45f, 12), c.H(Shade(Red, 0.2f)), DarkRed, 0.45f); // the tie
                c.Poly(Shapes.Ellipse(new Vector2(-7.6f, -7.9f), 0.5f, 0.6f, 8), c.H(Lit(Red, 0.4f)));
                break;
            case 7:
            {
                var puffs = underHat ? AfroTucked : AfroPuffs;
                foreach (var (pc, r) in puffs) c.Circle(pc, r + 0.8f, line);
                foreach (var (pc, r) in puffs) c.Circle(pc, r, c.H(Shade(c.Hair, 0.3f)));
                // Lit copies, nudged up and forward, so each puff keeps shadow on its lower back edge.
                foreach (var (pc, r) in puffs) // tangent to each puff's lit edge
                {
                    var n = new Vector2(0.4f, -0.55f) * (r / 4f);
                    c.Circle(pc + n, r - n.Length(), main);
                }
                foreach (var (pc, r) in puffs) c.Circle(pc + new Vector2(0.7f, -1f) * (r / 4f), r * 0.45f, c.H(Lit(c.Hair, 0.3f)));
                foreach (var (pc, r) in puffs) c.Arc(pc, r * 0.7f, Mathf.Pi * 1.1f, Mathf.Pi * 1.45f, 6, shine with { A = 0.5f }, 0.5f);
                if (!underHat)
                    foreach (var (pc, r) in puffs) // curl hints on the shadow side
                        c.Arc(pc, r * 0.55f, Mathf.Pi * 0.15f, Mathf.Pi * 0.6f, 6, strand with { A = 0.3f }, 0.35f);
                break;
            }
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

    // ---------- shading helpers (see docs/AVATAR_ART.md) ----------

    private static readonly Color ShadeTint = new("#2a2340"), LightTint = new("#fff1d0");
    private static Color Shade(Color c, float k = 0.35f) => c.Lerp(ShadeTint, k);
    private static Color Lit(Color c, float k = 0.25f) => c.Lerp(LightTint, k);

    /// <summary>Chaikin corner cutting: each pass rounds every corner (point count doubles). Call once, into a static array.</summary>
    private static Vector2[] Smooth(Vector2[] pts, int passes = 2)
    {
        for (int p = 0; p < passes; p++)
        {
            var r = new Vector2[pts.Length * 2];
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
                r[2 * i] = a.Lerp(b, 0.25f);
                r[2 * i + 1] = a.Lerp(b, 0.75f);
            }
            pts = r;
        }
        return pts;
    }

    /// <summary>
    /// The shape pulled toward its upper-front vertex: points near that vertex stay put, the rest slide toward it, so
    /// the copy sits flush against the lit edges and leaves a shadow sliver along the back and bottom.
    /// </summary>
    private static Vector2[] LitCopy(Vector2[] pts, float pull)
    {
        var light = pts[0];
        float best = float.MinValue;
        foreach (var q in pts)
        {
            float s = q.X * 0.6f - q.Y;
            if (s > best) { best = s; light = q; }
        }
        var r = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++) r[i] = pts[i].Lerp(light, pull);
        return r;
    }

    /// <summary>The outline moved <paramref name="w"/> units inward along each vertex's normal (for borders that follow a curve).</summary>
    private static Vector2[] Inset(Vector2[] pts, float w)
    {
        float area = 0;
        for (int i = 0; i < pts.Length; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Length]; area += a.X * b.Y - b.X * a.Y; }
        float sign = area > 0 ? 1 : -1;
        var r = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            var t = (pts[(i + 1) % pts.Length] - pts[(i + pts.Length - 1) % pts.Length]).Normalized();
            var inward = new Vector2(-t.Y, t.X) * sign;
            r[i] = pts[i] + inward * w;
        }
        return r;
    }

    /// <summary>
    /// A shape lit from the upper front: the whole shape in the shadow color with an outline, then a copy pulled toward
    /// the lit corner in a light-to-mid gradient, so the far (back and lower) edge keeps the shadow and the lit edges
    /// line up with the outline. Works on roughly convex shapes.
    /// </summary>
    private static void Form(Ctx c, Vector2[] pts, Color col, float ow, float pull = 0.14f)
    {
        c.Fill(pts, c.H(Shade(col)), col.Darkened(0.5f), ow);
        c.Gradient(LitCopy(pts, pull), c.H(Lit(col)), c.H(col));
    }

    /// <summary>A soft white highlight on the top front of a part.</summary>
    private static void Spec(Ctx c, Vector2 at, float rx, float ry, float a = 0.35f) =>
        c.Poly(Shapes.Ellipse(at, rx, ry, 12), c.P.Hurt ? Colors.White : new Color(1, 1, 1, a));

    private static void DrawHat(Ctx c, int style)
    {
        float t = c.P.Time;
        var dye = c.H(c.Hat);
        var dyeLine = c.Hat.Darkened(0.5f);
        var dyeShade = c.H(Shade(c.Hat));
        var dyeLight = c.H(Lit(c.Hat));
        var seam = dyeLine with { A = 0.5f };
        switch (style)
        {
            case 1: // Cap
            {
                var dome = Dome(new Vector2(0, -9.4f), 7.7f, 4.8f);
                Form(c, dome, c.Hat, 0.7f, 0.1f);
                c.Line(new Vector2(0.6f, -14.1f), new Vector2(1.8f, -9.6f), seam, 0.4f);
                c.Line(new Vector2(-3.4f, -13.1f), new Vector2(-4.2f, -9.8f), seam, 0.4f);
                c.Line(new Vector2(4.4f, -13.0f), new Vector2(5.8f, -9.8f), seam, 0.4f);
                c.Circle(new Vector2(0.4f, -14.2f), 0.75f, dyeLine);
                c.Circle(new Vector2(0.3f, -14.35f), 0.45f, dyeLight);
                Spec(c, new Vector2(-1.5f, -12.9f), 2.2f, 0.65f);
                // The beak sticks out of the front of the crown, its bottom edge on the crown's bottom edge (y -9.4).
                var beak = Smooth(new[]
                {
                    new Vector2(2f, -10.6f), new Vector2(7f, -11f), new Vector2(11f, -10.4f), new Vector2(12.6f, -9.4f),
                    new Vector2(2f, -9.4f),
                }, 1);
                c.Fill(beak, c.H(Shade(c.Hat, 0.45f)), dyeLine, 0.5f);
                c.Gradient(beak, c.H(c.Hat.Darkened(0.05f)), c.H(Shade(c.Hat, 0.5f)));
                c.Line(new Vector2(3f, -10.3f), new Vector2(10f, -10.2f), dyeLight with { A = 0.45f }, 0.4f);
                break;
            }
            case 2: // Top hat
            {
                var rim = HatGrey.Lightened(0.25f);
                var crown = Shapes.RoundRect(new Rect2(-5f, -21.4f, 10f, 11.6f), 1.2f);
                c.Fill(crown, c.H(HatBlack), rim, 0.6f);
                c.Gradient(crown, c.H(HatGrey.Lerp(HatBlack, 0.35f)), c.H(HatBlack));
                c.Poly(Shapes.RoundRect(new Rect2(1.6f, -20.6f, 3f, 10f), 0.8f), c.H(HatBlack) with { A = 0.6f });
                var band = Shapes.RoundRect(new Rect2(-5f, -13.8f, 10f, 2.6f), 0.4f);
                c.Poly(band, dye);
                c.Gradient(band, dyeLight, dyeShade);
                c.Line(new Vector2(-5f, -13.8f), new Vector2(5f, -13.8f), dyeLine with { A = 0.6f }, 0.35f);
                c.Fill(Shapes.RoundRect(new Rect2(0.8f, -13.9f, 2.4f, 2.8f), 0.4f), c.H(Gold), DarkGold, 0.35f);
                c.Poly(Shapes.RoundRect(new Rect2(1.5f, -13.3f, 1.2f, 1.6f), 0.2f), c.H(HatBlack));
                c.Line(new Vector2(-3.6f, -20f), new Vector2(-3.6f, -14.4f), c.H(HatGrey.Lightened(0.15f)) with { A = 0.7f }, 0.8f);
                var brim = Shapes.Capsule(new Vector2(-8.4f, -10.3f), new Vector2(8.4f, -10.3f), 1.2f);
                c.Fill(brim, c.H(HatBlack), rim, 0.6f);
                c.Line(new Vector2(-7.6f, -11f), new Vector2(7.2f, -11f), c.H(HatGrey.Lightened(0.1f)) with { A = 0.6f }, 0.4f);
                break;
            }
            case 3: // Beanie
            {
                // The crown's widest point sits inside the cuff, so the cuff overhangs it by a hair and the two join cleanly.
                var dome = Dome(new Vector2(0, -9f), 7.9f, 6.6f);
                Form(c, dome, c.Hat, 0.7f, 0.14f);
                var cuff = Shapes.RoundRect(new Rect2(-8.1f, -10.6f, 16.2f, 2.6f), 1.1f);
                c.Fill(cuff, c.H(Shade(c.Hat, 0.15f)), dyeLine, 0.6f);
                c.Gradient(cuff, dyeLight, c.H(Shade(c.Hat, 0.3f)));
                for (float x = -6.8f; x < 7.5f; x += 1.7f)
                    c.Line(new Vector2(x, -10.1f), new Vector2(x + 0.1f, -8.5f), seam, 0.4f);
                c.Line(new Vector2(-6f, -10.2f), new Vector2(5f, -10.2f), dyeLight with { A = 0.6f }, 0.4f);
                for (float x = -4.4f; x < 5; x += 2.2f)
                    c.Line(new Vector2(x, -15.4f + Mathf.Abs(x) * 0.25f), new Vector2(x * 0.9f, -11f), seam with { A = 0.3f }, 0.35f);
                c.Circle(new Vector2(0, -15.9f), 2.3f, dyeLine);
                c.Circle(new Vector2(0, -15.9f), 1.9f, c.H(Shade(c.Hat.Lightened(0.4f), 0.2f)));
                c.Circle(new Vector2(0.4f, -16.3f), 1.9f - 0.57f, c.H(Lit(c.Hat, 0.5f))); // tangent to the pom's lit edge
                Spec(c, new Vector2(-0.1f, -16.8f), 0.8f, 0.45f, 0.5f);
                break;
            }
            case 4: // Crown
            {
                var crown = new[]
                {
                    new Vector2(-6f, -9.8f), new Vector2(-6.8f, -15.8f), new Vector2(-3.3f, -12.7f), new Vector2(0f, -17.2f),
                    new Vector2(3.3f, -12.7f), new Vector2(6.8f, -15.8f), new Vector2(6f, -9.8f),
                };
                c.Fill(crown, c.H(DarkGold), DarkGold.Darkened(0.55f), 0.6f);
                c.Gradient(LitCopy(crown, 0.08f), c.H(PaleYellow), c.H(Gold));
                var band = Shapes.RoundRect(new Rect2(-6f, -11.6f, 12f, 1.8f), 0.4f);
                c.Poly(band, c.H(Gold));
                c.Gradient(band, c.H(Gold.Lightened(0.15f)), c.H(DarkGold));
                foreach (var tip in new[] { new Vector2(-6.8f, -15.8f), new Vector2(0, -17.2f), new Vector2(6.8f, -15.8f) })
                {
                    c.Circle(tip, 0.95f, c.H(DarkGold));
                    c.Circle(tip, 0.75f, c.H(PaleYellow));
                }
                foreach (float gx in new[] { -3.6f, 3.6f }) c.Circle(new Vector2(gx, -10.7f), 0.38f, c.H(Red.Lightened(0.15f)));
                c.Fill(Shapes.Ellipse(new Vector2(0, -12.6f), 1.3f, 1.5f, 12), dyeShade, dyeLine, 0.35f);
                c.Poly(Shapes.Ellipse(new Vector2(0.1f, -12.9f), 0.85f, 1f, 10), dye);
                c.Circle(new Vector2(-0.4f, -13.3f), 0.35f, c.P.Hurt ? Colors.White : new Color(1, 1, 1, 0.85f));
                break;
            }
            case 5: // Party
            {
                var tip = new Vector2(1f, -20.4f);
                Vector2 bl = new(-5f, -10.2f), br = new(5f, -10.2f);
                var cone = new[] { bl, tip, br };
                c.Fill(cone, dyeShade, dyeLine, 0.6f);
                c.Gradient(new[] { bl + new Vector2(0.9f, -0.2f), tip + new Vector2(-0.1f, 0.9f), br.Lerp(tip, 0.55f) + new Vector2(-0.5f, 0), br.Lerp(bl, 0.5f) + new Vector2(0, -0.2f) }, dyeLight, dye);
                foreach (var (a0, a1) in new[] { (0.2f, 0.34f), (0.5f, 0.64f) })
                    c.Poly(new[] { bl.Lerp(tip, a0), bl.Lerp(tip, a1), br.Lerp(tip, a1), br.Lerp(tip, a0) }, c.H(White.Lerp(c.Hat, 0.12f)));
                c.Line(bl, br, dyeLine with { A = 0.7f }, 0.5f);
                c.Circle(tip, 1.75f, c.H(Gold.Darkened(0.2f)));
                c.Circle(tip, 1.5f, c.H(PaleYellow));
                c.Circle(tip + new Vector2(-0.45f, -0.45f), 0.5f, Colors.White);
                break;
            }
            case 6: // Headband
            {
                float w1 = Mathf.Sin(t * 6f) * 0.9f, w2 = Mathf.Sin(t * 6f + 1.3f) * 0.9f;
                var tailA = Shapes.Capsule(new Vector2(-7.4f, -8.2f), new Vector2(-12.4f, -6.6f + w1), 0.85f);
                var tailB = Shapes.Capsule(new Vector2(-7.4f, -7.8f), new Vector2(-11.6f, -4.2f + w2), 0.85f);
                c.Fill(tailB, c.H(Shade(c.Hat, 0.45f)), dyeLine, 0.4f);
                c.Fill(tailA, c.H(Shade(c.Hat, 0.2f)), dyeLine, 0.4f);
                var band = Shapes.RoundRect(new Rect2(-7.7f, -9.4f, 15.4f, 2.5f), 1f);
                c.Fill(band, dyeShade, dyeLine, 0.55f);
                c.Gradient(band, dyeLight, dye);
                c.Line(new Vector2(-6.5f, -8.95f), new Vector2(6.5f, -8.95f), dyeLight with { A = 0.6f }, 0.4f);
                c.Line(new Vector2(-6f, -7.6f), new Vector2(6f, -7.6f), dyeLine with { A = 0.4f }, 0.35f);
                c.Fill(Shapes.Ellipse(new Vector2(-7.6f, -8.1f), 1.3f, 1.5f, 12), dyeShade, dyeLine, 0.4f); // the knot
                c.Poly(Shapes.Ellipse(new Vector2(-7.8f, -8.5f), 0.55f, 0.6f, 8), dyeLight);
                break;
            }
            case 7: // Viking
            {
                var hornL = Smooth(new[]
                {
                    new Vector2(-6.2f, -10.8f), new Vector2(-9.8f, -12f), new Vector2(-11.8f, -15.4f), new Vector2(-11.9f, -19.2f),
                    new Vector2(-10.0f, -16.2f), new Vector2(-7.7f, -14.2f), new Vector2(-5.4f, -13.2f),
                }, 1);
                var hornR = new Vector2[hornL.Length];
                for (int i = 0; i < hornL.Length; i++) hornR[i] = new Vector2(-hornL[i].X, hornL[i].Y);
                foreach (var horn in new[] { hornL, hornR })
                {
                    c.Fill(horn, c.H(Shade(Bone, 0.25f)), Bone.Darkened(0.55f), 0.6f);
                    c.Gradient(horn, c.H(Bone.Lightened(0.15f)), c.H(Shade(Bone, 0.25f)));
                }
                foreach (float sx in new[] { -1f, 1f })
                {
                    c.Line(new Vector2(sx * 9.4f, -12.6f), new Vector2(sx * 8.4f, -13.6f), Bone.Darkened(0.45f) with { A = 0.6f }, 0.4f);
                    c.Line(new Vector2(sx * 10.8f, -15f), new Vector2(sx * 9.4f, -15.7f), Bone.Darkened(0.45f) with { A = 0.6f }, 0.4f);
                }
                var dome = Dome(new Vector2(0, -9.6f), 7.8f, 5.6f);
                Form(c, dome, c.Hat, 0.7f);
                var bandPts = Shapes.RoundRect(new Rect2(-8.2f, -11.2f, 16.4f, 2.3f), 0.9f);
                c.Fill(bandPts, c.H(DarkMetal), DarkMetal.Darkened(0.4f), 0.5f);
                c.Gradient(bandPts, c.H(Metal.Lightened(0.2f)), c.H(DarkMetal));
                for (float x = -6f; x <= 6f; x += 3f)
                {
                    c.Circle(new Vector2(x, -10.05f), 0.45f, c.H(DarkMetal.Darkened(0.3f)));
                    c.Circle(new Vector2(x - 0.1f, -10.2f), 0.25f, c.H(Metal.Lightened(0.3f)));
                }
                Spec(c, new Vector2(-1.8f, -13.7f), 2.4f, 0.55f);
                break;
            }
            case 8: // Halo
            {
                float lift = Mathf.Sin(t * 2.6f) * 0.6f;
                var ring = Shapes.Ellipse(new Vector2(0, -14.8f + lift), 5.6f, 1.7f, 28);
                c.Closed(ring, c.P.Hurt ? Colors.White : Shapes.Hdr(DarkGold, 1.3f), 1.5f);
                c.Closed(ring, c.P.Hurt ? Colors.White : Shapes.Hdr(Gold, 1.7f), 1.0f);
                c.Closed(ring, c.H(PaleYellow) with { A = 0.85f }, 0.4f);
                break;
            }
            case 9: // Propeller
            {
                var dome = Dome(new Vector2(0, -9.6f), 7f, 4.4f);
                Form(c, dome, c.Hat, 0.7f);
                foreach (float sx in new[] { -2.9f, 0f, 2.9f }) // panel seams
                    c.Line(new Vector2(sx * 0.4f, -14f), new Vector2(sx * 1.9f, -9.8f), seam, 0.4f);
                c.Line(new Vector2(-6.8f, -10.4f), new Vector2(6.8f, -10.4f), dyeLine with { A = 0.45f }, 0.4f);
                Spec(c, new Vector2(-2.2f, -12.7f), 2f, 0.55f);
                c.Line(new Vector2(0, -13.8f), new Vector2(0, -15.4f), Ink, 0.8f);
                float spin = Mathf.Cos(t * 22f);
                float bw = Mathf.Max(0.4f, 2.8f * Mathf.Abs(spin));
                c.Fill(Shapes.Ellipse(new Vector2(-2.8f * spin, -15.6f), bw, 0.75f, 12), c.H(Red.Lightened(0.08f)), DarkRed, 0.4f);
                c.Fill(Shapes.Ellipse(new Vector2(2.8f * spin, -15.6f), bw, 0.75f, 12), c.H(Blue.Lightened(0.1f)), Blue.Darkened(0.5f), 0.4f);
                c.Circle(new Vector2(0, -15.6f), 0.7f, c.H(DarkGold));
                c.Circle(new Vector2(-0.1f, -15.7f), 0.5f, c.H(Gold.Lightened(0.2f)));
                break;
            }
            case 10: // Chef
            {
                var line = OffWhite.Darkened(0.5f);
                var puffs = new[] { (new Vector2(-3.8f, -16.4f), 3.6f), (new Vector2(3.8f, -16.4f), 3.6f), (new Vector2(0, -18f), 4f) };
                var band = Shapes.RoundRect(new Rect2(-5.5f, -16.6f, 11f, 6.4f), 0.6f);
                foreach (var (pc, r) in puffs) c.Circle(pc, r + 0.6f, line);
                c.Poly(Shapes.RoundRect(new Rect2(-6.1f, -16.6f, 12.2f, 6.4f), 0.8f), line);
                var shadeW = c.H(Shade(White, 0.3f));
                foreach (var (pc, r) in puffs) c.Circle(pc, r, shadeW);
                c.Poly(band, shadeW);
                // Lit copies, nudged up and forward, leave the shadow on the lower back edge.
                var litW = c.H(White);
                var nudge = new Vector2(0.5f, -0.55f);
                foreach (var (pc, r) in puffs) c.Circle(pc + nudge * 0.8f, r - nudge.Length() * 0.8f, litW); // tangent to each puff's lit edge
                c.Poly(Shapes.RoundRect(new Rect2(-4.8f, -16.6f, 10.3f, 5.6f), 0.6f), litW);
                // Pleats.
                c.Line(new Vector2(-2.4f, -15.4f), new Vector2(-2.1f, -12.6f), line with { A = 0.55f }, 0.4f);
                c.Line(new Vector2(0.4f, -15.9f), new Vector2(0.5f, -12.6f), line with { A = 0.45f }, 0.4f);
                c.Line(new Vector2(2.9f, -15.4f), new Vector2(2.7f, -12.6f), line with { A = 0.55f }, 0.4f);
                var cuff = Shapes.RoundRect(new Rect2(-5.6f, -12.4f, 11.2f, 1.8f), 0.4f);
                c.Poly(cuff, dye);
                c.Gradient(cuff, dyeLight, dyeShade);
                Spec(c, new Vector2(-2.6f, -19.6f), 1.5f, 0.6f, 0.6f);
                break;
            }
            case 11: // Wizard: a tall dyed cone flopping over at the tip, a wide brim and two gold stars.
            {
                float sway = Mathf.Sin(t * 2.2f) * 0.5f;
                var cone = new[]
                {
                    new Vector2(-5.4f, -10.6f), new Vector2(-2.6f, -17.5f), new Vector2(0.6f, -23.2f), new Vector2(3.8f + sway, -26.4f),
                    new Vector2(2.2f, -21.6f), new Vector2(3.4f, -16.6f), new Vector2(5.4f, -10.6f),
                };
                c.Fill(cone, dyeShade, dyeLine, 0.6f);
                c.Gradient(cone, dyeLight, c.H(Shade(c.Hat, 0.15f)));
                c.Poly(new[] { new Vector2(2.2f, -21.6f), new Vector2(3.4f, -16.6f), new Vector2(5.2f, -10.8f), new Vector2(2.4f, -10.8f), new Vector2(1.6f, -17f) }, dyeShade with { A = 0.5f });
                var brim = Shapes.Ellipse(new Vector2(0, -10.4f), 9.8f, 1.7f, 24);
                c.Fill(brim, c.H(Shade(c.Hat, 0.5f)), dyeLine, 0.6f);
                c.Gradient(brim, c.H(c.Hat.Darkened(0.05f)), c.H(Shade(c.Hat, 0.55f)));
                var band = Shapes.RoundRect(new Rect2(-5.5f, -12.6f, 11f, 1.8f), 0.4f);
                c.Poly(band, c.H(Shade(c.Hat, 0.5f)));
                c.Fill(Shapes.RoundRect(new Rect2(-1.1f, -12.8f, 2.2f, 2.2f), 0.4f), c.H(Gold), DarkGold, 0.35f);
                c.Poly(StarShape(new Vector2(-1.2f, -15.2f), 1.5f), c.H(PaleYellow));
                c.Poly(StarShape(new Vector2(1.8f, -19.6f), 1.0f), c.H(PaleYellow));
                c.Circle(new Vector2(-3f, -13.8f), 0.35f, c.H(PaleYellow) with { A = 0.8f });
                break;
            }
            case 12: // Laurel: two sprigs of gold leaves around the head, meeting over the brow with a dyed berry.
            {
                var center = new Vector2(0, -4.6f);
                const float r = 7.6f;
                foreach (var (from, to) in new[] { (192f, 262f), (278f, 348f) })
                    for (int i = 0; i < 5; i++)
                    {
                        float a = Mathf.DegToRad(Mathf.Lerp(from, to, i / 4f));
                        var p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                        var d = new Vector2(-Mathf.Sin(a), Mathf.Cos(a)) * 1.8f; // along the arc
                        var n = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.95f;  // outward
                        var leaf = new[] { p - d, p + n, p + d, p - n * 0.45f };
                        var lc = i % 2 == 0 ? Gold : PaleYellow;
                        c.Fill(leaf, c.H(Shade(lc, 0.25f)), DarkGold.Darkened(0.4f), 0.4f);
                        c.Gradient(leaf, c.H(Lit(lc, 0.3f)), c.H(lc));
                        c.Line(p - d * 0.7f, p + d * 0.8f, c.H(DarkGold) with { A = 0.5f }, 0.3f);
                    }
                c.Circle(center + new Vector2(0, -r), 1.15f, dyeLine);
                c.Circle(center + new Vector2(0, -r), 0.88f, dyeShade);
                c.Circle(center + new Vector2(0.1f, -r - 0.1f), 0.6f, dye);
                c.Circle(center + new Vector2(-0.2f, -r - 0.35f), 0.25f, c.P.Hurt ? Colors.White : new Color(1, 1, 1, 0.8f));
                break;
            }
            case 13: // Horns: two little red horns curling up out of the head.
            {
                var hornL = Smooth(new[]
                {
                    new Vector2(-6.4f, -10.2f), new Vector2(-7.8f, -12.8f), new Vector2(-7.5f, -15.4f), new Vector2(-6f, -17.2f),
                    new Vector2(-5.5f, -14.6f), new Vector2(-4.4f, -12.6f), new Vector2(-2.8f, -11.1f),
                }, 1);
                var hornR = new Vector2[hornL.Length];
                for (int i = 0; i < hornL.Length; i++) hornR[i] = new Vector2(-hornL[i].X, hornL[i].Y);
                foreach (var horn in new[] { hornL, hornR })
                {
                    c.Fill(horn, c.H(DarkRed), DarkRed.Darkened(0.5f), 0.6f);
                    c.Gradient(horn, c.H(Lit(Red, 0.35f)), c.H(Shade(DarkRed, 0.2f)));
                }
                foreach (float sx in new[] { -1f, 1f })
                {
                    c.Line(new Vector2(sx * 6.6f, -11.6f), new Vector2(sx * 5.1f, -12.1f), DarkRed.Darkened(0.4f) with { A = 0.6f }, 0.4f);
                    c.Line(new Vector2(sx * 7f, -13.6f), new Vector2(sx * 5.4f, -13.7f), DarkRed.Darkened(0.4f) with { A = 0.6f }, 0.4f);
                    c.Line(new Vector2(sx * 6.9f, -15.2f), new Vector2(sx * 5.9f, -15.2f), DarkRed.Darkened(0.4f) with { A = 0.5f }, 0.35f);
                }
                c.Line(new Vector2(-7.1f, -14.8f), new Vector2(-6.4f, -16.4f), c.H(Red.Lightened(0.5f)) with { A = 0.6f }, 0.4f);
                c.Line(new Vector2(5.8f, -16.4f), new Vector2(6.5f, -14.8f), c.H(Red.Lightened(0.5f)) with { A = 0.6f }, 0.4f);
                break;
            }
            case 14: // Pirate: a black tricorn edged all round with a dyed trim, and a little skull.
            {
                var hat = Smooth(new[]
                {
                    new Vector2(-9.2f, -10.4f), new Vector2(-7.4f, -14.8f), new Vector2(-3.6f, -16.8f), new Vector2(0, -15.2f),
                    new Vector2(3.6f, -16.8f), new Vector2(7.4f, -14.8f), new Vector2(9.2f, -10.4f), new Vector2(0, -11.8f),
                }, 1);
                // The trim is the outer rim of the hat itself; the black crown is the same outline moved in, so they always line up.
                var crownPts = Inset(hat, 0.75f);
                c.Fill(hat, c.H(Shade(c.Hat, 0.1f)), dyeLine, 0.6f);
                c.Gradient(hat, dyeLight, dyeShade);
                c.Fill(crownPts, c.H(HatBlack), HatBlack, 0.1f);
                c.Gradient(crownPts, c.H(HatGrey.Lerp(HatBlack, 0.3f)), c.H(HatBlack));
                c.Line(new Vector2(-5.2f, -15.2f), new Vector2(-1.4f, -14.9f), c.H(HatGrey.Lightened(0.2f)) with { A = 0.6f }, 0.4f);
                c.Line(new Vector2(-1.9f, -11.9f), new Vector2(1.9f, -13.1f), c.H(Bone), 0.5f);
                c.Line(new Vector2(-1.9f, -13.1f), new Vector2(1.9f, -11.9f), c.H(Bone), 0.5f);
                c.Circle(new Vector2(0, -13.6f), 1.3f, c.H(Bone));
                c.Poly(new[] { new Vector2(-0.6f, -12.5f), new Vector2(0.6f, -12.5f), new Vector2(0.5f, -11.8f), new Vector2(-0.5f, -11.8f) }, c.H(Bone));
                c.Circle(new Vector2(-0.45f, -13.7f), 0.34f, Ink);
                c.Circle(new Vector2(0.45f, -13.7f), 0.34f, Ink);
                break;
            }
        }
    }

    /// <summary>A five-pointed star around <paramref name="at"/>.</summary>
    private static Vector2[] StarShape(Vector2 at, float r)
    {
        var pts = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float a = -Mathf.Pi / 2 + i * Mathf.Pi / 5;
            pts[i] = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (i % 2 == 0 ? r : r * 0.45f);
        }
        return pts;
    }

    // ---------- arm ----------
    // A two-part arm in the cloak color, from the shoulder through a bending elbow (GunRig.Arm, sprite space) to the
    // hand on the grip. The shoulder slides from the chest (aiming up) to the front of the cloak (aiming level or down)
    // and the arm's length follows the reach, so the part showing past the body looks about the same at every aim. The
    // elbow folds as the gun kicks back and trails a little behind fast moves (AvatarPose.ArmSway). Drawn before the
    // body, so the body hides its root and it can never cover the face.

    private const float UpperArmRadius = 1.15f, ForearmRadius = 1.0f, ArmOutline = 0.55f, MaxSway = 2.6f;

    private static void DrawArm(Ctx c, int facing)
    {
        var p = c.P;
        var (grip, elbow, sh) = GunRig.Arm(SpriteGunAim(p, facing), p.Kick);
        var shoulder = G(sh);
        var e = G(elbow) + p.ArmSway.LimitLength(MaxSway);
        var upper = Shapes.Capsule(shoulder, e, UpperArmRadius);
        var fore = Shapes.Capsule(e, G(grip), ForearmRadius);
        // Both outlines first, then both fills, so the joint reads as one limb rather than two stacked pills.
        c.Fill(upper, Ink, Ink, ArmOutline);
        c.Fill(fore, Ink, Ink, ArmOutline);
        var col = c.H(Cloak(p.Body).Darkened(0.1f));
        c.Poly(upper, col.Darkened(0.08f));
        c.Poly(fore, col);
        c.Circle(e, ForearmRadius * 0.95f, col); // round elbow over the seam
    }

    // ---------- gun ----------

    private static void DrawGun(Ctx c, Vector2 bodyOrigin, float s, int facing)
    {
        var p = c.P;
        var (hand, angle) = GunRig.Pose(SpriteGunAim(p, facing), p.Kick);
        c.SetMatrix(GunXform(bodyOrigin, G(hand), angle, s, facing));

        var g = p.GunLook == default ? GunLook.Default : p.GunLook;
        const float ow = 0.45f;
        float muzzle = g.MuzzleX + 1, bt = g.BarrelThick;

        if (g.Stock > 0)
            c.Fill(Shapes.RoundRect(new Rect2(-1.2f - g.Stock, -2.5f, g.Stock + 0.8f, 2.4f), 0.8f), c.H(GunDark), Ink, ow);
        c.Fill(new[] { new Vector2(-0.3f, -0.2f), new Vector2(2f, -0.2f), new Vector2(1.5f, 3.3f), new Vector2(-0.8f, 3.3f) }, c.H(GunDark), Ink, ow);
        if (g.Mag > 0)
            c.Fill(Shapes.RoundRect(new Rect2(3f, -0.4f, 1.9f, g.Mag + 0.6f), 0.5f), c.H(GunMid), Ink, ow);
        c.Fill(Shapes.RoundRect(new Rect2(g.Receiver - 0.8f, -bt, muzzle - g.Receiver + 0.8f, bt), bt * 0.35f), c.H(GunMid), Ink, ow);
        c.Line(new Vector2(g.Receiver, -bt + 0.35f), new Vector2(muzzle - 1f, -bt + 0.35f), c.H(Lit(GunLight, 0.3f)), 0.4f);
        c.Line(new Vector2(g.Receiver, -0.35f), new Vector2(muzzle - 1f, -0.35f), c.H(Shade(GunDark, 0.4f)) with { A = 0.6f }, 0.35f);
        c.Fill(Shapes.RoundRect(new Rect2(muzzle - 1.3f, -bt - 0.35f, 1.4f, bt + 0.7f), 0.4f), c.H(GunDark), Ink, ow);
        var receiver = Shapes.RoundRect(new Rect2(-1.2f, -3.3f, g.Receiver + 0.6f, 3.6f), 1.1f);
        c.Fill(receiver, c.H(GunMid), Ink, ow);
        c.Gradient(receiver, c.H(GunLight), c.H(GunDark));
        c.Poly(Shapes.RoundRect(new Rect2(0.4f, -2.2f, g.Receiver - 1.6f, 0.9f), 0.45f), c.H(p.Body));
        if (g.Scope)
        {
            c.Fill(Shapes.RoundRect(new Rect2(0.8f, -5.5f, g.Receiver - 1.4f, 1.9f), 0.8f), c.H(GunDark), Ink, ow);
            c.Circle(new Vector2(g.Receiver - 0.8f, -4.55f), 0.55f, c.H(Shapes.Hdr(Visor, 1.3f)));
        }

        // The hand on the grip, in the cloak color (the arm behind the body reaches it).
        c.Fill(Shapes.Ellipse(G(GunRig.Grip), 1.45f, 1.35f, 14), c.H(Cloak(p.Body).Lightened(0.06f)), Ink, 0.5f);

        if (p.Flash > 0)
        {
            var at = new Vector2(muzzle + 0.6f, -bt * 0.5f);
            float k = p.Flash;
            c.Glow(at + new Vector2(2, 0), 6f + 6f * k, PaleYellow with { A = 0.7f * k });
            c.Poly(Shapes.Star(at + new Vector2(1.6f * k, 0), 2.2f + 3.4f * k, 1f + 0.6f * k, 5, k * 2), Shapes.Hdr(PaleYellow, 2.2f) with { A = Mathf.Min(1, k * 1.6f) });
            c.Circle(at + new Vector2(1f, 0), 1.2f * k + 0.4f, Shapes.Hdr(Colors.White, 2.5f));
        }
    }
}
