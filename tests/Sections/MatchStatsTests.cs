using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class MatchStatsTests
{
    public static void Run()
    {
        var book = new AwardBook();
        book.Add(0, AwardStat.Kills, 3); book.Add(0, AwardStat.Shots, 40); book.Add(0, AwardStat.Hits, 10);
        book.Add(0, AwardStat.DamageDealt, 250.6f); book.Add(0, AwardStat.DamageTaken, 120); book.Add(0, AwardStat.HitsBlocked, 4);
        book.Add(2, AwardStat.Kills, 1); book.Add(2, AwardStat.Shots, 5); book.Add(2, AwardStat.Hits, 9); // blasts that caught two
        book.Add(2, AwardStat.DamageTaken, 300);
        var lines = MatchStats.From(book, new[] { 0, 2, 5 });
        Check(lines.Count == 3 && lines[0] is { Slot: 0, Kills: 3, Shots: 40, Hits: 10, Blocked: 4 } && lines[0].Accuracy == 0.25f && lines[0].AccuracyText == "25%",
              "Match stats: a player's line comes off the award book", lines[0].ToString());
        Check(lines[1].Accuracy == 1 && lines[2].AccuracyText == "-", "Match stats: accuracy is capped at every shot hitting, and blank with no shots");
        Check(MatchStats.Leader(lines, MatchStats.Column.Kills) == 0 && MatchStats.Leader(lines, MatchStats.Column.Accuracy) == 2
              && MatchStats.Leader(lines, MatchStats.Column.DamageTaken) == -1,
              "Match stats: the column leader is lit, but never for damage taken");
        var tied = new List<MatchStatLine> { new(0, 2, 0, 0, 0, 0, 0), new(1, 2, 0, 0, 0, 0, 0) };
        Check(MatchStats.Leader(tied, MatchStats.Column.Kills) == -1 && MatchStats.Leader(tied, MatchStats.Column.Blocked) == -1,
              "Match stats: no leader on a tie or when nobody scored");

        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        MatchStats.Write(w, lines.Append(new MatchStatLine(7, 999, 1e6f, -5, 70000, 3, 2)).ToList());
        w.Flush();
        var back = MatchStats.Read(new System.IO.BinaryReader(new System.IO.MemoryStream(ms.ToArray())));
        Check(back.Count == 4 && back[0] == lines[0] with { DamageDealt = 251 } && back[1] == lines[1] && back[3] == new MatchStatLine(7, 255, 65535, 0, 65535, 3, 2),
              "Match stats: the table round-trips to every peer, whole numbers, clamped to fit", string.Join("; ", back));
    }
}
