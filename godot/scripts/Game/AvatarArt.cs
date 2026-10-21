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
    private static readonly float[] HatTop = { -9.5f, -14.6f, -21.6f, -18.5f, -17.7f, -22f, -9.5f, -19f, -17.4f, -16.4f, -22.6f };
    // How far each hat sits below where it is drawn, so it is worn over the top of the mask rather than perched on it.
    private static readonly float[] HatDrop = { 0, 2.2f, 2.2f, 2f, 2.2f, 2.2f, 1.2f, 2f, 1f, 2.2f, 2.2f };

    /// <summary>Topmost point used by this look (hair and hat), for placing the HP bar.</summary>
    public static float TopY(Appearance look)
        => Mathf.Min(HairTop[Math.Clamp(look.Hair, 0, HairTop.Length - 1)], HatTop[Math.Clamp(look.Hat, 0, HatTop.Length - 1)] + HatDrop[Math.Clamp(look.Hat, 0, HatDrop.Length - 1)]);

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
        public Color Mask, MaskShade; // the bone mask, tinted toward the player's color
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
        // A wash of the player's color over the bone, so masks tell players apart as well as cloaks.
        c.Mask = MaskBone.Lerp(p.Body, 0.15f);
        c.MaskShade = MaskShade.Lerp(p.Body, 0.1f);

        var rig = Solve(p, facing);
        var scale = new Vector2(facing * s, s);
        ci.DrawSetTransform(origin, 0, scale);
        DrawLegs(c, rig);

        // Everything above the hips (arms, body, face, hair, hat, gun) is drawn around the animated torso origin.
        var bodyOrigin = origin + BodyOffset(p);
        ci.DrawSetTransform(bodyOrigin, 0, scale);
        if (p.Gun) DrawArm(c, facing); // behind the body, in body (sprite) space, so it rides along with it
        DrawHairBack(c, look.Hair);
        DrawBody(c, rig.Breathe);
        DrawEyes(c, look.Eyes, facing);
        DrawMouth(c, look.Mouth, bodyOrigin, scale);
        DrawHair(c, look.Hair);
        // Hats are drawn with their brim on the crown; HatDrop pulls each one down so it fits over the mask.
        ci.DrawSetTransform(bodyOrigin + new Vector2(0, HatDrop[Math.Clamp(look.Hat, 0, HatDrop.Length - 1)] * s), 0, scale);
        DrawHat(c, look.Hat);
        ci.DrawSetTransform(bodyOrigin, 0, scale);

        if (p.Gun) DrawGun(c, bodyOrigin, s, facing);
        ci.DrawSetTransformMatrix(Transform2D.Identity);
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
        Leg(c, rig, -HipX, rig.Back, rig.BackTilt, rig.BackToe, c.H(cloak.Darkened(0.45f)), c.H(boot), Ink);
        Leg(c, rig, HipX, rig.Front, rig.FrontTilt, rig.FrontToe, c.H(cloak.Darkened(0.3f)), c.H(boot.Lightened(0.06f)), Ink);
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

    // A pale bone mask over a dark cloak, in the manner of a hollow little bug knight. The cloak takes a dark shade of
    // the player's color and a collar in the full color, so players stay easy to tell apart; the mask is the same for
    // everyone and carries the face. The mask spans y -9.5..MaskBottom, the cloak hangs from under it to a ragged hem.
    private const float MaskBottom = 1.35f;
    private static readonly Color MaskBone = new("#ece6d8"), MaskShade = new("#aaa396"), MaskDeep = new("#7d776d");
    private static readonly Color Hollow = new("#0c0a10");

    /// <summary>The cloak's color for a player color: darker and a little greyer, like dyed cloth in shadow.</summary>
    private static Color Cloak(Color body) => body.Lerp(new Color("#4a4652"), 0.15f).Darkened(0.3f);

    private static readonly Vector2[] MaskOutline = BuildMask();

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
        var ci = c.Ci;
        var p = c.P;
        var cloak = Cloak(p.Body);
        // Breathing swells the cloak a little at the sides.
        var cl = new Vector2[CloakOutline.Length];
        for (int i = 0; i < cl.Length; i++) cl[i] = CloakOutline[i] * new Vector2(1 + breathe * 0.06f, 1);
        Shapes.Fill(ci, cl, c.H(cloak), Ink, 0.9f);
        Shapes.Gradient(ci, cl, c.H(cloak.Lightened(0.14f)), c.H(cloak.Darkened(0.3f)));
        // A couple of folds.
        var fold = Ink with { A = 0.35f };
        ci.DrawLine(new Vector2(-2.4f, 0.6f), new Vector2(-3.1f, 3.6f), fold, 0.4f, true);
        ci.DrawLine(new Vector2(2.6f, 0.8f), new Vector2(3.3f, 3.9f), fold, 0.4f, true);
        // The collar in the player's own color.
        var collar = Shapes.RoundRect(new Rect2(-6.2f, -1.2f, 12.6f, 2.4f), 1.2f);
        Shapes.Fill(ci, collar, c.H(p.Body.Lerp(c.MaskShade, 0.12f)), Ink, 0.55f);
        ci.DrawLine(new Vector2(-5f, -0.6f), new Vector2(5.2f, -0.6f), c.H(p.Body.Lightened(0.35f)) with { A = 0.55f }, 0.35f, true);

        // The mask: bone white, shaded toward the jaw and the back, with a soft sheen on the brow.
        Shapes.Fill(ci, MaskOutline, c.H(c.Mask), Ink, 1.0f);
        Shapes.Gradient(ci, MaskOutline, c.H(c.Mask.Lightened(0.06f)), c.H(c.MaskShade));
        ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-5.1f, -3.2f), 1.2f, 4f, 16), c.H(c.MaskShade) with { A = 0.3f });
        ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(-1.2f, -7.8f), 3.4f, 1f, 16), new Color(1, 1, 1, 0.3f));
    }

    // ---------- eyes ----------
    // Hollow black eye holes in the mask. They lean a little toward the aim, so you can see where someone looks.

    private static void DrawEyes(Ctx c, int style, int facing)
    {
        var ci = c.Ci;
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
            ci.DrawColoredPolygon(pts, hole);
        }

        // Covers the top of a hollow with mask (a lid, a scowl), and carves its edge.
        void Lid(Vector2 a, Vector2 b, float above)
        {
            ci.DrawColoredPolygon(new[] { a, b, b + new Vector2(0, -above), a + new Vector2(0, -above) }, c.H(c.Mask.Darkened(0.04f)));
            ci.DrawLine(a, b, Ink, 0.6f, true);
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
                ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(-4.8f, -5.6f, 11f, p.Blink ? 0.8f : 2.6f), 1.2f), hole);
                if (p.Blink) break;
                float u = Mathf.PingPong(p.Time * 0.9f, 1f) * 1.6f;
                foreach (float x in new[] { -2.2f, 2.8f })
                {
                    var at = new Vector2(x + u + look.X, -4.3f);
                    GlowTex.Draw(ci, at, 2.2f, new Color("#bfe3ff") with { A = 0.5f });
                    ci.DrawCircle(at, 0.55f, p.Hurt ? Colors.White : Shapes.Hdr(new Color("#d8efff"), 1.4f), true, -1, true);
                }
                break;
            }
            case 6: // Cyclops
                Eye(new Vector2(0.6f, -4.5f), 2.3f, 3.1f);
                break;
            case 7: // Shades: a dark band across the mask.
            {
                var band = Shapes.RoundRect(new Rect2(-5.4f, -6.0f, 11.6f, 3.2f), 1f);
                ci.DrawColoredPolygon(band, hole);
                ci.DrawLine(new Vector2(-4.4f, -5.2f), new Vector2(-1.8f, -5.2f), new Color(1, 1, 1, 0.25f), 0.5f, true);
                ci.DrawLine(new Vector2(-7.3f, -5.6f), new Vector2(-5.3f, -5.2f), Ink, 0.7f, true);
                break;
            }
            case 8: // Happy: crescents curving up.
                foreach (var e in new[] { l + new Vector2(0, 0.4f), r + new Vector2(0, 0.4f) })
                {
                    var pts = new List<Vector2>();
                    for (int i = 0; i <= 10; i++) pts.Add(e + look + Vector2.FromAngle(Mathf.Pi + 0.25f + i * (Mathf.Pi - 0.5f) / 10) * 1.9f);
                    for (int i = 10; i >= 0; i--) pts.Add(e + look + new Vector2(0, 0.5f) + Vector2.FromAngle(Mathf.Pi + 0.45f + i * (Mathf.Pi - 0.9f) / 10) * 1.3f);
                    ci.DrawColoredPolygon(pts.ToArray(), hole);
                }
                break;
        }
    }

    // ---------- mouth ----------
    // Thin carved lines on the mask, drawn 1.4 px higher than the old cartoon mouths so they sit above the collar.

    private static readonly Vector2[] GrinShape = BuildGrin();

    private static Vector2[] BuildGrin()
    {
        var pts = new List<Vector2>();
        for (int i = 0; i <= 12; i++)
        {
            float a = i * Mathf.Pi / 12;
            pts.Add(new Vector2(1.5f + Mathf.Cos(a) * 2.5f, Mathf.Sin(a) * 1.9f));
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
        var ci = c.Ci;
        ci.DrawSetTransform(bodyOrigin + new Vector2(0, -1.4f * scale.Y), 0, scale);
        var ink = Ink with { A = 0.85f };
        switch (style)
        {
            case 0: // Smile
                ci.DrawArc(new Vector2(1.4f, -0.6f), 2.2f, 0.55f, Mathf.Pi - 0.55f, 14, ink, 0.6f, true);
                break;
            case 1: // Flat
                ci.DrawLine(new Vector2(0f, 1.1f), new Vector2(3f, 1.1f), ink, 0.6f, true);
                break;
            case 2: // Open
                ci.DrawColoredPolygon(Shapes.Ellipse(new Vector2(1.4f, 1f), 1.1f, 1.25f, 16), c.H(Hollow));
                break;
            case 3: // Grin: a dark crescent with bone teeth.
                ci.DrawColoredPolygon(GrinShape, c.H(Hollow));
                for (float x = -0.4f; x <= 3.4f; x += 1.25f)
                    ci.DrawColoredPolygon(new[] { new Vector2(x, 0), new Vector2(x + 0.9f, 0), new Vector2(x + 0.45f, 0.8f) }, c.H(c.Mask));
                break;
            case 4: // Frown
                ci.DrawArc(new Vector2(1.4f, 2.9f), 2.1f, Mathf.Pi + 0.6f, Mathf.Tau - 0.6f, 14, ink, 0.6f, true);
                break;
            case 5: // Fangs
                ci.DrawArc(new Vector2(1.4f, -3f), 3.6f, 1.1f, Mathf.Pi - 1.1f, 14, ink, 0.6f, true);
                foreach (float fx in new[] { 0.2f, 2.6f })
                    Shapes.Fill(ci, new[] { new Vector2(fx - 0.55f, 0.4f), new Vector2(fx + 0.55f, 0.4f), new Vector2(fx, 1.9f) }, c.H(c.Mask), Ink, 0.3f);
                break;
            case 6: // Tongue
                ci.DrawArc(new Vector2(1.4f, -0.6f), 2.2f, 0.55f, Mathf.Pi - 0.55f, 14, ink, 0.6f, true);
                Shapes.Fill(ci, Shapes.Capsule(new Vector2(2.4f, 1.4f), new Vector2(2.5f, 2.4f), 0.85f), c.H(new Color("#a86a76")), Ink, 0.35f);
                break;
            case 7: // Mustache (hair dye), a little higher still.
                ci.DrawLine(new Vector2(0.6f, 1.5f), new Vector2(2.3f, 1.5f), ink, 0.5f, true);
                Shapes.Fill(ci, Shapes.Offset(MustacheShape, new Vector2(0, 0.4f)), c.H(c.Hair), Ink, 0.45f);
                break;
            case 8: // Cat
                ci.DrawArc(new Vector2(0.4f, 0.3f), 1f, 0.2f, Mathf.Pi - 0.2f, 10, ink, 0.6f, true);
                ci.DrawArc(new Vector2(2.4f, 0.3f), 1f, 0.2f, Mathf.Pi - 0.2f, 10, ink, 0.6f, true);
                break;
        }
        ci.DrawSetTransform(bodyOrigin, 0, scale);
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

    // ---------- arm ----------
    // A two-part arm in the cloak color, from the shoulder (GunRig.Shoulder, sprite space) through a bending elbow
    // (GunRig.Elbow) to the hand on the grip. The elbow straightens as the gun reaches out, folds as it comes in or
    // kicks back, and trails a little behind fast moves (AvatarPose.ArmSway). Drawn before the body, so the body hides
    // its root and it can never cover the face; only the part reaching out past the body shows (over the head when
    // aiming up).

    private const float UpperArmRadius = 1.15f, ForearmRadius = 1.0f, ArmOutline = 0.55f, MaxSway = 2.6f;

    private static void DrawArm(Ctx c, int facing)
    {
        var p = c.P;
        var (grip, elbow) = GunRig.Arm(SpriteGunAim(p, facing), p.Kick);
        var shoulder = G(GunRig.Shoulder);
        var e = G(elbow) + p.ArmSway.LimitLength(MaxSway);
        var upper = Shapes.Capsule(shoulder, e, UpperArmRadius);
        var fore = Shapes.Capsule(e, G(grip), ForearmRadius);
        // Both outlines first, then both fills, so the joint reads as one limb rather than two stacked pills.
        Shapes.Fill(c.Ci, upper, Ink, Ink, ArmOutline);
        Shapes.Fill(c.Ci, fore, Ink, Ink, ArmOutline);
        var col = c.H(Cloak(p.Body).Darkened(0.1f));
        c.Ci.DrawColoredPolygon(upper, col.Darkened(0.08f));
        c.Ci.DrawColoredPolygon(fore, col);
        c.Ci.DrawCircle(e, ForearmRadius * 0.95f, col, true, -1, true); // round elbow over the seam
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

        // The hand on the grip, in the cloak color (the arm behind the body reaches it).
        Shapes.Fill(ci, Shapes.Ellipse(G(GunRig.Grip), 1.45f, 1.35f, 14), c.H(Cloak(p.Body).Lightened(0.06f)), Ink, 0.5f);

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
