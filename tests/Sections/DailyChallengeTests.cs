using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class DailyChallengeTests
{
    public static void Run()
    {
        var today = Daily.For(20261023);
        var again = Daily.For(new DateTime(2026, 10, 23, 23, 59, 0, DateTimeKind.Utc));
        Check(today.Seed == again.Seed && today.ArenaSeed == again.ArenaSeed && today.Mode == again.Mode && today.StartingCards.SequenceEqual(again.StartingCards),
              "Daily: the same day gives the same challenge");
        var days = Enumerable.Range(0, 30).Select(i => Daily.For(new DateTime(2026, 11, 1).AddDays(i))).ToList();
        Check(days.Select(d => d.ArenaSeed).Distinct().Count() == 30 && days.Select(d => d.Mode).Distinct().Count() > 1, "Daily: days differ in arena and mode");
        Check(days.All(d => d.StartingCards.Count == Daily.StartingCardCount && d.StartingCards.Distinct().Count() == Daily.StartingCardCount
                            && d.StartingCards.All(id => CardRegistry.Get(id) is { IsRare: false, IsCurse: false } && !CardRegistry.IsCustom(id))),
              "Daily: three different built-in common starting cards");
        Check(days.All(d => Daily.Modes.Contains(d.Mode) && GameModes.Problem(d.Mode, 0) == null), "Daily: the mode works in free-for-all");
        Check(today.DateText == "2026-10-23", "Daily: date text");
        var arenaCode = MapCode.Encode(today.Arena());
        Check(arenaCode == MapCode.Encode(Daily.For(20261023).Arena()) && MapCode.Decode(arenaCode) is { } dayArena && dayArena.Name == "Daily 2026-10-23"
              && dayArena.Build(Daily.Bots + 1).Spawns.Count >= Daily.Bots + 1,
              "Daily: the day's arena is the same every time, survives a map code and has a spawn for everyone");

        var lost = new DailyResult(false, 4, 20);
        var won = new DailyResult(true, 5, 2);
        Check(won.CompareTo(lost) > 0 && new DailyResult(false, 4, 21).CompareTo(lost) > 0 && new DailyResult(false, 3, 99).CompareTo(lost) < 0,
              "Daily: a win beats any loss, then rounds, then kills");
        string s = DailyBest.Merge(null, 20261023, lost);
        Check(DailyBest.Decode(s, 20261023) == lost && DailyBest.Decode(s, 20261024) == null, "Daily: the best is kept for its own day only");
        Check(DailyBest.Merge(s, 20261023, new DailyResult(false, 1, 0)) == s && DailyBest.Decode(DailyBest.Merge(s, 20261023, won), 20261023) == won,
              "Daily: a worse run keeps the best, a better one replaces it");
        Check(DailyBest.Decode(DailyBest.Merge(s, 20261024, new DailyResult(false, 0, 0)), 20261024) == new DailyResult(false, 0, 0), "Daily: a new day starts over");
    }
}
