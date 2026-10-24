using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class TeamModesTests
{
    public static void Run()
    {
        Check(Teams.ModeName(0) == "Free-for-all" && Teams.ModeName(3) == "3v3", "Teams: mode names");
        Check(Teams.NextSize(0) == 2 && Teams.NextSize(2) == 3 && Teams.NextSize(4) == 0, "Teams: the mode button steps 2v2, 3v3, 4v4, then back to free-for-all");
        Check(Teams.Deal(5).SequenceEqual(new[] { 0, 1, 0, 1, 0 }), "Teams: dealing alternates");
        Check(Teams.Emptier(new int[0], 2) == 0 && Teams.Emptier(new[] { 0 }, 2) == 1 && Teams.Emptier(new[] { 0, 1 }, 2) == 0,
            "Teams: newcomers join the emptier team, ties to Red");
        Check(Teams.Emptier(new[] { 0, 1, 0, 1 }, 2) == -1, "Teams: no room once both are full");
        Check(Teams.Swap(new[] { 1 }, 0, 2) == 1 && Teams.Swap(new[] { 1, 1 }, 0, 2) == 0, "Teams: a swap needs room on the other side");
        Check(Teams.Problem(new[] { 0, 1 }, 0) == null && Teams.Problem(new[] { 0, 0 }, 0) == null, "Teams: free-for-all has no team rules");
        Check(Teams.Problem(new[] { 0, 0 }, 2) != null, "Teams: both teams need someone");
        Check(Teams.Problem(new[] { 0, 0, 0, 1 }, 2) != null, "Teams: a team can't go over the size");
        Check(Teams.Problem(new[] { 0, 0, 1 }, 2) == null, "Teams: uneven teams can still play");
        Check(!Teams.Settled(new[] { 0, 1, 1 }, out _), "Teams: both teams standing, the battle goes on");
        Check(Teams.Settled(new[] { 1, 1 }, out int w1) && w1 == 1, "Teams: one team left wins");
        Check(Teams.Settled(new int[0], out int w2) && w2 == -1, "Teams: nobody left is a draw");
    }
}
