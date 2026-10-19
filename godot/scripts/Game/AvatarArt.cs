using System;
using System.Collections.Generic;
using Brawler.Core;
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
    public float Time;        // seconds; drives halo bob, propeller, visor scan
    public float WalkPhase;   // advances with distance walked
    public bool Grounded, Moving, Blink, Hurt;
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

/// <summary>A small pixel image authored as strings. Each char is a palette key; '.' and ' ' are empty.</summary>
public sealed class PixelArt
{
    public readonly int Ox, Oy;
    public readonly string[] Rows;
    public readonly bool Outline;
    private List<(int X, int Y)> _outline;

    public PixelArt(int ox, int oy, bool outline, params string[] rows)
    {
        Ox = ox; Oy = oy; Outline = outline; Rows = rows;
    }

    public int Top => Oy - (Outline ? 1 : 0);

    public bool Filled(int col, int row) =>
        row >= 0 && row < Rows.Length && col >= 0 && col < Rows[row].Length && Rows[row][col] is not ('.' or ' ');

    private List<(int, int)> OutlinePixels()
    {
        if (_outline != null) return _outline;
        _outline = new();
        int w = 0;
        foreach (var r in Rows) w = Math.Max(w, r.Length);
        for (int row = -1; row <= Rows.Length; row++)
            for (int col = -1; col <= w; col++)
                if (!Filled(col, row) && (Filled(col - 1, row) || Filled(col + 1, row) || Filled(col, row - 1) || Filled(col, row + 1)))
                    _outline.Add((col, row));
        return _outline;
    }

    public void Draw(CanvasItem ci, Func<char, Color> palette, int dx = 0, int dy = 0)
    {
        if (Outline)
            foreach (var (c, r) in OutlinePixels())
                ci.DrawRect(new Rect2(Ox + c + dx, Oy + r + dy, 1, 1), Palette.Outline);
        for (int r = 0; r < Rows.Length; r++)
        {
            var row = Rows[r];
            int c = 0;
            while (c < row.Length)
            {
                char ch = row[c];
                int start = c;
                while (c < row.Length && row[c] == ch) c++;
                if (ch is '.' or ' ') continue;
                ci.DrawRect(new Rect2(Ox + start + dx, Oy + r + dy, c - start, 1), palette(ch));
            }
        }
    }
}

/// <summary>
/// Procedural pixel art for players: body, face parts, hair, hats and the gun. Sprite space is facing right with the
/// body centered on (0,0) and covering x -7..6, y -9..8 (14x18, feet in the bottom two rows). Option indices follow
/// the name lists in <see cref="Appearance"/>.
/// </summary>
public static class AvatarArt
{
    private static readonly Color White = new("#f4f4f8"), OffWhite = new("#d4d4e2");
    private static readonly Color Red = new("#e8424f"), DarkRed = new("#9e2333");
    private static readonly Color Pink = new("#ff8fb8"), DarkPink = new("#d9608a");
    private static readonly Color Orange = new("#ffa033"), DarkOrange = new("#c46a12");
    private static readonly Color PaleYellow = new("#fff3a0"), Gold = new("#ffd23d"), DarkGold = new("#c9961a");
    private static readonly Color Blue = new("#3d9bff"), Metal = new("#9aa0b8"), DarkMetal = new("#5d6380");
    private static readonly Color Bone = new("#efe3c2"), HatBlack = new("#2a2a38"), HatGrey = new("#454560");
    private static readonly Color Visor = new("#3de6e0"), VisorLight = new("#c8fffb");
    private static readonly Color GunLight = new("#8a90aa"), GunMid = new("#575d78"), GunDark = new("#383c50");

    // ---------- body ----------

    private const int BodyTop = -9, BodyBottom = 6;

    private static int SpanL(int y) => y == -9 ? -5 : y == -8 || y == 6 ? -6 : -7;
    private static int SpanR(int y) => y == -9 ? 4 : y == -8 || y == 6 ? 5 : 6;
    private static bool InBody(int x, int y) => y >= BodyTop && y <= BodyBottom && x >= SpanL(y) && x <= SpanR(y);

    private enum Shade { Outline, Hi, Spec, Base, Back, Low }
    private static readonly List<(int X, int Y, Shade S)> BodyPixels = BuildBody();

