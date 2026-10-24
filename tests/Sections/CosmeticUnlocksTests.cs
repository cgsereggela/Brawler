using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class CosmeticUnlocksTests
{
    public static void Run()
    {
        var c = new Career { Matches = 12, Wins = 3, Kills = 140, BestWave = 4, GunGameWins = 1 };
        var back = Career.Decode(c.Encode());
        Check(back.Matches == 12 && back.Wins == 3 && back.Kills == 140 && back.BestWave == 4 && back.GunGameWins == 1, "Career: round-trips through its profile text");
        var odd = Career.Decode("kills=7;future=3;wins=-2;matches=x");
        Check(odd.Kills == 7 && odd.Wins == 0 && odd.Matches == 0 && Career.Decode(null).Matches == 0, "Career: unknown keys, negatives and junk are ignored");

        Check(Unlocks.All.All(u => u.Option > 0 && u.Option < Appearance.Count(u.Part)) && Unlocks.All.Select(u => (u.Part, u.Option)).Distinct().Count() == Unlocks.All.Length,
              "Unlocks: each is a real option, never a part's first (always free) one, and listed once");
        Check(Enumerable.Range(1, Appearance.Count(LookPart.Trail) - 1).All(t => Unlocks.For(LookPart.Trail, t) != null), "Unlocks: every trail but None is earned");
        Check(Unlocks.All.All(u => !u.Met(new Career())), "Unlocks: a new player has none");

        var look = Appearance.Default();
        look[LookPart.Hat] = 12;   // Laurel: win 10 matches
        look[LookPart.Trail] = 1;  // Sparks: play 3 matches
        Check(!Unlocks.Allowed(c, look) && Unlocks.Earned(c, look).Hat == 0 && Unlocks.Earned(c, look).Trail == 1 && Unlocks.Allowed(c, Unlocks.Earned(c, look)),
              "Unlocks: an unearned option comes off the look, earned ones stay");
        var gained = Unlocks.New(new Career { Matches = 2, Kills = 49 }, new Career { Matches = 3, Kills = 51 });
        Check(gained.Count == 2 && gained.Any(u => u.Name == "Sparks") && gained.Any(u => u.Name == "Pirate"), "Unlocks: crossing milestones lists what they unlock",
              string.Join(",", gained.Select(u => u.Name)));

        var rng = new Rng(9);
        bool free = true;
        for (int i = 0; i < 300; i++) free &= Unlocks.Allowed(new Career(), Appearance.Random(rng));
        Check(free, "Unlocks: random looks (bots, Randomize) only use free options");
        Check(Appearance.FromBytes(new byte[] { 1, 0, 2, 3, 4, 5, 6 }).Trail == 0, "Look: lobby data from before trails reads as no trail");
    }
}
