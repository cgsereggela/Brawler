using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

/// <summary>
/// Team modes (2v2, 3v3, 4v4): two teams, the last team with anyone standing wins the battle, and every player on
/// it scores together. Size 0 is the usual free-for-all.
/// </summary>
public static class Teams
{
    public const int Count = 2;
    /// <summary>Lobby choices for the team size, in the order the mode button steps through them.</summary>
    public static readonly int[] Sizes = { 0, 2, 3, 4 };
    public static readonly string[] Names = { "Red", "Blue" };

    public static string ModeName(int size) => size <= 0 ? "Free-for-all" : $"{size}v{size}";

    /// <summary>The next lobby choice after <paramref name="size"/>, wrapping round to free-for-all.</summary>
    public static int NextSize(int size)
    {
        int i = System.Array.IndexOf(Sizes, size);
        return Sizes[(i + 1) % Sizes.Length];
    }

    public static int[] Counts(IEnumerable<int> teams)
    {
        var n = new int[Count];
        foreach (int t in teams)
            if (t >= 0 && t < Count) n[t]++;
        return n;
    }

    /// <summary>Where a newcomer goes: the team with fewer players (ties go to Red), or -1 when both are full.</summary>
    public static int Emptier(IEnumerable<int> teams, int size)
    {
        var n = Counts(teams);
        int t = n[1] < n[0] ? 1 : 0;
        return n[t] < size ? t : -1;
    }

    /// <summary>The other team, if it has room for one more; else the same team.</summary>
    public static int Swap(IEnumerable<int> teams, int current, int size)
    {
        int other = 1 - current;
        return Counts(teams)[other] < size ? other : current;
    }

    /// <summary>Fresh teams for players in slot order: alternating, so a full lobby splits evenly.</summary>
    public static int[] Deal(int players)
    {
        var t = new int[players];
        for (int i = 0; i < players; i++) t[i] = i % Count;
        return t;
    }

    /// <summary>Why a lobby can't start in this mode (shown under Start), or null when it can.</summary>
    public static string Problem(IEnumerable<int> teams, int size)
    {
        if (size <= 0) return null;
        var n = Counts(teams);
        if (n[0] == 0 || n[1] == 0) return "Both teams need at least one player.";
        if (n[0] > size || n[1] > size) return $"{ModeName(size)} allows {size} per team.";
        return null;
    }

    /// <summary>
    /// Whether the fighting is over given the teams of everyone still standing, and who won: the one team left, or -1
    /// for a draw (nobody standing).
    /// </summary>
    public static bool Settled(IEnumerable<int> aliveTeams, out int winner)
    {
        var left = aliveTeams.Distinct().ToList();
        winner = left.Count == 1 ? left[0] : -1;
        return left.Count <= 1;
    }
}
