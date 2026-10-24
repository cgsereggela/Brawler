using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class CardStatsTests
{
    public static void Run()
    {
        // 10 two-player battles: the Homing (5) holder always beats the Tank (7) holder.
        MatchRecord Make(int seed)
        {
            var m = new MatchRecord { Id = $"m{seed}", Mode = "sim", CardsHash = StatsJson.CardsHash(), Complete = true, Winner = 0 };
            m.Players.Add(new PlayerRec { Slot = 0, Bot = true, FinalCards = { 5, 5 } });
            m.Players.Add(new PlayerRec { Slot = 1, Bot = false, FinalCards = { 7 } });
            for (int i = 0; i < 10; i++)
            {
                var b = new BattleRec { N = i + 1, Round = 1, FightSec = 4, Winner = 0 };
                b.Players.Add(new BattlePlayerRec { Slot = 0, Cards = { 5, 5 }, DamageDealt = 100 });
                b.Players.Add(new BattlePlayerRec { Slot = 1, Cards = { 7 }, DiedAt = 4 });
                m.Battles.Add(b);
            }
            m.Picks.Add(new PickRec { Slot = 1, Bot = false, Offered = { 7, 1, 2 }, Picked = 7 });
            return m;
        }

        var line = StatsJson.ToLine(Make(1));
        var parsed = StatsJson.ParseLines(new[] { line, "{broken", "" }, out int skipped);
        Check(parsed.Count == 1 && skipped == 1 && parsed[0].Battles.Count == 10 && parsed[0].Battles[0].Players[0].Cards.Count == 2,
            "Stats JSON round-trips and skips broken lines", $"parsed {parsed.Count}, skipped {skipped}");

        var data = StatsReport.Build(new[] { Make(1), Make(2) }, new StatsFilter { MinSamples = 5 });
        var homing = data.Cards.First(c => c.Card.Id == 5);
        var tank = data.Cards.First(c => c.Card.Id == 7);
        Check(Math.Abs(homing.Battle.Ratio - 2.0) < 1e-9 && tank.Battle.Ratio == 0, "Win ratio vs fair share",
            $"homing {homing.Battle.Ratio}, tank {tank.Battle.Ratio}");
        Check(homing.Battle.N == 20 && homing.ByStacks.ContainsKey(2) && !homing.ByStacks.ContainsKey(1), "Stacks count once per battle, bucketed by copies");
        Check(Math.Abs(homing.AvgWinSec - 4) < 1e-9 && Math.Abs(homing.Per(homing.Dealt) - 100) < 1e-9, "Win time and per-battle damage");
        Check(Math.Abs(tank.PickRatioHuman - 3.0) < 1e-9 && tank.Offered == 2, "Pick ratio vs random choice", $"{tank.PickRatioHuman}");
        Check(Math.Abs(homing.Match.Ratio - 2.0) < 1e-9, "Match win ratio from final cards");

        var humans = StatsReport.Build(new[] { Make(1) }, new StatsFilter { Bots = false });
        Check(humans.Cards.First(c => c.Card.Id == 5).Battle.N == 0 && humans.Cards.First(c => c.Card.Id == 7).Battle.N == 10,
            "Human/bot filter applies per player");
        var other = StatsReport.Build(new[] { Make(1) }, new StatsFilter { CardsHash = "deadbeef" });
        Check(other.Matches == 0, "Card-table hash filter excludes other balance versions");
        var koth = Make(3);
        koth.Settings.GameMode = "koth";
        Check(StatsReport.Build(new[] { koth }, new StatsFilter()).Matches == 0 && StatsReport.Build(new[] { Make(3) }, new StatsFilter()).Matches == 1,
            "Other game modes stay out of the card win rates");
        var oldLine = StatsJson.ToLine(Make(4)).Replace("\"gameMode\":\"elimination\",", "").Replace(",\"gameMode\":\"elimination\"", "");
        Check(!oldLine.Contains("gameMode") && StatsJson.ParseLines(new[] { oldLine }, out _)[0].Settings.GameMode == "elimination",
            "Logs from before game modes read as elimination");
        Check(StatsReport.Markdown(data).Contains("| Homing |"), "Markdown report renders");
    }
}
