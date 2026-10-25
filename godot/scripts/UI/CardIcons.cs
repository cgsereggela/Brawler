using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// The glyph on a card's header. Every built-in card has its own, made from a small kit of shapes (bullets, bodies,
/// boots, shields, hearts, clocks…) in white on the category color, with a dark drop shadow, so a card reads at a
/// glance and cards in one category still look different. Player-made cards get their category's glyph.
/// The icon fits in about 48×36 px around <c>c</c>.
/// </summary>
public static class CardIcons
{
    private static readonly Color Blood = new("#ff4d6a");

    public static void Draw(CanvasItem ci, CardDef card, Vector2 c, Color col, float t)
    {
        var p = new Pen(ci, c, col, t);
        if (!Glyph(p, card.Id)) Category(p, card.Category);
    }

    /// <summary>Draws the card's own glyph; false for an id that has none (custom cards).</summary>
    private static bool Glyph(Pen p, int id)
    {
        float t = p.T;
        var dim = p.Ink with { A = 0.5f };
        switch (id)
        {
            case 1: // Bouncy: a bullet bouncing up off the floor
                p.Seg(new(-22, 14), new(22, 14), 2.5f);
                p.Dashed(new Vector2[] { new(-20, -12), new(-5, 12), new(7, -3) }, 2f, dim);
                p.Bullet(new(12, -8), -0.95f, 16, 5);
                break;
            case 2: // Big Bullet
                p.Speed(-28, -19, new[] { -6f, 0, 6 });
                p.Bullet(new(3, 0), 0, 36, 11);
                break;
            case 3: // Pierce: through a plank and out the other side
                p.Dashed(new Vector2[] { new(-24, 0), new(-6, 0) }, 2f, dim);
                p.Fill(Shapes.RoundRect(new Rect2(-4, -16, 7, 32), 2));
                p.Bullet(new(14, 0), 0, 18, 5);
                p.Dot(new(8, -10), 1.6f, p.Hi, false);
                p.Dot(new(10, 9), 1.3f, p.Hi, false);
                break;
            case 4: // Splitter: one bullet, a pop, three small ones
                p.Bullet(new(-14, 0), 0, 16, 5);
                p.Fill(Shapes.Star(new(2, 0), 7, 3.5f, 6, t));
                foreach (float a in new[] { -0.55f, 0f, 0.55f })
                    p.Bullet(new Vector2(2, 0) + Vector2.FromAngle(a) * 16, a, 9, 3);
                break;
            case 5: // Homing: a curving path into a target
            {
                var path = Quad(new(-24, -10), new(0, 18), new(12, 2));
                p.Dashed(path, 2f, dim);
                p.Ring(new(20, -7), 6, 2.5f);
                p.Dot(new(20, -7), 2, p.Ink, false);
                p.Bullet(new(12, 2), (new Vector2(12, 2) - new Vector2(0, 18)).Angle(), 13, 4.5f);
                break;
            }
            case 6: // Explosive
                p.Fill(Shapes.Star(Vector2.Zero, 18, 9.5f, 10, t * 0.5f));
                p.Fill(Shapes.Star(Vector2.Zero, 9, 5, 8, -t * 0.7f), p.Hi, false);
                break;
            case 7: // Tank: a big body with a heart
                p.Body(new(-3, 2), 14);
                p.Heart(new(15, -9), 8);
                break;
            case 8: // Feather
            {
                var vane = Rot(Shapes.Ellipse(Vector2.Zero, 6.5f, 17, 20), 0.6f);
                p.Fill(vane);
                p.Seg(Vector2.FromAngle(0.6f + Mathf.Pi / 2) * 21, Vector2.FromAngle(0.6f - Mathf.Pi / 2) * 15, 1.6f, p.Dark, false);
                foreach (float k in new[] { -8f, -2f, 4f })
                {
                    var on = Vector2.FromAngle(0.6f - Mathf.Pi / 2) * k;
                    p.Seg(on, on + Vector2.FromAngle(0.6f - 0.6f) * 5, 1.1f, p.Dark, false);
                }
                break;
            }
            case 9: // Speedy: a small body leaving speed lines behind
                p.Speed(-24, -8, new[] { -4f, 2, 8 });
                p.Body(new(6, 2), 9);
                break;
            case 10: // Shotgun: four pellets from one muzzle
                p.Fill(Shapes.Star(new(-20, 0), 6, 2.5f, 5, 0.3f), p.Hi, false);
                foreach (float a in new[] { -0.45f, -0.15f, 0.15f, 0.45f })
                {
                    var d = Vector2.FromAngle(a);
                    p.Seg(new Vector2(-20, 0) + d * 10, new Vector2(-20, 0) + d * 21, 2, dim, false);
                    p.Dot(new Vector2(-20, 0) + d * 26, 3.6f);
                }
                break;
            case 11: // Quick Reload: a bullet inside a turning arrow
                p.ArcArrow(Vector2.Zero, 15, t * 3, t * 3 + 4.6f, 3.2f);
                p.Bullet(Vector2.Zero, -Mathf.Pi / 2, 14, 4);
                break;
            case 12: // Burst: three in a row
                for (int i = 0; i < 3; i++) p.Bullet(new(-14 + i * 14, 0), 0, 11, 4, p.Ink with { A = 0.55f + 0.2f * i });
                break;
            case 13: // Big Mag
                p.Mag(new(-4, 0), 16, 32, 6);
                p.Badge(new(14, -11), '+');
                break;
            case 14: // Vampire: fangs and a drop of blood
                p.Fill(Shapes.RoundRect(new Rect2(-16, -16, 32, 7), 3));
                p.Fill(new Vector2[] { new(-12, -10), new(-2, -10), new(-7, 8) });
                p.Fill(new Vector2[] { new(2, -10), new(12, -10), new(7, 8) });
                p.Drop(new(-7, 14), 4, Blood);
                break;
            case 15: // Blink: a faded you, then you somewhere else
                p.Body(new(-13, 2), 9, p.Ink with { A = 0.3f }, false);
                p.Dashed(new Vector2[] { new(-5, 2), new(4, 2) }, 2, dim);
                p.Body(new(12, 2), 9);
                p.Fill(Shapes.Star(new(21, -12), 5.5f, 1.6f, 4, t), p.Hi, false);
                break;
            case 16: // Glass Cannon: a cannon with a crack down it
                p.Fill(Shapes.Capsule(new(-14, 6), new(14, -7), 6.5f));
                p.Dot(new(-6, 10), 6.5f);
                p.Dot(new(-6, 10), 2, p.Dark, false);
                p.Line(new Vector2[] { new(2, -9), new(5, -3), new(1, 0), new(4, 4) }, 1.4f, p.Dark, false);
                break;
            case 17: // Spring Legs
            {
                float sq = 1 + 0.12f * Mathf.Sin(t * 4);
                p.Fill(Shapes.RoundRect(new Rect2(-12, 12, 24, 4), 2));
                p.Spring(new(0, 12), new(0, 12 - 22 * sq), 14, 5);
                p.Fill(Shapes.RoundRect(new Rect2(-12, 12 - 22 * sq - 4, 24, 4), 2));
                break;
            }
            case 18: // Double Jump: two chevrons up
                p.Chevron(new(0, 9), 11, -Mathf.Pi / 2, p.Ink with { A = 0.6f });
                p.Chevron(new(0, -5), 11, -Mathf.Pi / 2);
                break;
            case 19: // Cloud Walker: steps on a cloud
                p.Cloud(new(0, 8), 1);
                p.Chevron(new(-7, -11), 6, -Mathf.Pi / 2);
                p.Chevron(new(7, -11), 6, -Mathf.Pi / 2);
                break;
            case 20: // Sprinter: a running boot
                p.Speed(-26, -12, new[] { -6f, 0, 6 });
                p.Boot(new(2, 0), 15);
                break;
            case 21: // Afterburner: exhaust behind, chevrons ahead
                p.Flame(new(-14, 0), 9, -Mathf.Pi / 2);
                p.Chevron(new(2, 0), 10, 0, p.Ink with { A = 0.6f });
                p.Chevron(new(13, 0), 10, 0);
                break;
            case 22: // Rocket Boots
                p.Flame(new(-1, 13), 7, Mathf.Pi);
                p.Boot(new(-1, -5), 12);
                break;
            case 23: // Traction: a cleated sole
                for (int i = 0; i < 5; i++)
                    p.Fill(new Vector2[] { new(-15 + i * 7.5f, 6), new(-11 + i * 7.5f, 6), new(-13 + i * 7.5f, 12) });
                p.Fill(Shapes.RoundRect(new Rect2(-19, -6, 38, 12), 5));
                for (int i = 0; i < 4; i++) p.Seg(new(-11 + i * 7.5f, -3), new(-13 + i * 7.5f, 3), 1.8f, p.Dark, false);
                break;
            case 24: // Ice Skates
                p.Boot(new(-1, -6), 12);
                p.Seg(new(-4, 5), new(-4, 10), 2.2f);
                p.Seg(new(8, 5), new(8, 10), 2.2f);
                p.Line(new Vector2[] { new(-12, 11), new(14, 11), new(17, 9), new(17, 6) }, 2.6f);
                break;
            case 25: // Glider: a hang glider
                p.Fill(new Vector2[] { new(-23, 5), new(0, -13), new(23, 5), new(0, 1) });
                p.Seg(new(0, -10), new(0, 1), 1.5f, p.Dark, false);
                p.Seg(new(0, 1), new(0, 9), 1.5f);
                p.Dot(new(0, 13), 4);
                break;
            case 26: // Wall Runner: kicking up off a wall
                p.Fill(Shapes.RoundRect(new Rect2(-22, -17, 7, 34), 2));
                p.Chevron(new(-1, 7), 8, -Mathf.Pi / 4, p.Ink with { A = 0.6f });
                p.Chevron(new(11, -5), 8, -Mathf.Pi / 4);
                break;
            case 27: // Parkour: an arc over a box
                p.Fill(Shapes.RoundRect(new Rect2(-7, 3, 14, 13), 2));
                p.Dashed(Quad(new(-22, 14), new(0, -26), new(17, 10)), 2.2f, p.Ink);
                p.Head(new(17, 10), (new Vector2(17, 10) - new Vector2(0, -26)).Normalized(), 6);
                break;
            case 28: // Dash: a block that throws you forward
                p.Shield(new(-15, 0), 8);
                p.Arrow(new(-5, 0), new(22, 0), 5, 9);
                break;
            case 29: // Jetpack
                foreach (float x in new[] { -7f, 7f }) p.Flame(new(x, 12), 6 + Mathf.Sin(t * 13 + x) * 0.8f, Mathf.Pi);
                p.Fill(Shapes.Capsule(new(-7, -12), new(-7, 4), 5.5f));
                p.Fill(Shapes.Capsule(new(7, -12), new(7, 4), 5.5f));
                p.Seg(new(-12, -4), new(12, -4), 1.6f, p.Dark, false);
                break;
            case 30: // Sniper: a scope reticle
                p.Ring(Vector2.Zero, 14, 2.6f);
                for (int i = 0; i < 4; i++)
                {
                    var d = Vector2.FromAngle(i * Mathf.Pi / 2);
                    p.Seg(d * 6, d * 20, 2.6f);
                }
                p.Dot(Vector2.Zero, 2, p.Hi, false);
                break;
            case 31: // Minigun: three barrels and a spray
                foreach (float y in new[] { -8f, -2.5f, 3f }) p.Fill(Shapes.RoundRect(new Rect2(-22, y, 26, 5), 2));
                p.Fill(Shapes.RoundRect(new Rect2(3, -11, 5, 21), 2));
                foreach (var d in new Vector2[] { new(14, -6), new(19, 2), new(23, -4), new(16, 8) }) p.Dot(d, 2);
                break;
            case 32: // Twin Shot
                foreach (float y in new[] { -7f, 7f })
                {
                    p.Speed(-24, -14, new[] { y });
                    p.Bullet(new(3, y), 0, 20, 5);
                }
                break;
            case 33: // Hail Storm: a wide fan of small shots
                for (int i = 0; i < 7; i++)
                {
                    var d = Vector2.FromAngle(-0.9f + i * 0.3f);
                    p.Dot(new Vector2(-18, 0) + d * 26, 2.8f);
                    if (i < 6) p.Dot(new Vector2(-18, 0) + Vector2.FromAngle(-0.75f + i * 0.3f) * 15, 1.8f, dim, false);
                }
                break;
            case 34: // Rapid Fire: a stopwatch with a racing hand
                p.Fill(Shapes.RoundRect(new Rect2(-3, -17, 6, 5), 1.5f));
                p.Dot(new(0, 2), 14);
                for (int i = 0; i < 4; i++) p.Seg(new Vector2(0, 2) + Vector2.FromAngle(i * Mathf.Pi / 2) * 10, new Vector2(0, 2) + Vector2.FromAngle(i * Mathf.Pi / 2) * 12, 1.6f, p.Dark, false);
                p.Seg(new(0, 2), new Vector2(0, 2) + Vector2.FromAngle(t * 8) * 9, 2.2f, p.Dark, false);
                p.Dot(new(0, 2), 2, p.Dark, false);
                break;
            case 35: // High Velocity: a needle-nosed round and long streaks
                p.Speed(-27, -4, new[] { -8f, -3, 3, 8 });
                p.Bullet(new(10, 0), 0, 24, 4);
                break;
            case 36: // Heavy Slugs: a squat flat-nosed slug, dropping
                p.Dashed(Quad(new(14, 6), new(20, 8), new(22, 16)), 2, dim);
                p.Fill(Shapes.RoundRect(new Rect2(-16, -9, 30, 18), 4));
                p.Seg(new(-8, -8), new(-8, 8), 2, p.Dark, false);
                p.Seg(new(-4, -8), new(-4, 8), 2, p.Dark, false);
                break;
            case 37: // Cannonball
                p.Dot(new(-2, 1), 14);
                p.Dot(new(-7, -4), 4, p.Hi, false);
                foreach (float y in new[] { -8f, 1, 10 }) p.Seg(new(16, y), new(24, y * 1.2f), 2.4f);
                break;
            case 38: // Needles
                for (int i = 0; i < 3; i++)
                {
                    float x = -4 + (i % 2) * 6, y = -9 + i * 9;
                    p.Fill(new Vector2[] { new(x - 16, y - 1.4f), new(x + 12, y - 1.4f), new(x + 18, y), new(x + 12, y + 1.4f), new(x - 16, y + 1.4f) });
                }
                break;
            case 39: // Ricochet: bouncing between two walls
                p.Seg(new(-22, -16), new(22, -16), 2.5f);
                p.Seg(new(-22, 16), new(22, 16), 2.5f);
                p.Dashed(new Vector2[] { new(-21, 13), new(-11, -14), new(-1, 14), new(9, -14) }, 2f, dim);
                p.Bullet(new(15, 2), (new Vector2(7, 17)).Angle(), 13, 4.5f);
                break;
            case 40: // Marksman: a bullseye
                p.Dot(Vector2.Zero, 16);
                p.Dot(Vector2.Zero, 11, p.Col, false);
                p.Dot(Vector2.Zero, 6.5f, p.Ink, false);
                p.Dot(Vector2.Zero, 2.6f, p.Dark, false);
                break;
            case 41: // Impact Rounds: a hit that shoves
                p.Bullet(new(-12, 0), 0, 16, 5);
                p.Fill(Shapes.Star(new(4, 0), 8, 3.5f, 7, 0.2f), p.Hi, false);
                p.Arrow(new(11, -7), new(23, -12), 2.5f, 5);
                p.Arrow(new(11, 7), new(23, 12), 2.5f, 5);
                break;
            case 42: // Drum Mag
                p.Dot(Vector2.Zero, 16);
                for (int i = 0; i < 8; i++) p.Dot(Vector2.FromAngle(t + i * Mathf.Tau / 8) * 10, 2.4f, p.Dark, false);
                p.Dot(Vector2.Zero, 3.5f, p.Dark, false);
                break;
            case 43: // Giant: you, towering over a normal player
                p.Body(new(-5, 2), 15);
                p.Body(new(17, 10), 4.5f, p.Ink with { A = 0.6f }, false);
                break;
            case 44: // Tiny: you, next to the outline of a normal player
                p.Body(new(8, 2), 13, p.Ink with { A = 0.45f }, false, true);
                p.Body(new(-11, 8), 6);
                break;
            case 45: // Armor Plating: a breastplate
                p.Fill(new Vector2[] { new(-14, -14), new(-5, -10), new(5, -10), new(14, -14), new(17, -3), new(10, 15), new(-10, 15), new(-17, -3) });
                p.Seg(new(0, -9), new(0, 13), 1.5f, p.Dark, false);
                foreach (var r in new Vector2[] { new(-10, -7), new(10, -7), new(-7, 10), new(7, 10) }) p.Dot(r, 1.6f, p.Dark, false);
                break;
            case 46: // Regeneration: a heart with a plus, more rising
                p.Heart(new(-3, -1), 13);
                p.Plus(new(-3, 1), 4.5f, 2.6f, p.Dark);
                for (int i = 0; i < 2; i++)
                {
                    float u = (t * 0.6f + i * 0.5f) % 1f;
                    p.Plus(new(16 - i * 3, 8 - u * 24), 2.8f, 1.8f, p.Ink with { A = 1 - u });
                }
                break;
            case 47: // Anchor
                p.Ring(new(0, -13), 3.5f, 2.5f);
                p.Seg(new(0, -9), new(0, 14), 3.5f);
                p.Seg(new(-8, -4), new(8, -4), 3);
                p.Arc(new(0, 1), 14, 0.2f, Mathf.Pi - 0.2f, 3.5f);
                p.Fill(new Vector2[] { new(-17, 0), new(-10, 4), new(-15, 9) });
                p.Fill(new Vector2[] { new(17, 0), new(15, 9), new(10, 4) });
                break;
            case 48: // Rubber Body: a ball squashing on the floor
            {
                float ry = 8 + 2.5f * Mathf.Sin(t * 5), rx = 16 - 2.5f * Mathf.Sin(t * 5);
                p.Seg(new(-22, 16), new(22, 16), 2.5f);
                p.Fill(Shapes.Ellipse(new(0, 15 - ry), rx, ry, 24));
                p.Seg(new(-8, -6), new(-11, -13), 2, dim, false);
                p.Seg(new(0, -8), new(0, -16), 2, dim, false);
                p.Seg(new(8, -6), new(11, -13), 2, dim, false);
                break;
            }
            case 49: // Juggernaut: a heavy helmet
            {
                var dome = new List<Vector2>();
                for (int i = 0; i <= 16; i++) dome.Add(Vector2.FromAngle(Mathf.Pi + i * Mathf.Pi / 16) * 16 + new Vector2(0, 4));
                dome.Add(new(16, 15));
                dome.Add(new(-16, 15));
                p.Fill(dome.ToArray());
                p.Fill(Shapes.RoundRect(new Rect2(-11, 3, 22, 4), 1.5f), p.Dark, false);
                p.Seg(new(0, -11), new(0, 2), 2, p.Dark, false);
                break;
            }
            case 50: // Second Wind: a heart in a turning arrow
                p.ArcArrow(Vector2.Zero, 18, -0.6f + t, -0.6f + t + 4.8f, 2.8f);
                p.Heart(new(0, -1), 10);
                break;
            case 51: // Hearty
                p.Heart(new(0, -2), 17);
                p.Dot(new(-7, -6), 2.6f, p.Hi, false);
                break;
            case 52: // Shield Bearer: shields doubled up
                p.Shield(new(-7, -2), 11, p.Ink with { A = 0.5f }, false);
                p.Shield(new(6, 2), 12);
                break;
            case 53: // Thorns: a spiked ball
                p.Fill(Shapes.Star(Vector2.Zero, 17, 10, 12, t * 0.4f));
                p.Dot(Vector2.Zero, 6, p.Col, false);
                break;
            case 54: // Adrenaline: a heart with a racing pulse
                p.Heart(new(0, -1), 15);
                p.Line(new Vector2[] { new(-25, 3), new(-11, 3), new(-6, -8), new(-1, 13), new(4, -3), new(8, 3), new(25, 3) }, 2.4f, p.Dark, false);
                break;
            case 55: // Bullet Hell: a ring of shots
                for (int i = 0; i < 12; i++) p.Dot(Vector2.FromAngle(t * 0.8f + i * Mathf.Tau / 12) * 15, 2.9f);
                p.Dot(Vector2.Zero, 4, p.Hi, false);
                break;
            case 56: // Upside Down: a shot that falls up
                p.Dashed(Quad(new(-20, 14), new(6, 12), new(8, -6)), 2, dim);
                p.Bullet(new(8, -7), -Mathf.Pi / 2, 14, 4.5f);
                p.Chevron(new(19, 4), 5, -Mathf.Pi / 2);
                p.Chevron(new(19, 12), 5, -Mathf.Pi / 2, p.Ink with { A = 0.6f });
                break;
            case 57: // Boomerang
            {
                var arm = Rot(new Vector2[] { new(-16, -10), new(0, 9), new(16, -10) }, t * 3);
                p.Line(arm, 7);
                p.Dot(arm[0], 3.5f, p.Ink, false);
                p.Dot(arm[2], 3.5f, p.Ink, false);
                p.Dot(arm[1], 3.5f, p.Ink, false);
                break;
            }
            case 58: // Ghost Rounds: a ghost
            {
                float bob = Mathf.Sin(t * 2.5f) * 1.5f;
                var g = new List<Vector2>();
                for (int i = 0; i <= 14; i++) g.Add(Vector2.FromAngle(Mathf.Pi + i * Mathf.Pi / 14) * 13 + new Vector2(0, -2 + bob));
                for (int i = 0; i <= 8; i++) g.Add(new(13 - i * 26 / 8f, 13 + bob + (i % 2 == 0 ? 0 : -4)));
                p.Fill(g.ToArray());
                p.Fill(Shapes.Ellipse(new(-5, -3 + bob), 2.2f, 3.2f, 12), p.Dark, false);
                p.Fill(Shapes.Ellipse(new(5, -3 + bob), 2.2f, 3.2f, 12), p.Dark, false);
                break;
            }
            case 59: // Kamikaze: a bomb with a lit fuse
                p.Line(Quad(new(8, -9), new(12, -18), new(17, -14)), 2);
                p.Fill(Shapes.RoundRect(new Rect2(3, -13, 9, 6), 1.5f));
                p.Dot(new(-3, 3), 13);
                p.Dot(new(-8, -2), 3, p.Hi, false);
                p.Fill(Shapes.Star(new(18, -15), 5 + Mathf.Sin(t * 14), 2, 6, t * 5), p.Hi, false);
                break;
            case 60: // Recoil Jet: the shot goes one way, you the other
                p.Flame(new(-17, -4), 7, -Mathf.Pi / 2);
                p.Fill(Shapes.RoundRect(new Rect2(-10, -8, 16, 8), 2));
                p.Bullet(new(16, -4), 0, 12, 3.5f);
                p.Arrow(new(10, 11), new(-18, 11), 3, 6);
                break;
            case 61: // Warp Rounds: two portals and a jump between
                p.Ellipse(new(-15, 3), 5, 12, 2.8f);
                p.Ellipse(new(15, 3), 5, 12, 2.8f);
                p.Dashed(Quad(new(-12, -8), new(0, -26), new(12, -8)), 2, p.Ink);
                p.Head(new(12, -8), (new Vector2(12, -8) - new Vector2(0, -26)).Normalized(), 5);
                break;
            case 62: // Sticky Mines: a mine stuck to the floor, blinking
            {
                p.Seg(new(-22, 14), new(22, 14), 3);
                for (int i = 0; i < 5; i++)
                {
                    var d = Vector2.FromAngle(Mathf.Pi + (i + 0.5f) * Mathf.Pi / 5);
                    p.Seg(new Vector2(0, 13) + d * 10, new Vector2(0, 13) + d * 16, 2.4f);
                }
                var dome = new List<Vector2>();
                for (int i = 0; i <= 12; i++) dome.Add(Vector2.FromAngle(Mathf.Pi + i * Mathf.Pi / 12) * 11 + new Vector2(0, 13));
                p.Fill(dome.ToArray());
                p.Dot(new(0, 6), 2.6f, Blood with { A = 0.4f + 0.6f * (Mathf.Sin(t * 6) > 0 ? 1 : 0) }, false);
                break;
            }
            case 63: // Chain Lightning: arcs hopping between three
            {
                Vector2[] at = { new(-18, 10), new(0, -10), new(18, 8) };
                int seed = (int)(t * 10);
                for (int i = 0; i < 2; i++) p.Line(Zigzag(at[i], at[i + 1], 4, 4, seed + i), 2.4f, p.Hi);
                foreach (var a in at) p.Dot(a, 4.5f);
                break;
            }
            case 64: // Berserk: an angry face
                p.Dot(Vector2.Zero, 15);
                p.Seg(new(-10, -8), new(-2, -4), 2.6f, p.Dark, false);
                p.Seg(new(2, -4), new(10, -8), 2.6f, p.Dark, false);
                p.Dot(new(-5, -1), 2, p.Dark, false);
                p.Dot(new(5, -1), 2, p.Dark, false);
                p.Fill(Shapes.RoundRect(new Rect2(-7, 5, 14, 5), 1.5f), p.Dark, false);
                for (int i = -1; i <= 1; i++) p.Seg(new(i * 3.5f, 5.5f), new(i * 3.5f, 9.5f), 1, p.Ink, false);
                break;
            case 65: // One Shot: one big round standing alone
                p.Ring(Vector2.Zero, 18, 2, dim);
                p.Bullet(new(0, 1), -Mathf.Pi / 2, 30, 7.5f);
                break;
            case 66: // Wild Rounds: three shots, nothing alike
                p.Bullet(new(-13, -7), -0.6f, 10, 3);
                p.Bullet(new(4, 8), 0.4f, 22, 7);
                p.Bullet(new(15, -9), -1.2f, 14, 4.5f);
                break;
            case 67: // Pinball: a ball rattling round a box
                p.Closed(Shapes.RoundRect(new Rect2(-21, -16, 42, 32), 5), 2.5f);
                p.Dashed(new Vector2[] { new(-16, 11), new(-5, -12), new(7, 12), new(13, 0) }, 2, dim);
                p.Dot(new(13, 0), 4.5f);
                break;
            case 68: // Mind Control: a hypnotic spiral
            {
                var sp = new List<Vector2>();
                for (int i = 0; i <= 60; i++)
                {
                    float a = i / 60f * 5 * Mathf.Pi;
                    sp.Add(Vector2.FromAngle(a - t * 2) * (1 + a * 1.0f));
                }
                p.Line(sp.ToArray(), 2.6f);
                break;
            }
            case 69: // Leech Field: blood drawn in from all round
                p.DashedEllipse(Vector2.Zero, 17, 17, 2, dim, t);
                p.Dot(Vector2.Zero, 6);
                for (int i = 0; i < 6; i++)
                {
                    float u = (t * 0.8f + i * 0.37f) % 1f;
                    p.Dot(Vector2.FromAngle(i * Mathf.Tau / 6 + 0.3f) * (16 - u * 8), 2, Blood with { A = 1 - u * 0.5f }, false);
                }
                break;
            case 70: // Reflector: a shot coming in and going back out
                p.Dashed(new Vector2[] { new(-23, -14), new(-6, -3) }, 2.2f, p.Ink);
                p.Arrow(new(-6, 3), new(-22, 13), 2.6f, 5);
                p.Shield(new(7, 0), 12);
                break;
            case 71: // Shield Bash
                p.Shield(new(-6, 0), 12);
                p.Seg(new(10, -9), new(19, -14), 2.6f);
                p.Seg(new(11, 0), new(22, 0), 2.6f);
                p.Seg(new(10, 9), new(19, 14), 2.6f);
                break;
            case 72: // Bulwark: a tower shield
                p.Fill(Shapes.RoundRect(new Rect2(-11, -17, 22, 34), 4));
                p.Closed(Shapes.RoundRect(new Rect2(-7.5f, -13.5f, 15, 27), 3), 1.5f, p.Dark);
                foreach (var r in new Vector2[] { new(-4, -10), new(4, -10), new(-4, 10), new(4, 10) }) p.Dot(r, 1.3f, p.Dark, false);
                break;
            case 73: // Quick Guard
                p.Speed(-26, -12, new[] { -6f, 0, 6 });
                p.Shield(new(4, 0), 11);
                break;
            case 74: // Riposte: a sword
            {
                float a = -Mathf.Pi / 4;
                var blade = Rot(new Vector2[] { new(-4, -2.4f), new(19, -2.4f), new(24, 0), new(19, 2.4f), new(-4, 2.4f) }, a);
                p.Fill(Shift(blade, new Vector2(-3, 3)));
                var guard = new Vector2(-3, 3) + Vector2.FromAngle(a) * -4;
                p.Seg(guard + Vector2.FromAngle(a + Mathf.Pi / 2) * 8, guard - Vector2.FromAngle(a + Mathf.Pi / 2) * 8, 3.5f);
                p.Seg(guard, guard - Vector2.FromAngle(a) * 9, 3.2f);
                p.Dot(guard - Vector2.FromAngle(a) * 11, 3);
                p.Seg(new Vector2(-3, 3) + Vector2.FromAngle(a) * 2, new Vector2(-3, 3) + Vector2.FromAngle(a) * 17, 1, p.Dark with { A = 0.6f }, false);
                break;
            }
            case 75: // Absorb: a shield with a heart in it
                p.Shield(Vector2.Zero, 14);
                p.Heart(new(0, -1), 6, Blood, false);
                break;
            case 76: // Reactive Reload: a block that refills the magazine
                p.Shield(new(-9, 1), 11);
                p.Mag(new(13, 1), 10, 24, 4);
                break;
            case 77: // Momentum: a clock wound back
                p.ArcArrow(Vector2.Zero, 18, 3.6f - t, -0.8f - t, 2.6f);
                p.Dot(Vector2.Zero, 12);
                p.Seg(Vector2.Zero, new(0, -8), 2, p.Dark, false);
                p.Seg(Vector2.Zero, new(5, 3), 2, p.Dark, false);
                break;
            case 78: // Shockwave: rings spreading out
                for (int i = 0; i < 3; i++)
                {
                    float r = (t * 18 + i * 7) % 21 + 3;
                    p.Ring(Vector2.Zero, r, 2.6f, p.Ink with { A = 1 - (r - 3) / 21 }, false);
                }
                p.Dot(Vector2.Zero, 4);
                break;
            case 79: // Iron Will: an anvil
                p.Fill(new Vector2[] { new(-22, -9), new(14, -9), new(14, -3), new(6, 0), new(6, 7), new(12, 13), new(-10, 13), new(-4, 7), new(-4, 0), new(-14, -3) });
                p.Seg(new(-17, -7), new(12, -7), 1.2f, p.Hi, false);
                break;
            case 80: // All In: a stack of chips
                for (int i = 0; i < 4; i++)
                {
                    var at = new Vector2(i == 3 ? 1 : 0, 11 - i * 6);
                    p.Fill(Shapes.Ellipse(at + new Vector2(0, 2), 14, 5, 22));
                    p.Fill(Shapes.Ellipse(at, 14, 5, 22), p.Ink, false);
                    p.Ellipse(at, 9, 3, 1.6f, p.Dark, false);
                }
                break;
            case 81: // Spiked Shield
                p.Fill(Shapes.Star(new(0, 1), 20, 13, 10, 0.31f));
                p.Shield(Vector2.Zero, 12, p.Ink, false);
                p.Closed(ShieldPts(Vector2.Zero, 12), 1.6f, p.Dark);
                break;
            case 82: // Arsenal: a fan of three, and more
                foreach (float a in new[] { -0.4f, 0f, 0.4f }) p.Bullet(new Vector2(-14, 2) + Vector2.FromAngle(a) * 20, a, 14, 4.5f);
                p.Badge(new(-14, -11), '+');
                break;
            case 83: // Seeker Warheads: a finned missile on a curving path
            {
                var at = new Vector2(8, -5);
                const float a = -0.45f;
                p.Dashed(Quad(new(-24, 14), new(-12, 2), at - Vector2.FromAngle(a) * 12), 2, dim);
                var back = at - Vector2.FromAngle(a) * 9;
                var n = Vector2.FromAngle(a + Mathf.Pi / 2);
                p.Fill(new[] { back + n * 3, back + n * 8 - Vector2.FromAngle(a) * 4, back - Vector2.FromAngle(a) * 2 });
                p.Fill(new[] { back - n * 3, back - n * 8 - Vector2.FromAngle(a) * 4, back - Vector2.FromAngle(a) * 2 });
                p.Bullet(at, a, 22, 5.5f);
                p.Dot(at + Vector2.FromAngle(a) * 7, 2, Blood, false);
                break;
            }
            case 84: // Hydra: one shot branching into four
                p.Bullet(new(-17, 0), 0, 12, 4);
                foreach (var end in new Vector2[] { new(16, -15), new(21, -5), new(21, 5), new(16, 15) })
                {
                    p.Line(Quad(new(-8, 0), new(4, end.Y * 0.2f), end), 2.4f);
                    p.Dot(end, 3.5f);
                }
                break;
            case 85: // Overclock: a spinning gear with a bolt
            {
                var gear = new List<Vector2>();
                for (int i = 0; i < 40; i++) gear.Add(Vector2.FromAngle(t * 1.5f + i * Mathf.Tau / 40) * (i % 5 < 2 ? 17 : 12.5f));
                p.Fill(gear.ToArray());
                p.Dot(Vector2.Zero, 7.5f, p.Col, false);
                p.Bolt(Vector2.Zero, 7, p.Ink, false);
                break;
            }
            case 86: // Titan: a huge armored body
                p.Body(new(0, 3), 15);
                p.Fill(Shapes.RoundRect(new Rect2(-11, -2, 8, 5), 2), p.Dark, false);
                p.Fill(Shapes.RoundRect(new Rect2(3, -2, 8, 5), 2), p.Dark, false);
                p.Fill(Shapes.RoundRect(new Rect2(-5, -10, 10, 2.6f), 1), p.Dark, false);
                break;
            case 87: // Phoenix: a flame with wings
            {
                float flap = Mathf.Sin(t * 3) * 0.12f;
                p.Wing(new(3, 2), 20, 1, flap);
                p.Wing(new(-3, 2), 20, -1, flap);
                p.Flame(new(0, 4), 10, 0, p.Hi);
                break;
            }
            case 88: // Skywalker: a winged boot
                p.Boot(new(2, 1), 14);
                p.Wing(new(-3, -4), 15, -1, Mathf.Sin(t * 3) * 0.1f);
                break;
            case 89: // Storm Caller: a storm cloud and its bolt
                p.Bolt(new(2, 9), 10, p.Hi);
                p.Cloud(new(0, -6), 1.05f);
                break;
            case 90: // Vampire Lord: a bat
            {
                float f = Mathf.Sin(t * 4) * 2;
                p.Fill(new Vector2[]
                {
                    new(0, -5), new(2, -6), new(4, -12), new(6, -6), new(13, -10 - f), new(24, -12 - f * 1.5f), new(21, -4 - f), new(18, 1),
                    new(14, -2), new(10, 4), new(6, 2), new(3, 8), new(0, 10), new(-3, 8), new(-6, 2), new(-10, 4), new(-14, -2), new(-18, 1),
                    new(-21, -4 - f), new(-24, -12 - f * 1.5f), new(-13, -10 - f), new(-6, -6), new(-4, -12), new(-2, -6),
                });
                p.Dot(new(-2, -2), 1.3f, Blood, false);
                p.Dot(new(2, -2), 1.3f, Blood, false);
                break;
            }
            case 91: // Mirror Guard: a mirror sending shots back
                p.Dashed(new Vector2[] { new(-24, -13), new(-11, -3) }, 2.2f, p.Ink);
                p.Arrow(new(-11, 3), new(-24, 13), 2.6f, 5);
                p.Fill(new Vector2[] { new(2, -17), new(14, 0), new(2, 17), new(-10, 0) });
                p.Seg(new(-2, -5), new(3, -11), 1.6f, p.Hi, false);
                p.Seg(new(0, 1), new(7, -7), 1.6f, p.Hi, false);
                break;

            // ---------- curses ----------
            case 92: // Brittle: a cracked heart, a piece falling off
                p.Heart(new(-3, -1), 14);
                p.Line(new Vector2[] { new(-3, -8), new(0, -2), new(-5, 3), new(-2, 10) }, 1.6f, p.Dark, false);
                p.Fill(new Vector2[] { new(14, 8), new(19, 10), new(15, 14) });
                break;
            case 93: // Lead Boots
                p.Boot(new(-6, -1), 14);
                p.Badge(new(14, -9), 'v');
                break;
            case 94: // Butterfingers: a magazine slipping away
                p.Mag(new(6, 3), 13, 24, 4, 0.5f);
                foreach (float x in new[] { -6f, 0, 6 }) p.Seg(new(x - 6, -16), new(x - 6, -9), 2, dim, false);
                break;
            case 95: // Rusty Trigger: a snail
                p.Fill(Shapes.Capsule(new(-18, 10), new(12, 10), 4));
                p.Seg(new(-16, 7), new(-19, -4), 1.8f);
                p.Dot(new(-19, -5), 2.2f);
                p.Dot(new(3, -1), 11);
                p.Line(Spiral(new(3, -1), 8, 2.2f), 1.8f, p.Dark, false);
                break;
            case 96: // Blunt Rounds: a flat-nosed round
                p.Fill(Shapes.RoundRect(new Rect2(-16, -7, 26, 14), 1.5f));
                p.Seg(new(-8, -6), new(-8, 6), 2, p.Dark, false);
                p.Badge(new(15, -9), 'v');
                break;
            case 97: // Greased Soles: a boot on a slick
                p.Fill(Shapes.Ellipse(new(0, 13), 20, 4, 24), p.Hi with { A = 0.85f }, false);
                p.Boot(new(-3, -4), 12);
                p.Seg(new(14, -6), new(22, -9), 2, dim, false);
                p.Seg(new(14, 0), new(23, -1), 2, dim, false);
                break;
            case 98: // Heavy Shield
                p.Shield(new(-4, 0), 12);
                p.Badge(new(14, -9), 'v');
                break;
            case 99: // Glass Jaw: a shattered pane
            {
                p.Fill(Shapes.RoundRect(new Rect2(-15, -15, 30, 30), 3));
                var o = new Vector2(3, -1);
                foreach (var e in new Vector2[] { new(-15, -6), new(-6, 15), new(15, 8), new(8, -15), new(-5, -15) })
                    p.Line(Zigzag(o, e, 3, 1.6f, (int)e.X), 1.3f, p.Dark, false);
                break;
            }
            case 100: // Big Target: a bullseye, growing
                p.Dot(Vector2.Zero, 12);
                p.Dot(Vector2.Zero, 8, p.Col, false);
                p.Dot(Vector2.Zero, 4, p.Ink, false);
                for (int i = 0; i < 4; i++)
                {
                    var d = Vector2.FromAngle(Mathf.Pi / 4 + i * Mathf.Pi / 2);
                    p.Arrow(d * 14, d * 22, 2.4f, 4.5f);
                }
                break;
            case 101: // Small Mag
                p.Mag(new(-4, 2), 14, 24, 2);
                p.Badge(new(13, -10), '-');
                break;
            case 102: // Short Fuse: a shot that fizzles out
                p.Bullet(new(-12, 0), 0, 14, 4.5f);
                for (int i = 0; i < 3; i++) p.Dot(new(2 + i * 5.5f, 0), 2 - i * 0.4f, p.Ink with { A = 0.8f - i * 0.25f }, false);
                foreach (var (at, r) in new[] { (new Vector2(21, -3), 3.5f), (new Vector2(19, 3), 2.5f), (new Vector2(24, 2), 2.2f) })
                    p.Dot(at, r, p.Ink with { A = 0.4f }, false);
                break;
            case 103: // Pushover: shoved off your feet
                p.Arrow(new(-25, -3), new(-9, -3), 4, 7);
                p.Body(new(6, 3), 10);
                p.Seg(new(19, -6), new(24, -8), 2, dim, false);
                p.Seg(new(19, 2), new(25, 2), 2, dim, false);
                break;
            default:
                return false;
        }
        return true;
    }

