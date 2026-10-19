using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Brawler.Logic;

/// <summary>Which player-battles and picks to count.</summary>
public sealed class StatsFilter
{
    /// <summary>null = all, true = only bots, false = only humans.</summary>
    public bool? Bots;
    /// <summary>null = all modes, else "sim", "host" or "offline".</summary>
    public string Mode;
    /// <summary>Only matches recorded with this card table (null = all).</summary>
    public string CardsHash;
    /// <summary>Minimum samples before a row is ranked/flagged (pairs, stacks).</summary>
    public int MinSamples = 30;
}

/// <summary>
/// Win "ratio" compares wins with a fair share: in a battle of k players each is expected to win 1/k of the time,
/// so ratio 1.00 is perfectly balanced, 1.20 means 20% more wins than a fair share. Draws are left out.
/// The ± is a 95% interval from the binomial variance, so small samples show up as wide intervals.
/// </summary>
public sealed class WinTally
{
    public int N;
    public double Wins, Expected, Var;

    public void Add(bool won, int players)
    {
        double p = 1.0 / players;
        N++;
        if (won) Wins++;
        Expected += p;
        Var += p * (1 - p);
    }

    public double Ratio => Expected > 0 ? Wins / Expected : double.NaN;
    public double Ci95 => Expected > 0 ? 1.96 * Math.Sqrt(Var) / Expected : double.NaN;
}

public sealed class CardStat
{
    public CardDef Card;
    public int Offered, Picked, OfferedHuman, PickedHuman;
    public double ExpectedPicks, ExpectedPicksHuman; // sum of 1/options over offers
    public double PickRoundSum;
    public readonly WinTally Battle = new(), Match = new();
    public readonly Dictionary<int, WinTally> ByStacks = new(); // 1, 2, 3 (=3+)
    public int Wins;
    public double WinFightSecSum;
    public double Shots, Hits, Dealt, Taken, Kills, Deaths, SurviveSec, Healed, SelfDamage, HitsBlocked;

    public double PickRate => Offered > 0 ? (double)Picked / Offered : double.NaN;
    /// <summary>Picked vs a random pick (1.0 = picked exactly as often as chance).</summary>
    public double PickRatio => ExpectedPicks > 0 ? Picked / ExpectedPicks : double.NaN;
    public double PickRatioHuman => ExpectedPicksHuman > 0 ? PickedHuman / ExpectedPicksHuman : double.NaN;
    public double AvgWinSec => Wins > 0 ? WinFightSecSum / Wins : double.NaN;
    public double AvgPickRound => Picked > 0 ? PickRoundSum / Picked : double.NaN;
    public double Per(double v) => Battle.N > 0 ? v / Battle.N : double.NaN;
    public double Accuracy => Shots > 0 ? Hits / Shots : double.NaN;
}

public sealed class PairStat
{
    public CardDef A, B;
    public readonly WinTally Both = new();
    /// <summary>Observed ratio minus the additive prediction (ratioA + ratioB − 1). Positive = synergy.</summary>
    public double Synergy;
}

public sealed class StatsReportData
{
    public int Matches, CompleteMatches, Battles, Draws, PlayerBattles, Picks, HumanPicks;
    public double AvgFightSec;
    public List<string> CardHashes = new();
    public List<CardStat> Cards = new();
    public List<PairStat> Pairs = new();
    public StatsFilter Filter;
}

