using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class ControllersAndCouchCoOpTests
{
    public static void Run()
    {
        Check(Gamepad.Stick(0.15f, 0.1f, 0.22f) == Vector2.Zero, "Stick: inside the dead zone reads as centered");
        var full = Gamepad.Stick(1, 0, 0.22f);
        Check(Math.Abs(full.X - 1) < 1e-4f && full.Y == 0, "Stick: full deflection still reaches 1", $"{full}");
        var half = Gamepad.Stick(0, -0.61f, 0.22f);
        Check(half.X == 0 && half.Y < 0 && Math.Abs(half.Y + 0.5f) < 0.01f, "Stick: rescaled past the dead zone, direction kept", $"{half}");
        Check(Gamepad.Pressed(0b0101, 0b0110) == 0b0010, "Pad: only buttons that went down count as presses");
        Check(Gamepad.TriggerHeld(0.45f, false) && Gamepad.TriggerHeld(0.35f, true) && !Gamepad.TriggerHeld(0.35f, false),
            "Pad: triggers have a little hysteresis around the press point");

        var aim = new PadAim();
        Check(aim.Dir == Vector2.UnitX, "Aim: starts facing right");
        aim.Update(new PadState { RX = 0, RY = -1 });
        Check(aim.Aiming && Math.Abs(aim.Dir.Y + 1) < 1e-4f, "Aim: the right stick points the gun");
        aim.Update(new PadState());
        Check(!aim.Aiming && Math.Abs(aim.Dir.Y + 1) < 1e-4f, "Aim: let go, it keeps pointing where it was");
        aim.Update(new PadState { LX = -1 });
        Check(aim.Dir.X < -0.99f, "Aim: with the right stick resting, moving turns you that way");
        aim.Update(new PadState { LX = -0.4f, RX = 0.9f });
        Check(aim.Dir.X > 0.99f, "Aim: the right stick wins over the left");
        var me = Vector2.Zero;
        var ahead = new[] { new Vector2(200, 30) }; // about 8.5° below the aim line
        var bent = AimAssist.Bend(Vector2.UnitX, me, ahead, 0.5f);
        Check(bent.Y > 0 && bent.Y < 30 / 200f && Math.Abs(bent.Length() - 1) < 1e-4f, "Aim assist: bends part of the way toward an enemy near the line", $"{bent}");
        Check(AimAssist.Bend(Vector2.UnitX, me, ahead, 0) == Vector2.UnitX, "Aim assist: 0% leaves the aim alone");
        Check(AimAssist.Bend(Vector2.UnitX, me, new[] { new Vector2(0, 200) }, 1) == Vector2.UnitX, "Aim assist: an enemy well off the line doesn't pull");
        Check(AimAssist.Bend(Vector2.UnitX, me, new[] { new Vector2(900, 10) }, 1) == Vector2.UnitX, "Aim assist: an enemy out of range doesn't pull");
        var two = AimAssist.Bend(Vector2.UnitX, me, new[] { new Vector2(200, -60), new Vector2(200, 10) }, 1);
        Check(two.Y > 0, "Aim assist: the enemy nearest the line wins", $"{two}");
        Check(AimAssist.Bend(-Vector2.UnitX, me, new[] { new Vector2(-200, 5), new Vector2(-200, -5) }, 1).X < -0.99f, "Aim assist: works across the ±180° seam");

        foreach (var kind in Enum.GetValues<ColorVision.Kind>())
            Check(ColorVision.ClosestPair(ColorVision.Safe, kind) > 25, $"Colors: the safe set stays apart for {kind} vision", $"{ColorVision.ClosestPair(ColorVision.Safe, kind):0.0} ΔE");
        Check(ColorVision.ClosestPair(ColorVision.Usual, ColorVision.Kind.Deutan) < 5, "Colors: the usual blue and purple do merge for a deuteranope (why the safe set exists)");
        Check(ColorVision.Distance(ColorVision.Safe[0], ColorVision.Safe[1], ColorVision.Kind.Deutan) > 80, "Colors: the safe team colors (red and blue) are far apart");

        var held = new PadState { Buttons = 1u << PadLayout.LeftShoulder, RT = 0.9f, LT = 0.1f };
        Check(held.JumpHeld && held.ShootHeld && !held.BlockHeld && held.Active, "Pad: LB jumps, RT shoots, a resting LT doesn't block");

        var couch = new CouchRoster();
        Check(couch.Add(3) == 1 && couch.Add(0) == 2, "Couch: guests number from 1 in join order");
        Check(couch.Add(3) == -1, "Couch: a controller can't join twice");
        Check(couch.Claimed(0) && !couch.Claimed(1) && couch.DeviceOf(2) == 0, "Couch: knows whose controller is whose");
        couch.Remove(1);
        Check(couch.Add(5) == 1, "Couch: a freed number is reused");
        Check(couch.Add(6) == 3 && couch.Add(7) == -1 && couch.Count == CouchRoster.MaxGuests, "Couch: at most three guests per machine");
    }
}