    private static List<(int, int, Shade)> BuildBody()
    {
        var list = new List<(int, int, Shade)>();
        for (int y = BodyTop; y <= BodyBottom; y++)
            for (int x = SpanL(y); x <= SpanR(y); x++)
            {
                bool edge = !InBody(x - 1, y) || !InBody(x + 1, y) || !InBody(x, y - 1) || !InBody(x, y + 1);
                Shade s = edge ? Shade.Outline
                    : (x, y) is (-5, -7) or (-4, -7) or (-5, -6) ? Shade.Spec
                    : y <= -7 ? Shade.Hi
                    : y >= 4 ? Shade.Low
                    : x == SpanL(y) + 1 ? Shade.Back
                    : Shade.Base;
                list.Add((x, y, s));
            }
        return list;
    }

    // ---------- face parts ----------

    private static readonly PixelArt[] Noses =
    {
        null,
        new(2, -1, false, "SS"),
        new(2, -2, false, "LS", "SS"),
        new(3, -2, true, "LLLLL", "SSSS"),
        new(0, -2, false, "PPPPP", "PQPQP"),
        new(3, -2, true, "oooooo", "OOOOO"),
        new(2, -2, false, "Pr", "rR"),
    };

    private static readonly PixelArt[] Mouths =
    {
        new(-1, 0, false, "k....k", ".kkkk."),
        new(0, 1, false, "kkkk"),
        new(0, 0, false, ".kk.", "kRRk", ".kk."),
        new(-1, 0, false, "kkkkkk", "kwwwwk", ".kkkk."),
        new(-1, 0, false, ".kkkk.", "k....k"),
        new(-1, 0, false, "kkkkkk", ".w..w."),
        new(-1, 0, false, "k....k", ".kkkk.", "...PP.", "...PQ."),
        new(-1, -1, false, ".hhhh.", "hH..Hh", "..kk.."),
        new(0, 0, false, "k.k.k", ".k.k."),
    };

    // ---------- hair (# main, l light, d dark; color = hair dye) ----------

    private static readonly PixelArt[] Hairs =
    {
        null,
        new(-8, -10, true,
            "....##llll##....",
            "..###ll#######..",
            ".##############.",
            ".#########......",
            ".####...........",
            ".dd............."),
        new(-8, -12, true,
            "...#...##...#...",
            "..##..####.##...",
            "..#l#######l##..",
            ".##############.",
            ".##############.",
            ".######...###...",
            ".###............"),
        new(-8, -14, true,
            ".......##.......",
            "......###.......",
            ".....#l##.......",
            ".....####.......",
            "....#l###.......",
            "....#####d......"),
        new(-8, -10, true,
            "....##llll##....",
            "..###ll#######..",
            ".##############.",
            "##########......",
            "#####...........",
            "####............",
            "####............",
            "####............",
            "####............",
            "###d............",
            "##d.............",
            "#d.............."),
        new(-8, -10, true,
            "...##llll#####..",
            "..##ll#########.",
            ".##############.",
            "################",
            "####............",
            "####............",
            "###d............"),
        new(-10, -10, true,
            "......##llll##....",
            "....###ll#######..",
            "..r##############.",
            ".##########.......",
            ".##.####..........",
            "##..dd............",
            "##................",
            "##................",
            ".#................"),
        new(-9, -15, true,
            ".....##l#####.....",
            "...####ll#d####...",
            "..#d##l####d###...",
            ".###########d####.",
            "##d######d#######.",
            "#######d#####d####",
            "###d#########d####",
            "######d#####d#####",
            "######.......#####",
            "#####.........###.",
            "####d.............",
            ".##..............."),
    };

    // ---------- hats (# main, l light, d dark; color = hat dye) ----------

