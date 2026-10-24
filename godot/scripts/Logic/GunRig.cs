using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>Gun shape parameters. Every gun is built from these, so cards can later swap in a different look.</summary>
public readonly record struct GunLook(int Receiver = 5, int Barrel = 6, int BarrelThick = 2, int Mag = 0, bool Scope = false, int Stock = 0)
{
    // Spelled out: new() on a record struct skips the primary constructor and gives all zeros.
    public static readonly GunLook Default = new(Receiver: 5, Barrel: 6, BarrelThick: 2, Mag: 0, Scope: false, Stock: 0);
    public int MuzzleX => Receiver + Barrel - 1;
    /// <summary>Where bullets leave the gun, in gun space (x along the barrel, y down toward the grip).</summary>
    public Vector2 MuzzleLocal => new(MuzzleX + 1, -BarrelThick * 0.5f);
}

/// <summary>
/// Where the arms hold the gun. Shared by the art (Game/AvatarArt) and the host's bullet origin, so the muzzle the
/// players see is the muzzle bullets come from. Engine-independent so the unit tests can check it.
///
/// Spaces: <b>sprite space</b> is the character facing right at Size 1, body centered on (0,0) and covering
/// x -7..7, y -9.5..3.4 above two legs reaching y 9 (see <see cref="Body"/>; the art also bobs the body while walking,
/// which the host muzzle ignores: it uses this fixed rig). <b>Body space</b> is sprite space mirrored by facing and scaled by
/// size, relative to the body center. <b>Gun space</b> has +x along the barrel and +y toward the grip, with the
/// shooting hand at the origin.
///
/// The hand follows <see cref="HandPath"/>, a curve around the front of the body chosen so the gun (pointing along
/// the aim from the hand) never crosses the eyes, nose or mouth of any face option (<see cref="Face"/>) at any aim
/// the facing rules allow (sprite-space aim x &gt;= -0.15), recoil included. The unit tests sweep every angle.
/// One arm holds the gun. It is drawn behind the body, so only the gun and hand can ever overlap the face.
/// The arm is purely visual: its shoulder and length follow the aim (<see cref="ShoulderPath"/>, <see cref="ArmLengths"/>)
/// so the part showing past the body looks about the same length at every angle; the hand, gun and muzzle don't depend on it.
/// </summary>
public static class GunRig
{
    /// <summary>
    /// Where the arm starts (sprite space) for each sprite-space aim angle in degrees, interpolated in between. The arm
    /// is drawn behind the body, so the shoulder only decides where it comes out: aiming up it starts high on the chest
    /// and the head hides most of it; aiming level or down it slides to the front of the cloak (which spans y -1.6..3.5,
    /// see Game/AvatarArt.CloakOutline), so the arm leaves the body near the hand instead of running along under it.
    /// </summary>
    public static readonly (float Deg, Vector2 Shoulder)[] ShoulderPath =
    {
        (-100, new(1.0f, -1.0f)),
        (-30, new(1.0f, -1.0f)),
        (-10, new(3.4f, 0.2f)),
        (15, new(4.6f, 1.2f)),
        (45, new(4.8f, 1.6f)),
        (100, new(4.0f, 1.6f)),
    };

    /// <summary>How much longer than the rest reach the arm is, so the elbow keeps the same slight bend at every aim.</summary>
    public const float ArmSlack = 1.06f;

    /// <summary>The upper arm's share of the arm's length (the forearm gets the rest).</summary>
    public const float UpperShare = 0.48f;

    /// <summary>
    /// Where the shooting hand sits (sprite space) for each sprite-space aim angle in degrees (-90 up, 0 ahead,
    /// 90 down), interpolated in between. The hand moves rather than the gun pivoting on the chest: aiming straight up
    /// it is raised over the top of the head (y -9.5) with the gun held above it; in between it swings round in front
    /// of the face; level, the gun is held at chest height past the nose; aiming down it hangs in front of the legs
    /// (the body sits on legs below y 3.4, see Game/AvatarArt.BodyBottom) rather than into them.
    /// </summary>
    public static readonly (float Deg, Vector2 Hand)[] HandPath =
    {
        (-100, new(-0.3f, -13.6f)),
        (-90, new(0.6f, -13.6f)),
        (-75, new(4.6f, -13.0f)),
        (-60, new(8.4f, -11.4f)),
        (-45, new(11.6f, -8.8f)),
        (-30, new(14.0f, -5.0f)),
        (-15, new(15.2f, -0.4f)),
        (-6, new(14.2f, 3.0f)),
        (0, new(12.0f, 4.2f)),
        (15, new(10.6f, 5.6f)),
        (30, new(9.2f, 6.6f)),
        (60, new(7.8f, 7.6f)),
        (90, new(5.5f, 8.0f)),
        (100, new(4.0f, 8.0f)),
    };

