using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Logic;

namespace Brawler.Game;

/// <summary>
/// Keeps this machine's <see cref="Career"/>: when a match it played to the end finishes, its keyboard player's match,
/// win and kills are added and saved, and whatever that unlocked is listed for the match-over screen. Every peer counts
/// its own player from events it already gets, so nothing goes on the wire.
/// </summary>
public static class CareerBook
{
    /// <summary>Off for the automated checks, so test runs never earn anything.</summary>
    public static bool Enabled = true;

    public static Career Current { get; private set; } = new();

    /// <summary>What the last finished match unlocked (empty if nothing).</summary>
    public static readonly List<Unlock> JustUnlocked = new();

    private static int _kills;

    public static void Load() => Current = Profile.LoadCareer();

    /// <summary>A demo's own career, without touching the saved one.</summary>
    public static void Set(Career c) => Current = c.Clone();

    public static void MatchBegan()
    {
        _kills = 0;
        JustUnlocked.Clear();
    }

    public static void Died(GameManager g, int victim, int killer)
    {
        var me = g.LocalMatchPlayer;
        if (me != null && killer == me.Slot && victim != killer && !g.Allies(killer, victim)) _kills++;
    }

    public static void MatchEnded(GameManager g, int winner)
    {
        JustUnlocked.Clear();
        var me = g.LocalMatchPlayer;
        if (!Enabled || me == null || me.Left) return;
        var before = Current.Clone();
        bool won = winner >= 0 && (winner == me.Slot || g.Allies(winner, me.Slot));
        Current.Matches++;
        if (won) Current.Wins++;
        Current.Kills += _kills;
        if (g.Mode == GameMode.Survival) Current.BestWave = System.Math.Max(Current.BestWave, me.RoundWins);
        if (g.Mode == GameMode.GunGame && won) Current.GunGameWins++;
        JustUnlocked.AddRange(Unlocks.New(before, Current));
        Profile.SaveCareer(Current);
        Log.Info($"career: {Current.Encode()}{(JustUnlocked.Count > 0 ? $"; unlocked {string.Join(", ", JustUnlocked.Select(u => u.Name))}" : "")}");
    }
}