    private static readonly PixelArt[] Hats =
    {
        null,
        new(-8, -14, true,
            ".......l..........",
            ".....######.......",
            "....##ll####......",
            "...##l#######.....",
            "...##########.....",
            "..dddddddddddddddd"),
        new(-8, -17, true,
            "....KKKKKKKK....",
            "....KEKKKKKK....",
            "....KEKKKKKK....",
            "....KEKKKKKK....",
            "....########....",
            "....KKKKKKKK....",
            "....KKKKKKKK....",
            "..KKKKKKKKKKKK.."),
        new(-8, -15, true,
            ".......ww.......",
            "......wwWw......",
            ".....######.....",
            "....##l#####....",
            "...##l#######...",
            "..############..",
            "..d#d#d#d#d#d#..",
            ".d#d#d#d#d#d#d#."),
        new(-8, -14, true,
            "...g...gg...g...",
            "...gg..gg..gg...",
            "...gggggggggg...",
            "...grggbbggrg...",
            "...GGGGGGGGGG..."),
        PartyHat(),
        new(-11, -9, true,
            "d.................",
            ".dd.##############",
            "..dd..............",
            ".d................"),
        new(-10, -15, true,
            ".c................c",
            ".cc..............cc",
            "..cc...mmmmm....cc.",
            "...ccmmlmmmmmmmcc..",
            "....cmlmmmmmmmmmc..",
            ".....mmmmmmmmmmm...",
            "....McMMMMcMMMMcM.."),
        new(-8, -15, false,
            ".....yyyyyy.....",
            "....y......y....",
            ".....yyyyyy....."),
        new(-8, -12, true,
            ".....######.....",
            "....##l#####....",
            "...##l#######...",
            "...##########..."),
        new(-8, -19, true,
            "....ww..ww......",
            "...wwwwwwwww....",
            "..wwwwWwwwwww...",
            "..wwwwwwwwWww...",
            "...wwwwwwwww....",
            "....wwwwwwww....",
            "....WwwwwwwW....",
            "....WwwwwwwW....",
            "....WWWWWWWW....",
            "...WWWWWWWWWW..."),
    };

    private const int HaloHat = 8, PropellerHat = 9;

    private static PixelArt PartyHat()
    {
        // A striped cone, generated so the stripes stay even.
        var rows = new List<string> { new string('.', 7) + "yy" + new string('.', 7) };
        int[] half = { 1, 1, 2, 2, 3, 3, 4, 4, 5 };
        for (int i = 0; i < half.Length; i++)
        {
            char c = i % 3 == 2 ? 'w' : i % 3 == 1 ? 'l' : '#';
            int h = half[i];
            rows.Add(new string('.', 8 - h) + new string(c, h * 2) + new string('.', 8 - h));
        }
        return new PixelArt(-8, -19, true, rows.ToArray());
    }

    /// <summary>Topmost sprite row used by this look (hair, hat and outlines), for placing the HP bar.</summary>
    public static int TopY(Appearance look)
    {
        int top = BodyTop;
        if (Hairs[look.Hair] is { } h) top = Math.Min(top, h.Top);
        if (Hats[look.Hat] is { } hat) top = Math.Min(top, hat.Top - (look.Hat == HaloHat ? 1 : 0));
        if (look.Hat == PropellerHat) top = Math.Min(top, -14);
        return top;
    }

    // ---------- gun ----------

    private static readonly Dictionary<GunLook, PixelArt> GunCache = new();

