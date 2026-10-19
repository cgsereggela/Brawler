using System;
using System.Collections.Generic;

namespace Brawler.Game;

/// <summary>Visual themes a card adds to its holder's bullets, hits and kills.</summary>
[Flags]
public enum Flavor
{
    None = 0,
    Fire = 1 << 0,
    Homing = 1 << 1,
    Bouncy = 1 << 2,
    Pierce = 1 << 3,
    Split = 1 << 4,
    Big = 1 << 5,
    Vampire = 1 << 6,
    Glass = 1 << 7,
    Shotgun = 1 << 8,
    Feather = 1 << 9,
    Burst = 1 << 10,
}

/// <summary>
/// How flashy one player's effects are: <see cref="Power"/> grows with every card they hold (so late-match fights
/// get louder), and each card can add a <see cref="Flavor"/> (embers for Explosive, sparkles for Homing, ...).
/// Built from the card list, which every peer knows, so all screens show the same effects. Purely cosmetic.
/// </summary>
public sealed class FxStyle
{
    public static readonly FxStyle None = new(0, Flavor.None);

    // Card id → flavor. A card without an entry still adds power. Add new cards here to give them a look.
    private static readonly Dictionary<int, Flavor> CardFlavors = new()
    {
        [1] = Flavor.Bouncy, [2] = Flavor.Big, [3] = Flavor.Pierce, [4] = Flavor.Split, [5] = Flavor.Homing,
        [6] = Flavor.Fire, [8] = Flavor.Feather, [10] = Flavor.Shotgun, [12] = Flavor.Burst, [14] = Flavor.Vampire,
        [16] = Flavor.Glass,
    };

    public int Power { get; }
    public Flavor Flavors { get; }

    public FxStyle(int power, Flavor flavors)
    {
        Power = power;
        Flavors = flavors;
    }

    public static FxStyle From(IEnumerable<int> cards)
    {
        int n = 0;
        var f = Flavor.None;
        foreach (int id in cards)
        {
            n++;
            if (CardFlavors.TryGetValue(id, out var fl)) f |= fl;
        }
        return n == 0 ? None : new FxStyle(n, f);
    }

    public bool Has(Flavor f) => (Flavors & f) != 0;

    /// <summary>0 with no cards, up to 5 with ten or more.</summary>
    public int Tier => Power switch { 0 => 0, <= 2 => 1, <= 4 => 2, <= 6 => 3, <= 9 => 4, _ => 5 };

    /// <summary>A multiplier for particle counts and sizes: 1 with no cards, about 2.5 at the top tier.</summary>
    public float Intensity => 1f + Tier * 0.3f;
}
