using System;
using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

/// <summary>What the host counts for each player over a match, for the end-of-match awards.</summary>
public enum AwardStat { Kills, SelfKills, Falls, Shots, Hits, DamageDealt, DamageTaken, Blocks, HitsBlocked, Healed, Airtime, Count }

/// <summary>One award handed out: a title for a slot, with the number that earned it.</summary>
public readonly record struct Award(int Slot, int Id, float Value)
{
    public AwardDef Def => Awards.Get(Id);
    public string Title => Def?.Title ?? "?";
    public string Blurb => Def == null ? "" : string.Format(System.Globalization.CultureInfo.InvariantCulture, Def.Blurb, Value);
}

/// <param name="Score">The number this award ranks by, or null when the player doesn't qualify at all.</param>
/// <param name="Highest">True: goes to the top score. False: goes to the bottom one.</param>
/// <param name="Min">The best score must reach this (a "most" award) to be worth a title.</param>
/// <param name="Gap">A "least" award needs someone else at least this far above the bottom, so it means something.</param>
/// <param name="Cap">A "least" award only goes to a score at or under this.</param>
public sealed record AwardDef(int Id, string Title, string Blurb, Func<AwardBook, int, float?> Score, bool Highest, float Min = 0, float Gap = 0, float Cap = float.MaxValue);

public static class Awards
{
    public const int MaxShown = 4;

    // Ids are stable: they go over the network. Add new ones at the end.
    public static readonly AwardDef[] All =
    {
        new(0, "Own Worst Enemy", "{0:0} self-inflicted exits", (b, s) => b.Get(s, AwardStat.SelfKills), true, Min: 2),
        new(1, "Frequent Flyer", "{0:0.0}s in the air in one go", (b, s) => b.Get(s, AwardStat.Airtime), true, Min: 3),
        new(2, "Brick Wall", "blocked {0:0} hits", (b, s) => b.Get(s, AwardStat.HitsBlocked), true, Min: 3),
        new(3, "Blocking Is For Cowards", "blocked just {0:0}x all match", (b, s) => b.Get(s, AwardStat.Blocks), false, Gap: 6, Cap: 4),
        new(4, "Trigger Happy", "fired {0:0} bullets", (b, s) => b.Get(s, AwardStat.Shots), true, Min: 60),
        new(5, "Stormtrooper", "hit {0:0}% of their shots",
            (b, s) => b.Get(s, AwardStat.Shots) >= 25 ? 100f * b.Get(s, AwardStat.Hits) / b.Get(s, AwardStat.Shots) : null, false, Gap: 10),
        new(6, "Gravity Tester", "fell out of the arena {0:0} times", (b, s) => b.Get(s, AwardStat.Falls), true, Min: 2),
        new(7, "Punching Bag", "soaked up {0:0} damage", (b, s) => b.Get(s, AwardStat.DamageTaken), true, Min: 300),
        new(8, "Pacifist", "dealt the least damage ({0:0})", (b, s) => b.Get(s, AwardStat.DamageDealt), false, Gap: 150),
        new(9, "Self Care", "healed {0:0} HP", (b, s) => b.Get(s, AwardStat.Healed), true, Min: 60),
        new(10, "Menace", "{0:0} kills", (b, s) => b.Get(s, AwardStat.Kills), true, Min: 3),
        new(11, "Untouchable", "took the least damage ({0:0})", (b, s) => b.Get(s, AwardStat.DamageTaken), false, Gap: 150),
    };

    public static AwardDef Get(int id) => id >= 0 && id < All.Length ? All[id] : null;

    /// <summary>
    /// Up to <see cref="MaxShown"/> awards for these slots. Each award needs one clear holder (a tie gives nobody the
    /// title). Everyone gets one before anyone gets a second, and nobody gets more than two; which awards make the cut
    /// is shuffled, so matches don't always show the same ones.
    /// </summary>
    public static List<Award> Pick(AwardBook book, IEnumerable<int> slots, Rng rng)
    {
        var players = slots.Distinct().ToList();
        var candidates = new List<Award>();
        if (players.Count < 2) return candidates;
        foreach (var def in All)
        {
            var scores = players.Select(s => (slot: s, score: def.Score(book, s))).Where(x => x.score.HasValue).Select(x => (x.slot, score: x.score.Value)).ToList();
            if (scores.Count < 2) continue;
            var ordered = def.Highest ? scores.OrderByDescending(x => x.score).ToList() : scores.OrderBy(x => x.score).ToList();
            var best = ordered[0];
            if (Math.Abs(ordered[1].score - best.score) < 0.001f) continue; // tied
            if (def.Highest && best.score < def.Min) continue;
            if (!def.Highest && (ordered[^1].score - best.score < def.Gap || best.score > def.Cap)) continue;
            candidates.Add(new Award(best.slot, def.Id, best.score));
        }
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = rng.Range(0, i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        var picked = new List<Award>();
        for (int perPlayer = 1; perPlayer <= 2; perPlayer++)
            foreach (var c in candidates)
            {
                if (picked.Count >= MaxShown) break;
                if (picked.Contains(c) || picked.Count(p => p.Slot == c.Slot) >= perPlayer) continue;
                picked.Add(c);
            }
        return picked;
    }
}

/// <summary>Per-slot tallies over one match.</summary>
public sealed class AwardBook
{
    public const int Slots = 8;
    private readonly float[,] _v = new float[Slots, (int)AwardStat.Count];

    public void Reset() => Array.Clear(_v);

    public float Get(int slot, AwardStat s) => Ok(slot) ? _v[slot, (int)s] : 0;

    public void Add(int slot, AwardStat s, float amount = 1)
    {
        if (Ok(slot)) _v[slot, (int)s] += amount;
    }

    /// <summary>Keeps the largest value seen (for records like the longest time in the air).</summary>
    public void Max(int slot, AwardStat s, float value)
    {
        if (Ok(slot) && value > _v[slot, (int)s]) _v[slot, (int)s] = value;
    }

    private static bool Ok(int slot) => slot >= 0 && slot < Slots;
}
