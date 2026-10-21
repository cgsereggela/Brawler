using Brawler.Logic;
using Godot;

namespace Brawler.Game;

public enum Skyline { City, Mountains, Hills, Spires, Arches, Forest, Circuit }
public enum Motes { Bokeh, Embers, Bubbles, Snow, Fireflies, Leaves, Bits }
public enum Deco { Neon, Grass, Weeds, Mushrooms, Cracks, Icicles, Sprinkles, Torches, Ferns, Circuits }

/// <summary>A color scheme and dressing for one arena. Each battle picks one from its seed, so every peer agrees.</summary>
public sealed record ArenaTheme(
    string Name,
    Color SkyTop, Color SkyMid, Color SkyBottom,
    Color Block, Color Edge,
    Skyline Skyline, Motes Motes, Deco Deco,
    Color MoteA, Color MoteB)
{
    // Same order as Logic.ThemeId and ThemeRules.Names (the tests check the names line up).
    // Muted, painterly palettes: dusky skies, stone that sits close to the sky in hue, and one soft light color.
    public static readonly ArenaTheme[] All =
    {
        new("Neon Night", new("#0a0f1e"), new("#1f2e4c"), new("#5d7ea3"), new("#26324a"), new("#9fd6f2"),
            Skyline.City, Motes.Bokeh, Deco.Neon, new("#b6e2ff"), new("#e8d7a6")),
        new("Sunset Peaks", new("#221827"), new("#5e3a46"), new("#c99068"), new("#3e2b30"), new("#f0c58c"),
            Skyline.Mountains, Motes.Bokeh, Deco.Grass, new("#ffd9a3"), new("#f0a274")),
        new("Deep Reef", new("#061820"), new("#14404c"), new("#4f9a9a"), new("#1b3d45"), new("#a6eedb"),
            Skyline.Hills, Motes.Bubbles, Deco.Weeds, new("#d2fff4"), new("#94e6d1")),
        new("Toxic Grove", new("#09170f"), new("#1f3f27"), new("#6f9a58"), new("#223b27"), new("#cbea80"),
            Skyline.Spires, Motes.Fireflies, Deco.Mushrooms, new("#e6ff9c"), new("#a6f0b6")),
        new("Magma Core", new("#120a0a"), new("#3a1a16"), new("#a14a2c"), new("#2e1e1c"), new("#ffad5c"),
            Skyline.Mountains, Motes.Embers, Deco.Cracks, new("#ffc275"), new("#ff7a45")),
        new("Frost Bite", new("#0e1628"), new("#2d4870"), new("#9ab9d6"), new("#2e4262"), new("#e6f3ff"),
            Skyline.Spires, Motes.Snow, Deco.Icicles, new("#ffffff"), new("#cfe6ff")),
        new("Candy Clouds", new("#21152f"), new("#5e3a6e"), new("#d197b3"), new("#4a3158"), new("#fff0b8"),
            Skyline.Hills, Motes.Bokeh, Deco.Sprinkles, new("#fff2b0"), new("#b8f0ff")),
        new("Dungeon Depths", new("#0c0a12"), new("#262033"), new("#5f5264"), new("#2a2632"), new("#f2a65e"),
            Skyline.Arches, Motes.Embers, Deco.Torches, new("#ffbb6b"), new("#ffe2a0")),
        new("Elder Forest", new("#0f1912"), new("#2c4630"), new("#8aa477"), new("#2b3a28"), new("#d8e6a0"),
            Skyline.Forest, Motes.Leaves, Deco.Ferns, new("#e8c66a"), new("#c9784a")),
        new("Mainframe", new("#050d12"), new("#0f2a30"), new("#2f6a6a"), new("#13262c"), new("#5ff2c8"),
            Skyline.Circuit, Motes.Bits, Deco.Circuits, new("#7dffd8"), new("#ffcf5c")),
    };

    /// <summary>The look for a theme id; All is in ThemeId order.</summary>
    public static ArenaTheme Of(ThemeId id) => All[(int)id];

    /// <summary>The seed picks the theme in Logic (the map generator fits the arena extras to it); this is its look.</summary>
    public static ArenaTheme ForSeed(int seed) => Of(ThemeRules.ForSeed(seed));
}