    /// <summary>Where the gun hand grips, in gun space (the arm ends here).</summary>
    public static readonly Vector2 Grip = new(0.6f, 0.2f);

    /// <summary>
    /// Two-bone arm: the elbow (sprite space) for an arm from <paramref name="shoulder"/> to <paramref name="hand"/>.
    /// It bends to the clockwise side of the shoulder→hand line (below it when reaching ahead, in front when reaching
    /// up), so the elbow hangs as gravity would have it. Out of reach, the arm goes straight toward the hand.
    /// </summary>
    public static Vector2 Elbow(Vector2 shoulder, Vector2 hand, float upper, float fore)
    {
        var d = hand - shoulder;
        float len = d.Length();
        if (len < 1e-4f) return shoulder + new Vector2(0, upper);
        var dir = d / len;
        float reach = Math.Clamp(len, Math.Abs(upper - fore) + 1e-3f, upper + fore);
        // Law of cosines: distance along the line to the elbow's foot, then how far it sticks out.
        float along = (upper * upper - fore * fore + reach * reach) / (2 * reach);
        float side = MathF.Sqrt(MathF.Max(0, upper * upper - along * along));
        return shoulder + dir * along + new Vector2(-dir.Y, dir.X) * side;
    }

    /// <summary>The shoulder (sprite space) for a sprite-space aim, from <see cref="ShoulderPath"/>.</summary>
    public static Vector2 Shoulder(Vector2 spriteAim) => Along(ShoulderPath, spriteAim);

    /// <summary>
    /// Upper arm and forearm lengths for a sprite-space aim: <see cref="ArmSlack"/> times the shoulder-to-grip distance
    /// with no recoil, so the elbow has the same slight bend at every aim and folds further as recoil pulls the hand in.
    /// </summary>
    public static (float Upper, float Fore) ArmLengths(Vector2 spriteAim)
    {
        var (hand, angle) = Pose(spriteAim);
        float len = Vector2.Distance(Shoulder(spriteAim), ToSprite(Grip, hand, angle)) * ArmSlack;
        return (len * UpperShare, len * (1 - UpperShare));
    }

    /// <summary>The hand's grip point, the elbow and the shoulder in sprite space for a sprite-space aim and recoil.</summary>
    public static (Vector2 Grip, Vector2 Elbow, Vector2 Shoulder) Arm(Vector2 spriteAim, float kick = 0)
    {
        var (hand, angle) = Pose(spriteAim, kick);
        var grip = ToSprite(Grip, hand, angle);
        var shoulder = Shoulder(spriteAim);
        var (upper, fore) = ArmLengths(spriteAim);
        return (grip, Elbow(shoulder, grip, upper, fore), shoulder);
    }

    /// <summary>Recoil: how far the hand is pushed back and how much the muzzle climbs (radians) at kick 1.</summary>
    public const float KickBack = 1.6f, KickClimb = 0.22f;

    /// <summary>Facing rules let a player aim at most this far behind themselves (|aim.x| must pass 0.15 to turn).</summary>
    public const float MaxBackAimX = 0.15f;

    /// <summary>Aim in sprite space: mirrored so the character faces right. Falls back to straight ahead.</summary>
    public static Vector2 SpriteAim(Vector2 aim, int facing)
    {
        var a = new Vector2(aim.X * (facing < 0 ? -1 : 1), aim.Y);
        return a.LengthSquared() > 1e-4f ? Vector2.Normalize(a) : Vector2.UnitX;
    }

    /// <summary>The shooting hand (gun-space origin) in sprite space for a sprite-space aim, with no recoil.</summary>
    public static Vector2 Hand(Vector2 spriteAim) => Along(HandPath, spriteAim);