    /// <summary>
    /// Gun sprite in gun space: x runs along the barrel, the hand grips at (0,0), +y is the grip side.
    /// Keys: m light metal, M mid metal, D dark metal, a player color, k black.
    /// </summary>
    private static PixelArt GunSprite(GunLook g)
    {
        if (GunCache.TryGetValue(g, out var art)) return art;
        int x0 = -1 - g.Stock, len = g.MuzzleX - x0 + 1;
        int y0 = -4 - (g.Scope ? 2 : 0), y1 = 2 + g.Mag;
        var grid = new char[y1 - y0 + 1, len];
        for (int r = 0; r < grid.GetLength(0); r++)
            for (int c = 0; c < len; c++) grid[r, c] = '.';
        void Set(int x, int y, char ch) { if (y >= y0 && y <= y1 && x >= x0 && x <= g.MuzzleX) grid[y - y0, x - x0] = ch; }

        // Receiver: x -1..Receiver-1, y -3..0 with a top highlight and a team-colored stripe.
        for (int x = -1; x < g.Receiver; x++)
        {
            Set(x, -3, 'm');
            Set(x, -2, x > 0 && x < g.Receiver - 1 ? 'a' : 'M');
            Set(x, -1, 'M');
            Set(x, 0, 'D');
        }
        // Barrel: from the receiver to the muzzle, BarrelThick rows ending at y -1.
        for (int x = g.Receiver; x <= g.MuzzleX; x++)
            for (int t = 0; t < g.BarrelThick; t++)
                Set(x, -1 - t, t == g.BarrelThick - 1 ? 'm' : 'M');
        Set(g.MuzzleX, -1, 'k');
        // Grip below the hand, magazine in front of it.
        Set(0, 1, 'D'); Set(1, 1, 'D'); Set(0, 2, 'D'); Set(1, 2, 'D');
        for (int i = 1; i <= g.Mag; i++) { Set(3, i, 'M'); Set(4, i, 'D'); }
        for (int x = x0; x < -1; x++) { Set(x, -2, 'D'); Set(x, -1, 'D'); }
        if (g.Scope)
            for (int x = 1; x < g.Receiver; x++) { Set(x, -5, 'D'); Set(x, -4, x == 1 ? 'V' : 'M'); }

        var rows = new string[grid.GetLength(0)];
        for (int r = 0; r < rows.Length; r++)
        {
            var chars = new char[len];
            for (int c = 0; c < len; c++) chars[c] = grid[r, c];
            rows[r] = new string(chars);
        }
        art = new PixelArt(x0, y0, true, rows);
        GunCache[g] = art;
        return art;
    }

    /// <summary>Where the hand holds the gun, relative to the body center (world units, unrotated).</summary>
    public static Vector2 GunPivot(Vector2 aim, float scale) => (new Vector2(0, 3f) + aim * 5f) * scale;

    /// <summary>Muzzle position relative to the body center, matching what Draw renders with no recoil.</summary>
    public static Vector2 Muzzle(Vector2 aim, int facing, float scale, GunLook g)
    {
        facing = facing < 0 ? -1 : 1;
        var local = new Vector2(g.MuzzleX + 1, -(g.BarrelThick * 0.5f) * facing) * scale;
        return GunPivot(aim, scale) + local.Rotated(aim.Angle());
    }

    // ---------- drawing ----------

    public static void Draw(CanvasItem ci, Vector2 origin, AvatarPose p)
    {
        var look = p.Look ?? Appearance.Default();
        float s = p.Scale <= 0 ? 1 : p.Scale;
        int facing = p.Facing < 0 ? -1 : 1;
        var body = p.Hurt ? Colors.White : p.Body;
        var hair = Palette.Dye(look.HairColor);
        var hat = Palette.Dye(look.HatColor);

        // Walk cycle: feet alternate and the body dips on the stride frames.
        int frame = p.Grounded && p.Moving ? ((int)p.WalkPhase & 3) : -1;
        int bob = frame is 0 or 2 ? 1 : 0;
        (int bx, int by, int fx, int fy) = !p.Grounded ? (-1, -1, 1, -1)
            : frame == 0 ? (-1, -1, 1, 0)
            : frame == 2 ? (1, 0, -1, -1)
            : (0, 0, 0, 0);

        ci.DrawSetTransform(origin, 0, new Vector2(facing * s, s));
        var shoe = body.Darkened(0.6f);
        ci.DrawRect(new Rect2(-5 + bx, 7 + by, 4, 2), shoe);
        ci.DrawRect(new Rect2(1 + fx, 7 + fy, 4, 2), shoe);
        ci.DrawRect(new Rect2(2 + fx, 7 + fy, 2, 1), body.Darkened(0.35f));
        ci.DrawRect(new Rect2(-4 + bx, 7 + by, 2, 1), body.Darkened(0.35f));

        ci.DrawSetTransform(origin + new Vector2(0, bob * s), 0, new Vector2(facing * s, s));
        foreach (var (x, y, sh) in BodyPixels)
        {
            var c = sh switch
            {
                Shade.Outline => body.Darkened(0.6f),
                Shade.Hi => body.Lightened(0.18f),
                Shade.Spec => body.Lightened(0.55f),
                Shade.Low => body.Darkened(0.18f),
                Shade.Back => body.Darkened(0.08f),
                _ => body,
            };
            ci.DrawRect(new Rect2(x, y, 1, 1), c);
        }

        Color Pal(char ch, Color main) => p.Hurt && ch is not ('k' or 'K') ? Colors.White : ch switch
        {
            '#' => main,
            'l' => main.Lightened(0.3f),
            'd' => main.Darkened(0.35f),
            'k' => Palette.Outline,
            'w' => White,
            'W' => OffWhite,
            's' => body,
            'S' => body.Darkened(0.3f),
            'L' => body.Lightened(0.25f),
            'h' => hair,
            'H' => hair.Darkened(0.35f),
            'r' => Red,
            'R' => DarkRed,
            'P' => Pink,
            'Q' => DarkPink,
            'o' => Orange,
            'O' => DarkOrange,
            'y' => PaleYellow,
            'g' => Gold,
            'G' => DarkGold,
            'b' => Blue,
            'm' => Metal,
            'M' => DarkMetal,
            'c' => Bone,
            'K' => HatBlack,
            'E' => HatGrey,
            'v' => Visor,
            'V' => VisorLight,
            _ => main,
        };

        Hairs[look.Hair]?.Draw(ci, ch => Pal(ch, hair));
        DrawEyes(ci, look.Eyes, p, facing, body);
        Noses[look.Nose]?.Draw(ci, ch => Pal(ch, body));
        Mouths[look.Mouth]?.Draw(ci, ch => Pal(ch, body));

        if (look.Hat == HaloHat)
        {
            int lift = (int)(p.Time * 2.5f) % 2;
            Hats[HaloHat].Draw(ci, ch => Pal(ch, hat), 0, -lift);
        }
        else Hats[look.Hat]?.Draw(ci, ch => Pal(ch, hat));
        if (look.Hat == PropellerHat) DrawPropeller(ci, p.Time, p.Hurt);

        if (p.Gun) DrawGun(ci, origin + new Vector2(0, bob * s), p, s, facing);
        ci.DrawSetTransformMatrix(Transform2D.Identity);
    }

