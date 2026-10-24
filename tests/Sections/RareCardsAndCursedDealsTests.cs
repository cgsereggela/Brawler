using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class RareCardsAndCursedDealsTests
{
    public static void Run()
    {
        var rares = CardRegistry.All.Where(c => c.IsRare).ToList();
        var curses = CardRegistry.All.Where(c => c.IsCurse).ToList();
        Check(rares.Count >= 10 && curses.Count >= 12, "At least 10 rare cards and 12 curses", $"{rares.Count} rare, {curses.Count} curses");
        Check(rares.All(c => !c.IsCurse), "No card is both rare and a curse");
        // A curse is a pure downside: compared with base stats it must make something worse and nothing better.
        var baseStats = StatBlock.Base();
        var higherIsWorse = new HashSet<Stat> { Stat.FireCooldown, Stat.ReloadTime, Stat.BlockCooldown, Stat.Size, Stat.KnockbackTaken };
        bool allWorse = curses.All(c =>
        {
            var s = StatBlock.Compute(new[] { c });
            var moved = Enum.GetValues<Stat>().Where(st => Math.Abs(s[st] - baseStats[st]) > 1e-4f).ToList();
            return moved.Count > 0 && moved.All(st => higherIsWorse.Contains(st) ? s[st] > baseStats[st] : s[st] < baseStats[st]);
        });
        Check(allWorse, "Every curse only makes stats worse");
        var jaw = StatBlock.Compute(new[] { CardRegistry.Get(99) });
        Check(Math.Abs(jaw[Stat.Armor] + 0.2f) < 1e-4f, "Glass Jaw's negative armor survives the clamp", $"{jaw[Stat.Armor]}");

        var rng = new Rng(11);
        bool plainOnly = true, neverCursed = true, atMostOne = true;
        int picks = 3000, withRare = 0;
        for (int i = 0; i < picks; i++)
        {
            var plain = CardRegistry.RollOptions(rng, 5);
            plainOnly &= plain.All(id => !CardRegistry.Get(id).IsRare && !CardRegistry.Get(id).IsCurse);
            var opts = CardRegistry.RollOptions(rng, 5, null, 0.1f);
            neverCursed &= opts.All(id => !CardRegistry.Get(id).IsCurse) && opts.Distinct().Count() == opts.Count && opts.Count == 5;
            int r = opts.Count(id => CardRegistry.Get(id).IsRare);
            atMostOne &= r <= 1;
            if (r > 0) withRare++;
        }
        Check(plainOnly, "Rares off: offers are only common cards");
        Check(neverCursed, "Curses are never offered on their own, and rare offers stay distinct and full");
        Check(atMostOne, "A pick offers at most one rare card while commons are left");
        double rate = (double)withRare / picks;
        Check(rate > 0.08 && rate < 0.12, "A 10% rare chance puts a rare card in about 10% of picks", $"{rate:P1}");
        var allRare = CardRegistry.RollOptions(new Rng(2), 5, null, 1f);
        Check(allRare.Count == 5 && allRare.Count(id => CardRegistry.Get(id).IsRare) == 1, "A 100% rare chance puts one rare in every pick");
        var commonsOff = new HashSet<int>(CardPool.Allowed(null));
        var onlyRares = CardRegistry.RollOptions(new Rng(2), 5, commonsOff, 0.1f);
        Check(onlyRares.Count == 5 && onlyRares.All(id => CardRegistry.Get(id).IsRare), "With every common card disabled, rares fill the offer");

        bool dealsOk = true;
        var seen = new HashSet<int>();
        var r2 = new Rng(4);
        var noArsenal = new HashSet<int> { 82, 92 };
        for (int i = 0; i < 2000; i++)
        {
            var opts = CardRegistry.RollOptions(r2, 5, noArsenal, 0.35f);
            var deal = CardRegistry.RollDeal(r2, noArsenal, opts);
            dealsOk &= deal is { } d && CardRegistry.Get(d.Rare).IsRare && CardRegistry.Get(d.Curse).IsCurse
                       && !opts.Contains(d.Rare) && !noArsenal.Contains(d.Rare) && !noArsenal.Contains(d.Curse);
            if (deal is { } dd) { seen.Add(dd.Rare); seen.Add(dd.Curse); }
        }
        Check(dealsOk, "A deal is a rare card not already offered plus a curse, both enabled");
        Check(seen.Count == rares.Count + curses.Count - 2, "Every enabled rare and curse comes up in deals", $"{seen.Count}");
        var noCurses = new HashSet<int>(curses.Select(c => c.Id));
        Check(CardRegistry.RollDeal(new Rng(1), noCurses, new List<int>()) == null, "No enabled curses: no deal");
        var noRares = new HashSet<int>(rares.Select(c => c.Id));
        Check(CardRegistry.RollDeal(new Rng(1), noRares, new List<int>()) == null, "No enabled rares: no deal");
        var dealRng = new Rng(9);
        int deals = 0;
        for (int i = 0; i < 3000; i++)
            if (CardRegistry.RollDeal(dealRng, null, new List<int>(), 0.1f) != null) deals++;
        Check(deals > 240 && deals < 360, "A 10% rare chance offers a cursed deal in about 10% of picks", $"{deals / 30.0:F1}%");
        Check(CardRegistry.RollDeal(new Rng(1), null, new List<int>(), 0) == null, "Rares off: no deals either");
        Check(CardPool.CommonCount(noRares) == CardPool.CommonCount(null) && CardPool.CommonCount(null) >= CardPool.MinEnabled,
            "Rares don't count toward the common minimum");

        // Stats report: a taken deal counts as a pick for the rare and the curse, an extra option for everything offered.
        var m = new MatchRecord { Complete = true, Winner = 0 };
        m.Players.Add(new PlayerRec { Slot = 0, Name = "A" });
        m.Picks.Add(new PickRec { Slot = 0, Offered = new List<int> { 1, 2, 3 }, Deal = new List<int> { 83, 94 }, Picked = 83, TookDeal = true });
        var rep = StatsReport.Build(new[] { m }, new StatsFilter());
        var c83 = rep.Cards.First(c => c.Card.Id == 83);
        var c94 = rep.Cards.First(c => c.Card.Id == 94);
        var c1 = rep.Cards.First(c => c.Card.Id == 1);
        Check(c83.Picked == 1 && c94.Picked == 1 && c83.Offered == 1 && Math.Abs(c1.ExpectedPicks - 0.25) < 1e-9,
            "Stats report: a taken deal counts its rare and curse", $"{c83.Picked}/{c94.Picked}/{c1.ExpectedPicks}");
    }
}