    /// <summary>A point from a table keyed by sprite-space aim angle (degrees), interpolated between entries.</summary>
    private static Vector2 Along((float Deg, Vector2 At)[] path, Vector2 spriteAim)
    {
        float deg = Math.Clamp(MathF.Atan2(spriteAim.Y, spriteAim.X) * 180 / MathF.PI, path[0].Deg, path[^1].Deg);
        for (int i = 1; i < path.Length; i++)
            if (deg <= path[i].Deg)
            {
                var (d0, h0) = path[i - 1];
                var (d1, h1) = path[i];
                return Vector2.Lerp(h0, h1, (deg - d0) / (d1 - d0));
            }
        return path[^1].At;
    }

    /// <summary>Gun angle (radians, sprite space) and hand position for a sprite-space aim and recoil 0..1.</summary>
    public static (Vector2 Hand, float Angle) Pose(Vector2 spriteAim, float kick = 0)
    {
        kick = Math.Clamp(kick, 0, 1);
        float angle = MathF.Atan2(spriteAim.Y, spriteAim.X) - kick * KickClimb;
        return (Hand(spriteAim) - spriteAim * (kick * KickBack), angle);
    }

    /// <summary>Gun space to sprite space.</summary>
    public static Vector2 ToSprite(Vector2 gunLocal, Vector2 hand, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return hand + new Vector2(gunLocal.X * c - gunLocal.Y * s, gunLocal.X * s + gunLocal.Y * c);
    }

    /// <summary>Sprite space to body space (mirror by facing, scale by size).</summary>
    public static Vector2 ToBody(Vector2 sprite, int facing, float scale)
        => new(sprite.X * (facing < 0 ? -1 : 1) * scale, sprite.Y * scale);

    /// <summary>
    /// Muzzle relative to the body center (body space) for a world aim, matching what the art draws with no recoil.
    /// Pure function of aim, facing, size and gun look, so every peer computes the same point.
    /// </summary>
    public static Vector2 Muzzle(Vector2 aim, int facing, float scale, GunLook g)
    {
        var a = SpriteAim(aim, facing);
        var (hand, angle) = Pose(a);
        return ToBody(ToSprite(g.MuzzleLocal, hand, angle), facing, scale);
    }

    /// <summary>Gun-space rectangles (x, y, w, h) covering everything drawn with the gun, hand included, plus outline.</summary>
    public static List<(float X, float Y, float W, float H)> Footprint(GunLook g)
    {
        const float o = 0.3f; // half the outline width, rounded up
        float muzzle = g.MuzzleX + 1, bt = g.BarrelThick;
        var r = new List<(float, float, float, float)>
        {
            (-1.2f, -3.3f, g.Receiver + 0.6f, 3.6f),                        // receiver
            (g.Receiver - 0.8f, -bt, muzzle - g.Receiver + 0.8f, bt),       // barrel
            (muzzle - 1.3f, -bt - 0.35f, 1.4f, bt + 0.7f),                  // muzzle cap
            (-0.8f, -0.2f, 2.8f, 3.5f),                                     // grip
            (0.6f - 1.45f, 0.2f - 1.35f, 2.9f, 2.7f),                       // hand
        };
        if (g.Stock > 0) r.Add((-1.2f - g.Stock, -2.5f, g.Stock + 0.8f, 2.4f));
        if (g.Mag > 0) r.Add((3f, -0.4f, 1.9f, g.Mag + 0.6f));
        if (g.Scope) r.Add((0.8f, -5.5f, g.Receiver - 1.4f, 1.9f));
        for (int i = 0; i < r.Count; i++)
        {
            var (x, y, w, h) = r[i];
            r[i] = (x - o, y - o, w + 2 * o, h + 2 * o);
        }
        return r;
    }

    /// <summary>
    /// Sprite-space boxes (x, y, w, h) around every face option's eyes, nose and mouth (see Game/AvatarArt), which the
    /// gun must never cover.
    /// </summary>
    public static readonly (float X, float Y, float W, float H)[] Face =
    {
        (-7.6f, -8.2f, 13.8f, 7.2f), // eyes, brows, shades and their arm
        (6.2f, -6.8f, 1.4f, 6.4f),   // front edge of the visor, happy cheek
        (2.0f, -3.4f, 4.0f, 3.8f),   // noses: button, pig, clown
        (6.0f, -3.3f, 3.9f, 3.3f),   // long nose and beak, out to the tip
        (-3.8f, -1.8f, 10.4f, 5.7f), // mouths, mustache and tongue
    };
}