    private static void DrawEyes(CanvasItem ci, int style, AvatarPose p, int facing, Color body)
    {
        var k = Palette.Outline;
        float ax = p.Aim.X * facing, ay = p.Aim.Y;
        int lookX = ax > 0.4f ? 1 : 0;
        int lookY = ay < -0.4f ? -1 : ay > 0.4f ? 1 : 0;
        void R(int x, int y, int w, int h, Color c) => ci.DrawRect(new Rect2(x, y, w, h), c);
        var white = p.Hurt ? Colors.White : White;

        switch (style)
        {
            case 0: // Round
            case 4: // Angry
                foreach (int ex in new[] { -3, 2 })
                {
                    if (p.Blink) { R(ex, -4, 3, 1, k); continue; }
                    R(ex, -6, 3, 4, white);
                    R(ex + lookX, -5 + lookY, 2, 2, k);
                }
                if (style == 4)
                {
                    R(-4, -8, 2, 1, k); R(-2, -7, 2, 1, k);
                    R(2, -7, 2, 1, k); R(4, -8, 2, 1, k);
                    if (!p.Blink) { R(-1, -6, 1, 1, body); R(2, -6, 1, 1, body); }
                }
                break;
            case 1: // Big
                foreach (int ex in new[] { -4, 2 })
                {
                    if (p.Blink) { R(ex, -5, 4, 1, k); continue; }
                    R(ex, -7, 4, 5, white);
                    R(ex + 1 + lookX, -6 + 1 + lookY, 2, 2, k);
                    R(ex + 1 + lookX, -6 + 1 + lookY, 1, 1, new Color(1, 1, 1, 0.8f));
                }
                break;
            case 2: // Dots
                foreach (int ex in new[] { -2, 3 })
                {
                    if (p.Blink) R(ex + lookX, -4, 2, 1, k);
                    else R(ex + lookX, -5 + lookY, 2, 2, k);
                }
                break;
            case 3: // Sleepy
                foreach (int ex in new[] { -3, 2 })
                {
                    R(ex, -5, 3, 1, k);
                    if (p.Blink) continue;
                    R(ex, -4, 3, 2, white);
                    R(ex + lookX, -4, 2, 1, k);
                }
                break;
            case 5: // Visor
            {
                R(-4, -6, 11, 4, k);
                R(-3, -5, 9, 2, p.Hurt ? Colors.White : Visor);
                int scan = (int)(p.Time * 8) % 16;
                int sx = scan < 8 ? scan : 15 - scan;
                R(-3 + sx, -5, 2, 1, VisorLight);
                break;
            }
            case 6: // Cyclops
                if (p.Blink) { R(-1, -5, 5, 1, k); break; }
                R(0, -7, 3, 5, white);
                R(-1, -6, 5, 3, white);
                R(0 + lookX, -6 + lookY, 2, 2, k);
                break;
            case 7: // Shades
                R(-4, -5, 4, 2, k); R(2, -5, 4, 2, k);
                R(0, -5, 2, 1, k); R(-6, -5, 2, 1, k);
                R(-4, -5, 1, 1, HatGrey); R(2, -5, 1, 1, HatGrey);
                break;
            case 8: // Happy
                foreach (int ex in new[] { -3, 2 })
                {
                    R(ex, -4, 1, 1, k); R(ex + 1, -5, 1, 1, k); R(ex + 2, -4, 1, 1, k);
                }
                R(-5, -2, 2, 1, Pink with { A = 0.8f }); R(5, -2, 1, 1, Pink with { A = 0.8f });
                break;
        }
    }