    /// <summary>The category's own glyph, for cards without one (custom cards).</summary>
    private static void Category(Pen p, CardCategory cat)
    {
        var ink = p.Ink;
        float t = p.T;
        switch (cat)
        {
            case CardCategory.Gun:
                for (int i = 3; i >= 1; i--) p.Dot(new(-i * 9, 0), 6 - i * 1.2f, ink with { A = 0.25f * (4 - i) }, false);
                p.Dot(Vector2.Zero, 10);
                p.Dot(new(-3, -3), 3.5f, p.Hi, false);
                break;
            case CardCategory.Body:
                p.Heart(new(0, -1), 12);
                break;
            case CardCategory.Movement:
            {
                // A bouncing double chevron with speed lines.
                float hop = Mathf.Abs(Mathf.Sin(t * 3)) * -4;
                p.Chevron(new(-4, hop), 12, 0, ink with { A = 0.6f });
                p.Chevron(new(7, hop), 12, 0);
                for (int i = 0; i < 3; i++)
                    p.Seg(new(-24, -7 + i * 7 + hop), new(-14 + (i % 2) * 3, -7 + i * 7 + hop), 2f, ink with { A = 0.5f }, false);
                break;
            }
            case CardCategory.Block:
            {
                // A shield with a gentle sway and a boss in the middle.
                float sway = Mathf.Sin(t * 2) * 0.08f;
                p.Fill(Rot(ShieldPts(Vector2.Zero, 13), sway));
                p.Seg(new Vector2(0, -11).Rotated(sway), new Vector2(0, 12).Rotated(sway), 2.5f, p.Col with { A = 0.7f }, false);
                p.Dot(Vector2.Zero, 4, p.Col, false);
                p.Dot(new(-1.2f, -1.2f), 1.4f, p.Col.Lightened(0.7f), false);
                break;
            }
            case CardCategory.Curse:
                p.Skull(Mathf.Sin(t * 1.6f) * 0.12f);
                break;
            default:
                p.Fill(Shapes.Star(Vector2.Zero, 17, 7, 5, t * 0.8f - Mathf.Pi / 2));
                break;
        }
    }