public static class StatsReport
{
    public static StatsReportData Build(IEnumerable<MatchRecord> all, StatsFilter f)
    {
        var data = new StatsReportData { Filter = f };
        var cards = CardRegistry.All.ToDictionary(c => c.Id, c => new CardStat { Card = c });
        var pairs = new Dictionary<(int, int), PairStat>();
        double fightSum = 0;
        int fightN = 0;

        var matches = all.Where(m => f.Mode == null || m.Mode == f.Mode).ToList();
        data.CardHashes = matches.Select(m => m.CardsHash).Distinct().ToList();
        if (f.CardsHash != null) matches = matches.Where(m => m.CardsHash == f.CardsHash).ToList();

        foreach (var m in matches)
        {
            data.Matches++;
            if (m.Complete) data.CompleteMatches++;
            var isBot = m.Players.ToDictionary(p => p.Slot, p => p.Bot);
            bool Counted(int slot) => f.Bots == null || (isBot.TryGetValue(slot, out bool b) && b == f.Bots);

            foreach (var b in m.Battles)
            {
                data.Battles++;
                fightSum += b.FightSec;
                fightN++;
                if (b.Winner < 0) { data.Draws++; continue; }
                int k = b.Players.Count;
                if (k < 2) continue;
                foreach (var bp in b.Players)
                {
                    if (!Counted(bp.Slot)) continue;
                    data.PlayerBattles++;
                    bool won = bp.Slot == b.Winner;
                    var counts = bp.Cards.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
                    foreach (var (id, n) in counts)
                    {
                        if (!cards.TryGetValue(id, out var cs)) continue;
                        cs.Battle.Add(won, k);
                        int bucket = Math.Min(n, 3);
                        if (!cs.ByStacks.TryGetValue(bucket, out var st)) cs.ByStacks[bucket] = st = new WinTally();
                        st.Add(won, k);
                        if (won) { cs.Wins++; cs.WinFightSecSum += b.FightSec; }
                        cs.Shots += bp.Shots;
                        cs.Hits += bp.Hits;
                        cs.Dealt += bp.DamageDealt;
                        cs.Taken += bp.DamageTaken;
                        cs.SelfDamage += bp.SelfDamage;
                        cs.Healed += bp.Healed;
                        cs.HitsBlocked += bp.HitsBlocked;
                        cs.Kills += bp.Kills;
                        if (bp.DiedAt >= 0) cs.Deaths++;
                        cs.SurviveSec += bp.DiedAt >= 0 ? bp.DiedAt : b.FightSec;
                    }
                    var ids = counts.Keys.Where(cards.ContainsKey).OrderBy(x => x).ToList();
                    for (int i = 0; i < ids.Count; i++)
                        for (int j = i + 1; j < ids.Count; j++)
                        {
                            var key = (ids[i], ids[j]);
                            if (!pairs.TryGetValue(key, out var ps))
                                pairs[key] = ps = new PairStat { A = cards[ids[i]].Card, B = cards[ids[j]].Card };
                            ps.Both.Add(won, k);
                        }
                }
            }

            if (m.Complete && m.Winner >= 0)
            {
                int k = m.Players.Count(p => !p.Left || p.Slot == m.Winner);
                foreach (var p in m.Players)
                {
                    if (!Counted(p.Slot) || k < 2) continue;
                    foreach (int id in p.FinalCards.Distinct())
                        if (cards.TryGetValue(id, out var cs)) cs.Match.Add(p.Slot == m.Winner, k);
                }
            }

            foreach (var pk in m.Picks)
            {
                if (pk.TimedOut || pk.Offered.Count == 0) continue;
                if (f.Bots != null && pk.Bot != f.Bots) continue;
                data.Picks++;
                if (!pk.Bot) data.HumanPicks++;
                double chance = 1.0 / pk.Offered.Count;
                foreach (int id in pk.Offered.Distinct())
                {
                    if (!cards.TryGetValue(id, out var cs)) continue;
                    cs.Offered++;
                    cs.ExpectedPicks += chance;
                    if (!pk.Bot) { cs.OfferedHuman++; cs.ExpectedPicksHuman += chance; }
                }
                if (cards.TryGetValue(pk.Picked, out var chosen))
                {
                    chosen.Picked++;
                    chosen.PickRoundSum += pk.Round;
                    if (!pk.Bot) chosen.PickedHuman++;
                }
            }
        }

        data.AvgFightSec = fightN > 0 ? fightSum / fightN : double.NaN;
        data.Cards = cards.Values.OrderBy(c => c.Card.Id).ToList();
        foreach (var ps in pairs.Values)
        {
            double ra = cards[ps.A.Id].Battle.Ratio, rb = cards[ps.B.Id].Battle.Ratio;
            ps.Synergy = ps.Both.Ratio - (ra + rb - 1);
        }
        data.Pairs = pairs.Values.ToList();
        return data;
    }

