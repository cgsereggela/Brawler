using System.Collections.Generic;

namespace Brawler.Logic;

/// <summary>Silly two-word player names ("Turbo Waffle") for the README captures, short enough for the name box.</summary>
public static class FunNames
{
    public const int MaxLength = 12; // the menu's name box

    private static readonly string[] First =
        { "Turbo", "Sneaky", "Wobbly", "Spicy", "Grumpy", "Soggy", "Mighty", "Tiny", "Feral", "Cosmic", "Jolly", "Salty", "Dizzy", "Fancy", "Lucky", "Rusty" };
    private static readonly string[] Second =
        { "Waffle", "Badger", "Noodle", "Goblin", "Pickle", "Walrus", "Muffin", "Cactus", "Llama", "Toast", "Gecko", "Bean", "Yeti", "Moose", "Duck", "Taco" };

    /// <summary><paramref name="count"/> names with no word used twice.</summary>
    public static List<string> Pick(Rng rng, int count)
    {
        var names = new List<string>();
        var used = new HashSet<string>();
        for (int tries = 0; names.Count < count && tries < 1000; tries++)
        {
            string a = First[rng.Range(0, First.Length - 1)], b = Second[rng.Range(0, Second.Length - 1)];
            string name = $"{a} {b}";
            if (name.Length > MaxLength || used.Contains(a) || used.Contains(b)) continue;
            used.Add(a);
            used.Add(b);
            names.Add(name);
        }
        return names;
    }
}
