using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class GunRigTests
{
    public static void Run()
    {
        // Add any new gun look here: the hand path in GunRig must keep every look clear of the face.
        var looks = new[]
        {
            ("default", GunLook.Default),
        };
        // Every aim the facing rules allow in sprite space: up to MaxBackAimX behind, from straight up round to straight down.
        float back = MathF.Asin(GunRig.MaxBackAimX);
        foreach (var (name, look) in looks)
        {
            var boxes = GunRig.Footprint(look);
            int covered = 0;
            var bad = new SortedSet<int>();
            float minMargin = float.MaxValue;
            string worst = "";
            for (float deg = -90 - back * 180 / MathF.PI; deg <= 90 + back * 180 / MathF.PI + 1e-3f; deg += 0.5f)
                foreach (float kick in new[] { 0f, 0.5f, 1f })
                {
                    float rad = deg * MathF.PI / 180;
                    var (hand, angle) = GunRig.Pose(new Vector2(MathF.Cos(rad), MathF.Sin(rad)), kick);
                    foreach (var (x, y, w, h) in boxes)
                        for (float u = x; u <= x + w + 1e-3f; u += Math.Min(0.25f, w))
                            for (float v = y; v <= y + h + 1e-3f; v += Math.Min(0.25f, h))
                            {
                                var p = GunRig.ToSprite(new Vector2(u, v), hand, angle);
                                foreach (var (fx, fy, fw, fh) in GunRig.Face)
                                {
                                    // Signed distance outside the face box (negative = inside).
                                    float dx = Math.Max(fx - p.X, p.X - (fx + fw)), dy = Math.Max(fy - p.Y, p.Y - (fy + fh));
                                    float margin = dx > 0 && dy > 0 ? MathF.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy);
                                    if (margin < minMargin) { minMargin = margin; worst = $"{deg:0.#} deg kick {kick} gun ({u:0.##},{v:0.##}) at {p} in face box {fx},{fy}"; }
                                    if (margin < 0) { covered++; bad.Add((int)MathF.Floor(deg / 5) * 5); }
                                }
                            }
                }
            Check(covered == 0, $"Gun ({name}) never covers the eyes, nose or mouth at any allowed aim or recoil",
                $"closest {minMargin:0.00} px at {worst}{(covered > 0 ? $"; {covered} points inside, aims (5-degree bands): {string.Join(",", bad)}" : "")}");
        }

        // The two-bone arm reaches the grip at every allowed aim and recoil without stretching, bends more as the hand
        // comes in, and the elbow moves smoothly (no flips) as the aim sweeps round.
        {
            float worstUpper = 0, worstFore = 0, maxJump = 0, minReachSlack = float.MaxValue;
            Vector2? prev = null;
            for (float deg = -90 - back * 180 / MathF.PI; deg <= 90 + back * 180 / MathF.PI + 1e-3f; deg += 0.5f)
            {
                float rad = deg * MathF.PI / 180;
                var aim = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                foreach (float kick in new[] { 0f, 1f })
                {
                    var (grip, elbow) = GunRig.Arm(aim, kick);
                    worstUpper = MathF.Max(worstUpper, MathF.Abs(Vector2.Distance(GunRig.Shoulder, elbow) - GunRig.UpperArm));
                    worstFore = MathF.Max(worstFore, MathF.Abs(Vector2.Distance(elbow, grip) - GunRig.Forearm));
                    minReachSlack = MathF.Min(minReachSlack, GunRig.UpperArm + GunRig.Forearm - Vector2.Distance(GunRig.Shoulder, grip));
                }
                var e0 = GunRig.Arm(aim).Elbow;
                if (prev is { } pe) maxJump = MathF.Max(maxJump, Vector2.Distance(pe, e0));
                prev = e0;
            }
            Check(worstUpper < 1e-3f && worstFore < 1e-3f && minReachSlack > 0, "Arm reaches the grip at every aim without stretching",
                $"upper err {worstUpper:0.0000}, fore err {worstFore:0.0000}, reach slack {minReachSlack:0.00}");
            Check(maxJump < 0.6f, "Elbow moves smoothly as the aim sweeps round", $"largest step {maxJump:0.00} per 0.5 deg");
            var (lg, le) = GunRig.Arm(new Vector2(1, 0));
            var (kg, ke) = GunRig.Arm(new Vector2(1, 0), 1);
            float Sag(Vector2 gr, Vector2 el) { var d = Vector2.Normalize(gr - GunRig.Shoulder); var r = el - GunRig.Shoulder; return MathF.Abs(r.X * d.Y - r.Y * d.X); }
            Check(le.Y > GunRig.Shoulder.Y + (lg.Y - GunRig.Shoulder.Y) * ((le.X - GunRig.Shoulder.X) / (lg.X - GunRig.Shoulder.X)) && Sag(kg, ke) > Sag(lg, le),
                "Level arm's elbow hangs below the line and folds further under recoil", $"sag {Sag(lg, le):0.00} -> {Sag(kg, ke):0.00}");
        }

        // The muzzle mirrors with facing, scales with size and sits past the body at arm's length.
        var g = GunLook.Default;
        var right = GunRig.Muzzle(new Vector2(1, 0), 1, 1, g);
        var left = GunRig.Muzzle(new Vector2(-1, 0), -1, 1, g);
        var big = GunRig.Muzzle(new Vector2(1, 0), 1, 2, g);
        Check(MathF.Abs(right.X + left.X) < 1e-4f && MathF.Abs(right.Y - left.Y) < 1e-4f, "Muzzle mirrors with facing", $"{right} / {left}");
        Check(Vector2.Distance(big, right * 2) < 1e-4f, "Muzzle scales with size", $"{big} vs {right * 2}");
        Check(right.X > Body.W / 2 + 8, "Level muzzle is held out at arm's length past the body", $"{right}");
        var up = GunRig.Muzzle(new Vector2(0, -1), 1, 1, g);
        Check(up.Y < -Body.H / 2 - 10 && MathF.Abs(up.X) < 3, "Upward muzzle is held straight up over the head", $"{up}");
        // Aiming straight up, the hand and the whole gun are raised over the top of the head (sprite y -9.5), recoil included.
        float lowest = float.MinValue;
        foreach (float kick in new[] { 0f, 0.5f, 1f })
        {
            var (hand, angle) = GunRig.Pose(new Vector2(0, -1), kick);
            foreach (var (x, y, w, h) in GunRig.Footprint(g))
                foreach (var corner in new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x, y + h), new Vector2(x + w, y + h) })
                    lowest = Math.Max(lowest, GunRig.ToSprite(corner, hand, angle).Y);
        }
        Check(lowest < -9.5f, "Aiming straight up, the hand and gun sit above the head", $"lowest point y {lowest:0.0}");

        // The muzzle (bullet origin) follows the aim smoothly: no jumps as the aim sweeps, so bullets leave the barrel.
        float maxStep = 0;
        for (int facing = -1; facing <= 1; facing += 2)
            for (float deg = -100; deg < 100; deg += 1)
            {
                Vector2 A(float d) => new(facing * MathF.Cos(d * MathF.PI / 180), MathF.Sin(d * MathF.PI / 180));
                maxStep = Math.Max(maxStep, Vector2.Distance(GunRig.Muzzle(A(deg), facing, 1, g), GunRig.Muzzle(A(deg + 1), facing, 1, g)));
            }
        Check(maxStep < 0.6f, "Muzzle moves smoothly as the aim turns (1 degree steps)", $"largest step {maxStep:0.000} px");

        // Same inputs, same muzzle: the host's bullet origin is a pure function every peer can reproduce.
        Check(GunRig.Muzzle(new Vector2(0.6f, -0.8f), 1, 1.3f, g) == GunRig.Muzzle(new Vector2(0.6f, -0.8f), 1, 1.3f, g),
            "Muzzle is deterministic");
    }
}