    // ------------------------------------------------------------------ rendering

    private static string F(double v, string fmt = "0.00") =>
        double.IsNaN(v) || double.IsInfinity(v) ? "–" : v.ToString(fmt, CultureInfo.InvariantCulture);

    private static string Pct(double v) => double.IsNaN(v) ? "–" : (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Ratio(WinTally t) => t.N == 0 ? "–" : $"{F(t.Ratio)} ±{F(t.Ci95)}";

    /// <summary>Flags cards whose win ratio is clearly off balance (interval excludes 1.0 by a margin).</summary>
    public static string Verdict(WinTally t, int minSamples)
    {
        if (t.N < minSamples) return "few samples";
        double lo = t.Ratio - t.Ci95, hi = t.Ratio + t.Ci95;
        if (lo > 1.10) return "**strong**";
        if (lo > 1.0) return "strong?";
        if (hi < 0.90) return "**weak**";
        if (hi < 1.0) return "weak?";
        return "ok";
    }

    public static string Markdown(StatsReportData d)
    {
        var sb = new StringBuilder();
        var f = d.Filter;
        sb.AppendLine("# Brawler card balance report");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Players: {(f.Bots == null ? "all" : f.Bots == true ? "bots only" : "humans only")}, " +
                      $"mode: {f.Mode ?? "all"}, card table: {f.CardsHash ?? "all versions"}.");
        sb.AppendLine();
        sb.AppendLine($"- **{d.Matches}** matches ({d.CompleteMatches} complete), **{d.Battles}** battles ({d.Draws} draws), " +
                      $"{d.PlayerBattles} player-battles, avg fight {F(d.AvgFightSec, "0.0")}s");
        sb.AppendLine($"- **{d.Picks}** card picks ({d.HumanPicks} by humans)");
        if (d.CardHashes.Count > 1 && f.CardsHash == null)
            sb.AppendLine($"- ⚠ log mixes {d.CardHashes.Count} card-table versions ({string.Join(", ", d.CardHashes)}); use --cards to pick one");
        sb.AppendLine();
        sb.AppendLine("**Reading the numbers.** *Win ratio* = wins ÷ fair share (1/players per battle); 1.00 is balanced, " +
                      "±is a 95% interval. *Pick ratio* = picked ÷ what random picking would give (1.00 = indifferent); " +
                      "bots pick at random, so only the human pick ratio says what players like. *Win time* = average fight length " +
                      "of battles the holder won (lower = wins faster). Per-battle columns are averages over battles where the card was held.");
        sb.AppendLine();

        sb.AppendLine("## Cards");
        sb.AppendLine();
        sb.AppendLine("| Card | Held | Win ratio | Verdict | Match win | Win time | Dmg dealt | Dmg taken | Kills | Survive | Acc | Offered | Pick ratio | Human pick | Avg round |");
        sb.AppendLine("|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var c in d.Cards.OrderByDescending(c => double.IsNaN(c.Battle.Ratio) ? -1 : c.Battle.Ratio))
        {
            sb.AppendLine($"| {c.Card.Name} | {c.Battle.N} | {Ratio(c.Battle)} | {Verdict(c.Battle, f.MinSamples)} | {Ratio(c.Match)} | " +
                          $"{F(c.AvgWinSec, "0.0")}s | {F(c.Per(c.Dealt), "0")} | {F(c.Per(c.Taken), "0")} | {F(c.Per(c.Kills))} | " +
                          $"{F(c.Per(c.SurviveSec), "0.0")}s | {Pct(c.Accuracy)} | {c.Offered} | {F(c.PickRatio)} | " +
                          $"{(c.OfferedHuman > 0 ? F(c.PickRatioHuman) : "–")} | {F(c.AvgPickRound, "0.0")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Stacking (win ratio by copies held)");
        sb.AppendLine();
        sb.AppendLine("| Card | ×1 | ×2 | ×3+ |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var c in d.Cards)
        {
            string S(int n) => c.ByStacks.TryGetValue(n, out var t) ? $"{F(t.Ratio)} (n={t.N})" : "–";
            sb.AppendLine($"| {c.Card.Name} | {S(1)} | {S(2)} | {S(3)} |");
        }
        sb.AppendLine();

        var ranked = d.Pairs.Where(p => p.Both.N >= f.MinSamples && !double.IsNaN(p.Synergy)).ToList();
        sb.AppendLine($"## Synergies (pairs held together, n ≥ {f.MinSamples})");
        sb.AppendLine();
        sb.AppendLine("*Synergy* = the pair's win ratio minus what the two cards' separate effects predict (ratio A + ratio B − 1). " +
                      "Positive = the combo is stronger than its parts; check the ± before acting on it.");
        sb.AppendLine();
        if (ranked.Count == 0) sb.AppendLine("_Not enough data yet._");
        else
        {
            void Table(string title, IEnumerable<PairStat> rows)
            {
                sb.AppendLine($"**{title}**");
                sb.AppendLine();
                sb.AppendLine("| Pair | n | Pair win ratio | Synergy |");
                sb.AppendLine("|---|---:|---:|---:|");
                foreach (var p in rows)
                    sb.AppendLine($"| {p.A.Name} + {p.B.Name} | {p.Both.N} | {Ratio(p.Both)} | {(p.Synergy >= 0 ? "+" : "")}{F(p.Synergy)} |");
                sb.AppendLine();
            }
            Table("Best combos", ranked.OrderByDescending(p => p.Synergy).Take(10));
            Table("Worst combos (anti-synergy)", ranked.OrderBy(p => p.Synergy).Take(10));
            Table("Strongest pairs overall", ranked.OrderByDescending(p => p.Both.Ratio).Take(10));
        }
        return sb.ToString();
    }

    public static string CardsCsv(StatsReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,name,category,held,wins,win_ratio,win_ci95,match_n,match_win_ratio,avg_win_sec,dmg_dealt,dmg_taken,self_dmg,healed,kills,deaths,survive_sec,accuracy,hits_blocked,offered,picked,pick_ratio,offered_human,picked_human,pick_ratio_human,avg_pick_round");
        foreach (var c in d.Cards)
            sb.AppendLine(string.Join(",", c.Card.Id, Csv(c.Card.Name), c.Card.Category, c.Battle.N, c.Wins, F(c.Battle.Ratio, "0.000"), F(c.Battle.Ci95, "0.000"),
                c.Match.N, F(c.Match.Ratio, "0.000"), F(c.AvgWinSec), F(c.Per(c.Dealt)), F(c.Per(c.Taken)), F(c.Per(c.SelfDamage)), F(c.Per(c.Healed)),
                F(c.Per(c.Kills), "0.000"), F(c.Per(c.Deaths), "0.000"), F(c.Per(c.SurviveSec)), F(c.Accuracy, "0.000"), F(c.Per(c.HitsBlocked), "0.000"),
                c.Offered, c.Picked, F(c.PickRatio, "0.000"), c.OfferedHuman, c.PickedHuman, F(c.PickRatioHuman, "0.000"), F(c.AvgPickRound)).Replace("–", ""));
        return sb.ToString();
    }

    public static string PairsCsv(StatsReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("card_a,card_b,n,win_ratio,win_ci95,synergy");
        foreach (var p in d.Pairs.OrderBy(p => p.A.Id).ThenBy(p => p.B.Id))
            sb.AppendLine(string.Join(",", Csv(p.A.Name), Csv(p.B.Name), p.Both.N, F(p.Both.Ratio, "0.000"), F(p.Both.Ci95, "0.000"), F(p.Synergy, "0.000")).Replace("–", ""));
        return sb.ToString();
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