    // ---------- shape helpers (points relative to the icon's center) ----------

    private static Vector2[] Rot(Vector2[] pts, float a)
    {
        var o = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++) o[i] = pts[i].Rotated(a);
        return o;
    }

    private static Vector2[] Shift(Vector2[] pts, Vector2 by) => Shapes.Offset(pts, by);

    private static Vector2[] Quad(Vector2 a, Vector2 ctl, Vector2 b, int n = 14)
    {
        var o = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float u = i / (float)n;
            o[i] = (1 - u) * (1 - u) * a + 2 * (1 - u) * u * ctl + u * u * b;
        }
        return o;
    }

    /// <summary>A jagged line from a to b: <paramref name="kinks"/> corners pushed alternately to each side.</summary>
    private static Vector2[] Zigzag(Vector2 a, Vector2 b, int kinks, float amp, int seed)
    {
        var o = new Vector2[kinks + 2];
        var n = (b - a).Normalized().Orthogonal();
        for (int i = 0; i <= kinks + 1; i++)
        {
            float u = i / (float)(kinks + 1);
            float jitter = 0.6f + 0.4f * (((seed * 31 + i * 17) & 7) / 7f);
            o[i] = a.Lerp(b, u) + (i == 0 || i == kinks + 1 ? Vector2.Zero : n * amp * jitter * (i % 2 == 0 ? 1 : -1));
        }
        return o;
    }

    private static Vector2[] Spiral(Vector2 at, float r, float turns)
    {
        var o = new Vector2[40];
        for (int i = 0; i < o.Length; i++)
        {
            float u = i / (o.Length - 1f);
            o[i] = at + Vector2.FromAngle(u * turns * Mathf.Tau) * (r * (1 - u) + 0.5f);
        }
        return o;
    }

    /// <summary>A heater shield, <paramref name="s"/> half as wide as it is.</summary>
    private static Vector2[] ShieldPts(Vector2 at, float s)
    {
        float k = s / 13f;
        var pts = new List<Vector2>();
        for (int i = 0; i <= 12; i++)
        {
            float x = -13 + 26 * i / 12f;
            pts.Add(new Vector2(x, -13 + 2.5f * (1 - Mathf.Pow(x / 13, 2))));
        }
        for (int i = 1; i < 12; i++)
        {
            float u = i / 12f;
            pts.Add(new Vector2(13 * (1 - u) * Mathf.Cos(u * 0.9f), -2 + 17 * Mathf.Sin(u * Mathf.Pi / 2)));
        }
        pts.Add(new Vector2(0, 15));
        for (int i = 11; i >= 1; i--)
        {
            float u = i / 12f;
            pts.Add(new Vector2(-13 * (1 - u) * Mathf.Cos(u * 0.9f), -2 + 17 * Mathf.Sin(u * Mathf.Pi / 2)));
        }
        return pts.Select(q => at + q * k).ToArray();
    }

    /// <summary>
    /// Draws in the icon's own space: white ink with a dark drop shadow, a dark tone of the category color for details
    /// on the ink, and a light tone for glints.
    /// </summary>
    private sealed class Pen
    {
        private static readonly Vector2 Off = new(2, 2);
        public readonly CanvasItem Ci;
        public readonly Vector2 C;
        public readonly Color Col, Shadow, Dark, Hi;
        public readonly Color Ink = new(1, 1, 1, 0.95f);
        public readonly float T;

        public Pen(CanvasItem ci, Vector2 c, Color col, float t)
        {
            Ci = ci; C = c; Col = col; T = t;
            Shadow = col.Darkened(0.55f) with { A = 0.6f };
            Dark = col.Darkened(0.5f);
            Hi = col.Lightened(0.6f);
        }

        private Vector2[] Abs(Vector2[] pts) => Shapes.Offset(pts, C);

        public void Fill(Vector2[] pts, Color? color = null, bool shadow = true)
        {
            var a = Abs(pts);
            if (shadow) Ci.DrawColoredPolygon(Shapes.Offset(a, Off), Shadow);
            Ci.DrawColoredPolygon(a, color ?? Ink);
        }

        public void Line(Vector2[] pts, float w, Color? color = null, bool shadow = true)
        {
            var a = Abs(pts);
            if (shadow) Ci.DrawPolyline(Shapes.Offset(a, Off), Shadow, w, true);
            Ci.DrawPolyline(a, color ?? Ink, w, true);
        }

        public void Seg(Vector2 a, Vector2 b, float w, Color? color = null, bool shadow = true) => Line(new[] { a, b }, w, color, shadow);

        public void Closed(Vector2[] pts, float w, Color? color = null, bool shadow = true) => Line(pts.Append(pts[0]).ToArray(), w, color, shadow);

        public void Dot(Vector2 at, float r, Color? color = null, bool shadow = true)
        {
            if (shadow) Ci.DrawCircle(C + at + Off, r, Shadow, true, -1, true);
            Ci.DrawCircle(C + at, r, color ?? Ink, true, -1, true);
        }

        public void Arc(Vector2 at, float r, float from, float to, float w, Color? color = null, bool shadow = true)
        {
            int n = Mathf.Clamp((int)(Mathf.Abs(to - from) * r / 3) + 3, 4, 48);
            if (shadow) Ci.DrawArc(C + at + Off, r, from, to, n, Shadow, w, true);
            Ci.DrawArc(C + at, r, from, to, n, color ?? Ink, w, true);
        }

        public void Ring(Vector2 at, float r, float w, Color? color = null, bool shadow = true) => Arc(at, r, 0, Mathf.Tau, w, color, shadow);

        public void Ellipse(Vector2 at, float rx, float ry, float w, Color? color = null, bool shadow = true) => Closed(Shapes.Ellipse(at, rx, ry, 28), w, color, shadow);

        /// <summary>A dashed ellipse outline (no shadow), turned by <paramref name="spin"/>.</summary>
        public void DashedEllipse(Vector2 at, float rx, float ry, float w, Color color, float spin)
        {
            for (int i = 0; i < 12; i++)
            {
                float a0 = spin + i * Mathf.Tau / 12;
                Line(new[] { at + new Vector2(Mathf.Cos(a0) * rx, Mathf.Sin(a0) * ry), at + new Vector2(Mathf.Cos(a0 + 0.3f) * rx, Mathf.Sin(a0 + 0.3f) * ry) }, w, color, false);
            }
        }

        /// <summary>A dashed path (no shadow): the trail something left.</summary>
        public void Dashed(Vector2[] pts, float w, Color color, float dash = 4, float gap = 3)
        {
            float carry = 0;
            bool on = true;
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                var a = pts[i];
                var d = pts[i + 1] - a;
                float len = d.Length(), at = 0;
                if (len < 0.001f) continue;
                d /= len;
                while (at < len)
                {
                    float step = Mathf.Min((on ? dash : gap) - carry, len - at);
                    if (on) Ci.DrawLine(C + a + d * at, C + a + d * (at + step), color, w, true);
                    at += step;
                    carry += step;
                    if (carry >= (on ? dash : gap) - 0.001f) { carry = 0; on = !on; }
                }
            }
        }

        /// <summary>Horizontal speed lines from x0 to x1 at each y, fading toward the back.</summary>
        public void Speed(float x0, float x1, float[] ys)
        {
            for (int i = 0; i < ys.Length; i++)
            {
                float stagger = (i % 2) * 3;
                Ci.DrawLine(C + new Vector2(x0 + stagger, ys[i]), C + new Vector2(x1, ys[i]), Ink with { A = 0.5f }, 2f, true);
            }
        }

        public void Head(Vector2 tip, Vector2 dir, float size, Color? color = null)
        {
            var n = dir.Orthogonal();
            Fill(new[] { tip + dir * size * 0.6f, tip - dir * size * 0.6f + n * size * 0.7f, tip - dir * size * 0.6f - n * size * 0.7f }, color);
        }

        public void Arrow(Vector2 from, Vector2 to, float w, float head, Color? color = null)
        {
            var dir = (to - from).Normalized();
            Seg(from, to - dir * head * 0.5f, w, color);
            Head(to - dir * head * 0.3f, dir, head, color);
        }

        /// <summary>An arc with an arrowhead at <paramref name="to"/>, pointing the way it turns.</summary>
        public void ArcArrow(Vector2 at, float r, float from, float to, float w)
        {
            float dirSign = Mathf.Sign(to - from);
            float stop = to - dirSign * 0.3f;
            Arc(at, r, Mathf.Min(from, stop), Mathf.Max(from, stop), w);
            var pos = at + Vector2.FromAngle(stop) * r;
            var tan = Vector2.FromAngle(stop + dirSign * Mathf.Pi / 2);
            Head(pos + tan * 2, tan, 7);
        }

        /// <summary>A round with its nose toward <paramref name="angle"/>, a dark band round its casing.</summary>
        public void Bullet(Vector2 at, float angle, float len, float r, Color? color = null)
        {
            var pts = new List<Vector2> { new(-len / 2, r) , new(-len / 2, -r) };
            float nose = len / 2 - r;
            for (int i = 0; i <= 8; i++) pts.Add(new Vector2(nose, 0) + Vector2.FromAngle(-Mathf.Pi / 2 + i * Mathf.Pi / 8) * r);
            var shape = Shapes.Offset(Rot(pts.ToArray(), angle), at);
            Fill(shape, color, color == null);
            float band = -len / 2 + len * 0.3f;
            Seg(at + new Vector2(band, -r + 0.6f).Rotated(angle), at + new Vector2(band, r - 0.6f).Rotated(angle), Mathf.Max(1, r * 0.3f), Dark with { A = (color ?? Ink).A }, false);
        }

        /// <summary>A player: a round head on a rounded body, <paramref name="s"/> about half its height.</summary>
        public void Body(Vector2 at, float s, Color? color = null, bool shadow = true, bool outline = false)
        {
            var torso = Shapes.RoundRect(new Rect2(at.X - 0.6f * s, at.Y - 0.3f * s, 1.2f * s, 1.2f * s), 0.45f * s);
            var head = at + new Vector2(0, -0.78f * s);
            if (outline)
            {
                Closed(torso, 1.6f, color, false);
                Ring(head, 0.42f * s, 1.6f, color, false);
                return;
            }
            Fill(torso, color, shadow);
            Dot(head, 0.42f * s, color, shadow);
        }

        public void Heart(Vector2 at, float s, Color? color = null, bool shadow = true)
        {
            var pts = new Vector2[40];
            float k = s / 16;
            for (int i = 0; i < 40; i++)
            {
                float a = i / 40f * Mathf.Tau;
                pts[i] = at + new Vector2(16 * Mathf.Pow(Mathf.Sin(a), 3), -(13 * Mathf.Cos(a) - 5 * Mathf.Cos(2 * a) - 2 * Mathf.Cos(3 * a) - Mathf.Cos(4 * a))) * k;
            }
            Fill(pts, color, shadow);
        }

        public void Shield(Vector2 at, float s, Color? color = null, bool shadow = true) => Fill(ShieldPts(at, s), color, shadow);

        public void Boot(Vector2 at, float s)
        {
            Fill(new Vector2[] { new(-0.55f, -1), new(0.3f, -1), new(0.3f, 0.05f), new(0.8f, 0.2f), new(1.05f, 0.45f), new(1.05f, 0.95f), new(-0.55f, 0.95f) }.Select(q => at + q * s).ToArray());
            Seg(at + new Vector2(-0.55f, -0.72f) * s, at + new Vector2(0.3f, -0.72f) * s, Mathf.Max(1.2f, s * 0.12f), Dark, false);
            Seg(at + new Vector2(-0.55f, 0.66f) * s, at + new Vector2(1.05f, 0.66f) * s, Mathf.Max(1.2f, s * 0.12f), Dark, false);
        }

        public void Chevron(Vector2 at, float s, float angle, Color? color = null)
        {
            var pts = Rot(new Vector2[] { new(-0.5f * s, -s), new(0.5f * s, 0), new(-0.5f * s, s) }, angle);
            Line(Shapes.Offset(pts, at), 4.5f, color);
        }

        public void Flame(Vector2 at, float s, float angle, Color? color = null)
        {
            float w = Mathf.Sin(T * 9 + at.X) * 0.08f;
            var pts = new Vector2[] { new(w, -1.25f), new(0.3f, -0.55f), new(0.6f, 0), new(0.5f, 0.45f), new(0.25f, 0.7f), new(0, 0.76f), new(-0.25f, 0.7f), new(-0.5f, 0.45f), new(-0.6f, 0), new(-0.25f, -0.4f), new(-0.1f, -0.7f) };
            var inner = new Vector2[] { new(w * 0.6f, -0.55f), new(0.28f, 0.15f), new(0.2f, 0.5f), new(0, 0.58f), new(-0.2f, 0.5f), new(-0.28f, 0.15f) };
            Fill(Shapes.Offset(Rot(pts.Select(q => q * s).ToArray(), angle), at), color);
            Fill(Shapes.Offset(Rot(inner.Select(q => q * s).ToArray(), angle), at), color == null ? Hi : Ink, false);
        }

        public void Drop(Vector2 at, float s, Color color)
        {
            var pts = new List<Vector2> { at + new Vector2(0, -s * 1.1f) };
            for (int i = 0; i <= 10; i++) pts.Add(at + new Vector2(0, s * 0.25f) + Vector2.FromAngle(-Mathf.Pi / 3 + i * (Mathf.Pi * 5 / 3) / 10) * s * 0.65f);
            Fill(pts.ToArray(), color, false);
        }

        public void Cloud(Vector2 at, float s)
        {
            (Vector2 o, float r)[] puffs = { (new(-9, 2), 6), (new(0, -3), 8.5f), (new(9, 2), 6.5f) };
            var baseRect = Shapes.RoundRect(new Rect2(at.X - 15 * s, at.Y + 1 * s, 30 * s, 8 * s), 4 * s);
            foreach (var (o, r) in puffs) Ci.DrawCircle(C + at + o * s + Off, r * s, Shadow, true, -1, true);
            Ci.DrawColoredPolygon(Shapes.Offset(Abs(baseRect), Off), Shadow);
            foreach (var (o, r) in puffs) Ci.DrawCircle(C + at + o * s, r * s, Ink, true, -1, true);
            Ci.DrawColoredPolygon(Abs(baseRect), Ink);
        }

        public void Bolt(Vector2 at, float s, Color? color = null, bool shadow = true)
        {
            var pts = new Vector2[] { new(0.25f, -1), new(-0.5f, 0.12f), new(-0.02f, 0.12f), new(-0.3f, 1.05f), new(0.55f, -0.25f), new(0.05f, -0.25f), new(0.45f, -1) };
            Fill(pts.Select(q => at + q * s).ToArray(), color, shadow);
        }

        public void Spring(Vector2 bottom, Vector2 top, float width, int coils)
        {
            var pts = new List<Vector2> { bottom };
            for (int i = 1; i < coils * 2; i++) pts.Add(bottom.Lerp(top, i / (coils * 2f)) + new Vector2(i % 2 == 0 ? -width / 2 : width / 2, 0));
            pts.Add(top);
            Line(pts.ToArray(), 3);
        }

        /// <summary>A magazine with <paramref name="rounds"/> rounds showing, tipped by <paramref name="tilt"/>.</summary>
        public void Mag(Vector2 at, float w, float h, int rounds, float tilt = 0)
        {
            Fill(Shapes.Offset(Rot(Shapes.RoundRect(new Rect2(-w / 2, -h / 2, w, h), 3), tilt), at));
            for (int i = 0; i < rounds; i++)
            {
                float y = -h / 2 + 4 + i * (h - 8) / Mathf.Max(rounds, 1);
                var r = Shapes.RoundRect(new Rect2(-w / 2 + 3, y, w - 6, Mathf.Min(3, (h - 8) / rounds - 1.2f)), 1);
                Fill(Shapes.Offset(Rot(r, tilt), at), Dark, false);
            }
        }

        /// <summary>A feathered wing from its root at <paramref name="at"/>, reaching right (or left when <paramref name="side"/> is -1).</summary>
        public void Wing(Vector2 at, float s, int side, float lift = 0)
        {
            var pts = new Vector2[]
            {
                new(0, 0.1f), new(0.2f, -0.4f - lift), new(0.6f, -0.75f - lift * 2), new(1.05f, -0.85f - lift * 3), new(0.9f, -0.4f - lift * 2),
                new(0.78f, -0.3f - lift * 2), new(0.66f, 0f - lift), new(0.5f, -0.02f - lift), new(0.38f, 0.28f), new(0.22f, 0.22f), new(0.1f, 0.4f),
            };
            Fill(pts.Select(q => at + new Vector2(q.X * side, q.Y) * s).ToArray());
        }

        public void Skull(float lean)
        {
            Vector2 P(float x, float y) => new Vector2(x, y).Rotated(lean);
            var skull = new List<Vector2>();
            for (int i = 0; i <= 20; i++)
            {
                float a = Mathf.Pi * (0.85f + 1.3f * i / 20f);
                skull.Add(P(14 * Mathf.Cos(a), -3 + 13 * Mathf.Sin(a)));
            }
            skull.AddRange(new[] { P(8, 9), P(7, 14), P(-7, 14), P(-8, 9) });
            Fill(skull.ToArray());
            Dot(P(-5.5f, -2), 3.6f, Dark, false);
            Dot(P(5.5f, -2), 3.6f, Dark, false);
            Fill(new[] { P(0, 3), P(2, 7), P(-2, 7) }, Dark, false);
            for (int i = -1; i <= 1; i++) Seg(P(i * 3.2f, 10), P(i * 3.2f, 14), 1.4f, Dark, false);
        }

        public void Plus(Vector2 at, float r, float w, Color color)
        {
            Seg(at - new Vector2(r, 0), at + new Vector2(r, 0), w, color, false);
            Seg(at - new Vector2(0, r), at + new Vector2(0, r), w, color, false);
        }

        /// <summary>A small white disc with a dark +, - or down arrow ('v'): more, fewer, worse.</summary>
        public void Badge(Vector2 at, char kind)
        {
            Dot(at, 6.5f);
            switch (kind)
            {
                case '+': Plus(at, 3.5f, 2.2f, Dark); break;
                case '-': Seg(at - new Vector2(3.5f, 0), at + new Vector2(3.5f, 0), 2.2f, Dark, false); break;
                default:
                    Seg(at + new Vector2(0, -3.5f), at + new Vector2(0, 1), 2, Dark, false);
                    Fill(new[] { at + new Vector2(-3.2f, 0), at + new Vector2(3.2f, 0), at + new Vector2(0, 4) }, Dark, false);
                    break;
            }
        }
    }
}
