using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class EndOfMatchAwardsTests
{
    public static void Run()
    {
        var book = new AwardBook();
        book.Add(0, AwardStat.SelfKills, 3);
        book.Add(1, AwardStat.SelfKills, 1);
        book.Add(0, AwardStat.Kills, 5); // tied with slot 1: nobody is the Menace
        book.Add(1, AwardStat.Kills, 5);
        book.Max(2, AwardStat.Airtime, 2f);
        book.Max(2, AwardStat.Airtime, 1f); // keeps the longest
        var awards = Awards.Pick(book, new[] { 0, 1, 2 }, new Rng(1));
        Check(awards.Any(a => a.Id == 0 && a.Slot == 0 && a.Blurb == "3 self-inflicted exits"), "Awards: a clear leader gets the title", string.Join(", ", awards.Select(a => a.Title)));
        Check(!awards.Any(a => a.Id == 10), "Awards: a tie gives nobody the title");
        Check(!awards.Any(a => a.Id == 1) && Math.Abs(book.Get(2, AwardStat.Airtime) - 2f) < 0.001f, "Awards: below the minimum, no title (and Max keeps the record)");
        Check(Awards.Pick(book, new[] { 0 }, new Rng(1)).Count == 0, "Awards: none with fewer than two players");

        // Everyone qualifies for something: four awards, one each before anyone gets a second.
        var full = new AwardBook();
        for (int s = 0; s < 4; s++) full.Add(s, AwardStat.Shots, 10);
        full.Add(0, AwardStat.SelfKills, 4);
        full.Add(0, AwardStat.Falls, 4);
        full.Add(1, AwardStat.HitsBlocked, 9);
        full.Add(2, AwardStat.Healed, 200);
        full.Add(3, AwardStat.Kills, 7);
        full.Add(3, AwardStat.DamageTaken, 900);
        var four = Awards.Pick(full, new[] { 0, 1, 2, 3 }, new Rng(5));
        Check(four.Count == Awards.MaxShown && four.Select(a => a.Slot).Distinct().Count() == 4, "Awards: four players, one award each",
            string.Join(", ", four.Select(a => $"{a.Slot}:{a.Title}")));
        var again = Awards.Pick(full, new[] { 0, 1, 2, 3 }, new Rng(5));
        Check(four.SequenceEqual(again), "Awards: same seed, same awards");
        var two = Awards.Pick(full, new[] { 0, 3 }, new Rng(5));
        Check(two.Count == 4 && two.Count(a => a.Slot == 0) == 2, "Awards: two players can get two each", string.Join(", ", two.Select(a => $"{a.Slot}:{a.Title}")));
    }
}
