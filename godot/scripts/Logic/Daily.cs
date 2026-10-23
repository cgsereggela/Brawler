using System;
using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

/// <summary>
/// The daily challenge: one arena, mode and starting hand per UTC day, the same for everyone (it comes only from the
/// date), played against three Hard bots. <see cref="DailyBest"/> keeps your best result of the day.
/// </summary>
public sealed class Daily
{
    public const int Bots = 3;
    public const int StartingCardCount = 3;
    /// <summary>The modes a day can be: every free-for-all mode that keeps the hand you start with.</summary>
    public static readonly GameMode[] Modes = { GameMode.Elimination, GameMode.KingOfTheHill, GameMode.Headhunter };

    /// <summary>The day as yyyymmdd (UTC), which is also what the saved best is filed under.</summary>
    public int Day { get; private init; }
    public ulong Seed { get; private init; }
    /// <summary>The seed of the day's arena (a generated one, for four players).</summary>
    public int ArenaSeed { get; private init; }
    public GameMode Mode { get; private init; }
    /// <summary>Built-in common cards only, so a custom card pack can't change anyone's day.</summary>
    public List<int> StartingCards { get; private init; }

    public static int DayOf(DateTime utc) => utc.Year * 10000 + utc.Month * 100 + utc.Day;

    public static Daily For(DateTime utc) => For(DayOf(utc));

    public static Daily For(int day)
    {
        // Spread nearby days apart (SplitMix-style), so consecutive days don't start their RNG on neighbouring states.
        ulong seed = (ulong)day * 0x9E3779B97F4A7C15UL;
        seed = (seed ^ (seed >> 31)) * 0xBF58476D1CE4E5B9UL;
        var rng = new Rng(seed);
        int arena = (int)(rng.NextUInt() & 0x7FFFFFFF);
        var mode = Modes[rng.Range(0, Modes.Length - 1)];
        var pool = CardRegistry.All.Where(c => !CardRegistry.IsCustom(c.Id) && !c.IsRare && !c.IsCurse).Select(c => c.Id).ToList();
        var cards = new List<int>();
        while (cards.Count < StartingCardCount && pool.Count > 0)
        {
            int i = rng.Range(0, pool.Count - 1);
            cards.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return new Daily { Day = day, Seed = seed, ArenaSeed = arena, Mode = mode, StartingCards = cards };
    }

    public string DateText => $"{Day / 10000:D4}-{Day / 100 % 100:D2}-{Day % 100:D2}";

    /// <summary>
    /// The day's arena, played every battle: the first generated four-player arena from <see cref="ArenaSeed"/> on that didn't
    /// need the fallback layout, kept as a design so every battle (and every player) gets exactly it.
    /// </summary>
    public MapDesign Arena()
    {
        for (int s = ArenaSeed; ; s++)
        {
            var map = MapGenerator.Generate(s, Bots + 1);
            if (map.UsedFallback) continue;
            var d = MapDesign.From(map);
            d.Name = $"Daily {DateText}";
            return d;
        }
    }
}

/// <summary>How a daily challenge went: won or not, rounds won, kills. Better is a win, then more rounds, then more kills.</summary>
public readonly record struct DailyResult(bool Won, int Rounds, int Kills) : IComparable<DailyResult>
{
    public int CompareTo(DailyResult o) =>
        Won != o.Won ? Won.CompareTo(o.Won) : Rounds != o.Rounds ? Rounds.CompareTo(o.Rounds) : Kills.CompareTo(o.Kills);

    public override string ToString() => $"{(Won ? "won" : "lost")}, {Rounds} round{(Rounds == 1 ? "" : "s")}, {Kills} kill{(Kills == 1 ? "" : "s")}";
}

/// <summary>The best daily result kept in the profile: only today's counts, a new day starts empty.</summary>
public static class DailyBest
{
    /// <summary>Text form for the profile, e.g. "day=20261023;won=1;rounds=5;kills=12".</summary>
    public static string Encode(int day, DailyResult r) => $"day={day};won={(r.Won ? 1 : 0)};rounds={r.Rounds};kills={r.Kills}";

    /// <summary>The stored best if it is for <paramref name="day"/>, else null (an older day, or nothing readable).</summary>
    public static DailyResult? Decode(string s, int day)
    {
        int d = -1, won = 0, rounds = 0, kills = 0;
        foreach (var kv in (s ?? "").Split(';'))
        {
            var parts = kv.Split('=');
            if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out int v) || v < 0) continue;
            switch (parts[0].Trim())
            {
                case "day": d = v; break;
                case "won": won = v; break;
                case "rounds": rounds = v; break;
                case "kills": kills = v; break;
            }
        }
        return d == day ? new DailyResult(won != 0, rounds, kills) : null;
    }

    /// <summary>What to store after a run: the run if it beats the stored best (or the stored one is another day's).</summary>
    public static string Merge(string stored, int day, DailyResult run) =>
        Decode(stored, day) is { } best && best.CompareTo(run) >= 0 ? stored : Encode(day, run);
}
