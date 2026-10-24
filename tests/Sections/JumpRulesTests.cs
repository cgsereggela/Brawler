using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class JumpRulesTests
{
    public static void Run()
    {
        // Jumping from the ground spends the ground jump; card air jumps still follow.
        var j = new JumpCharges(); j.Land(1);
        var seq = new[] { j.Use(true, false, false, true), j.Use(false, false, false, true), j.Use(false, false, false, true) };
        Check(seq[0] == JumpKind.Ground && seq[1] == JumpKind.Air && seq[2] == JumpKind.None, "Ground jump, then air jumps", string.Join(",", seq));

        // Walking off a ledge keeps the ground jump for any time in the air (no coyote needed), then air jumps apply.
        j = new JumpCharges(); j.Land(1);
        var ledge = new[] { j.Use(false, false, false, true), j.Use(false, false, false, true), j.Use(false, false, false, true) };
        Check(ledge[0] == JumpKind.Ledge && ledge[1] == JumpKind.Air && ledge[2] == JumpKind.None, "Ledge jump kept after walking off", string.Join(",", ledge));
        j = new JumpCharges(); j.Land(0);
        Check(j.Use(false, false, false, true) == JumpKind.Ledge && j.Use(false, false, false, true) == JumpKind.None, "Ledge jump without air-jump cards, once");

        // A wall beats the ledge jump and costs nothing; the ledge jump is still there afterwards.
        j = new JumpCharges(); j.Land(0);
        Check(j.Use(false, true, false, true) == JumpKind.Wall && j.Left == 1 && j.Use(false, false, false, true) == JumpKind.Ledge, "Wall jump keeps the ledge jump");
        // ...but just off the floor a press is still a ground jump.
        j = new JumpCharges(); j.Land(0);
        Check(j.Use(true, true, false, true) == JumpKind.Ground && j.Left == 0, "Coyote ground jump beats wall jump");

        // Jetpack only once jumps run out, and only on a fresh press; landing restores everything.
        j = new JumpCharges(); j.Land(0); j.Ground = false;
        Check(j.Use(false, false, true, false) == JumpKind.None && j.Use(false, false, true, true) == JumpKind.Jet, "Jetpack after jumps, fresh press only");
        j.Land(2);
        Check(j.Ground && j.Air == 2 && j.Left == 3, "Landing restores ground and air jumps");

        // A jump pad launch keeps the ground jump: presses wait while the launch outpaces a jump, then it's a full ledge jump.
        j = new JumpCharges(); j.Land(1);
        Check(j.Use(false, false, true, true, risingFaster: true) == JumpKind.None && j.Left == 2
              && j.Use(false, false, false, true) == JumpKind.Ledge && j.Use(false, false, false, true) == JumpKind.Air,
            "Pad launch keeps the ground jump until landing");
    }
}