    private static void DrawPropeller(CanvasItem ci, float time, bool hurt)
    {
        var k = Palette.Outline;
        ci.DrawRect(new Rect2(-1, -14, 2, 2), k);
        int f = (int)(time * 14) % 3;
        Color a = hurt ? Colors.White : Red, b = hurt ? Colors.White : Blue;
        if (f == 0) { ci.DrawRect(new Rect2(-6, -15, 5, 1), a); ci.DrawRect(new Rect2(1, -15, 5, 1), b); }
        else if (f == 1) { ci.DrawRect(new Rect2(-3, -15, 3, 1), b); ci.DrawRect(new Rect2(0, -15, 3, 1), a); }
        else ci.DrawRect(new Rect2(-1, -15, 2, 1), a);
    }

    private static void DrawGun(CanvasItem ci, Vector2 origin, AvatarPose p, float s, int facing)
    {
        var aim = p.GunAim.LengthSquared() > 0.01f ? p.GunAim.Normalized()
            : p.Aim.LengthSquared() > 0.01f ? p.Aim.Normalized() : new Vector2(facing, 0);
        float angle = aim.Angle() - p.Kick * 0.35f * facing;
        var pivot = origin + GunPivot(aim, s) - aim * (p.Kick * 2.5f * s);
        // Flip the gun's y when aiming left so the grip always hangs down.
        ci.DrawSetTransform(pivot, angle, new Vector2(s, s * facing));

        var gl = p.GunLook == default ? GunLook.Default : p.GunLook;
        var g = GunSprite(gl);
        var team = p.Body;
        g.Draw(ci, ch => p.Hurt ? Colors.White : ch switch
        {
            'm' => GunLight,
            'M' => GunMid,
            'D' => GunDark,
            'a' => team,
            'V' => Visor,
            _ => Palette.Outline,
        });

        // The hand on the grip, in the body color.
        var hand = p.Hurt ? Colors.White : p.Body;
        ci.DrawRect(new Rect2(-1, -2, 4, 4), hand.Darkened(0.6f));
        ci.DrawRect(new Rect2(0, -1, 2, 2), hand);
        ci.DrawRect(new Rect2(0, -1, 1, 1), hand.Lightened(0.3f));

        if (p.Flash > 0)
        {
            int mx = gl.MuzzleX + 1;
            float cy = -gl.BarrelThick * 0.5f;
            bool big = p.Flash > 0.5f;
            ci.DrawRect(new Rect2(mx, cy - 1.5f, big ? 3 : 2, 3), PaleYellow);
            ci.DrawRect(new Rect2(mx, cy - 0.5f, big ? 5 : 3, 1), White);
            if (big)
            {
                ci.DrawRect(new Rect2(mx + 1, cy - 3, 1, 1), PaleYellow);
                ci.DrawRect(new Rect2(mx + 1, cy + 2, 1, 1), PaleYellow);
            }
        }
    }
}
