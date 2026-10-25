using System;
using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

/// <summary>What this machine's main player has done over every match they finished: the milestones cosmetics unlock at.</summary>
public sealed class Career
{
    public int Matches, Wins, Kills, BestWave, GunGameWins;

    /// <summary>Text form for the career file, e.g. "matches=12;wins=3;kills=40;wave=6;gungame=1". Unknown keys are ignored.</summary>
    public string Encode() => $"matches={Matches};wins={Wins};kills={Kills};wave={BestWave};gungame={GunGameWins}";

    public static Career Decode(string s)
    {
        var c = new Career();
        foreach (var kv in (s ?? "").Split(';'))
        {
            var parts = kv.Split('=');
            if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out int v) || v < 0) continue;
            switch (parts[0].Trim())
            {
                case "matches": c.Matches = v; break;
                case "wins": c.Wins = v; break;
                case "kills": c.Kills = v; break;
                case "wave": c.BestWave = v; break;
                case "gungame": c.GunGameWins = v; break;
            }
        }
        return c;
    }

    public Career Clone() => (Career)MemberwiseClone();
}

/// <summary>One cosmetic option that has to be earned, and the milestone that earns it.</summary>
public sealed record Unlock(LookPart Part, int Option, string Goal, Func<Career, bool> Met)
{
    public string Name => Appearance.OptionNameOf(Part, Option);
}

/// <summary>
/// The cosmetics players earn: hats, trails and eyes behind milestones in their <see cref="Career"/>. Everything else is free
/// from the start. Purely visual, like every look option. A look with an option not yet earned is never saved or sent.
/// </summary>
public static class Unlocks
{
    public static readonly Unlock[] All =
    {
        new(LookPart.Trail, 1, "Play 3 matches", c => c.Matches >= 3),          // Sparks
        new(LookPart.Trail, 2, "Win 3 matches", c => c.Wins >= 3),              // Bubbles
        new(LookPart.Hat, 11, "Play 10 matches", c => c.Matches >= 10),         // Wizard
        new(LookPart.Hat, 14, "Take out 50 players", c => c.Kills >= 50),       // Pirate
        new(LookPart.Trail, 4, "Take out 100 players", c => c.Kills >= 100),    // Flames
        new(LookPart.Hat, 12, "Win 10 matches", c => c.Wins >= 10),             // Laurel
        new(LookPart.Hat, 13, "Take out 200 players", c => c.Kills >= 200),     // Horns
        new(LookPart.Trail, 5, "Clear wave 5 in Survival", c => c.BestWave >= 5), // Stars
        new(LookPart.Trail, 3, "Win a Gun Game", c => c.GunGameWins >= 1),       // Rainbow
        new(LookPart.Eyes, 9, "Lose 10 matches", c => c.Matches - c.Wins >= 10), // Crying
        new(LookPart.Eyes, 10, "Play 25 matches", c => c.Matches >= 25),        // Nerdy Glasses
    };

    /// <summary>The milestone behind an option, or null if it's free.</summary>
    public static Unlock For(LookPart part, int option) => All.FirstOrDefault(u => u.Part == part && u.Option == option);

    public static bool Has(Career c, LookPart part, int option) => For(part, option) is not { } u || u.Met(c);

    /// <summary>Every part of the look is earned.</summary>
    public static bool Allowed(Career c, Appearance look) => Enum.GetValues<LookPart>().All(p => Has(c, p, look[p]));

    /// <summary>The look with anything not yet earned taken off (back to the part's first option).</summary>
    public static Appearance Earned(Career c, Appearance look)
    {
        var a = look.Clone();
        foreach (LookPart p in Enum.GetValues<LookPart>())
            if (!Has(c, p, a[p])) a[p] = 0;
        return a;
    }

    /// <summary>What reaching <paramref name="after"/> from <paramref name="before"/> unlocked, in list order.</summary>
    public static List<Unlock> New(Career before, Career after) => All.Where(u => !u.Met(before) && u.Met(after)).ToList();

    /// <summary>Whether an option is one anyone may wear at random (bots, the Randomize button): free ones only.</summary>
    public static bool Free(LookPart part, int option) => For(part, option) == null;
}
