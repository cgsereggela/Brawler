using Brawler.Logic;
using Godot;

namespace Brawler.Game;

public enum Skyline { City, Mountains, Hills, Spires }
public enum Motes { Bokeh, Embers, Bubbles, Snow, Fireflies }
public enum Deco { Neon, Grass, Weeds, Mushrooms, Cracks, Icicles, Sprinkles }

/// <summary>A color scheme and dressing for one arena. Each battle picks one from its seed, so every peer agrees.</summary>
public sealed record ArenaTheme(
    string Name,
    Color SkyTop, Color SkyMid, Color SkyBottom,
    Color Block, Color Edge,
    Skyline Skyline, Motes Motes, Deco Deco,
    Color MoteA, Color MoteB)
{
    // Same order as Logic.ThemeId and ThemeRules.Names (the tests check the names line up).
    public static readonly ArenaTheme[] All =
    {
        new("Neon Night", new("#0e0a2e"), new("#3a1470"), new("#c2307a"), new("#3b2b85"), new("#36f3ff"),
            Skyline.City, Motes.Bokeh, Deco.Neon, new("#ff4fd8"), new("#36f3ff")),
        new("Sunset Peaks", new("#2b1a5c"), new("#b8457a"), new("#ffa45e"), new("#5e2c63"), new("#ffd166"),
            Skyline.Mountains, Motes.Bokeh, Deco.Grass, new("#ffd166"), new("#ff7a59")),
        new("Deep Reef", new("#03182e"), new("#0b4a78"), new("#1cc0cf"), new("#17587a"), new("#6dffd2"),
            Skyline.Hills, Motes.Bubbles, Deco.Weeds, new("#bff9ff"), new("#6dffd2")),
        new("Toxic Grove", new("#06170f"), new("#174a2c"), new("#7bd94a"), new("#22603b"), new("#d4ff3d"),
            Skyline.Spires, Motes.Fireflies, Deco.Mushrooms, new("#e8ff6b"), new("#6bffb0")),
        new("Magma Core", new("#140405"), new("#55100f"), new("#f0552a"), new("#5a2219"), new("#ffb13b"),
            Skyline.Mountains, Motes.Embers, Deco.Cracks, new("#ffb13b"), new("#ff5a2a")),
        new("Frost Bite", new("#0b1538"), new("#2f57a8"), new("#9be0ff"), new("#3d64b0"), new("#eafaff"),
            Skyline.Spires, Motes.Snow, Deco.Icicles, new("#ffffff"), new("#bfe8ff")),
        new("Candy Clouds", new("#3a1878"), new("#a63fc4"), new("#ff86bd"), new("#7442c4"), new("#fff27a"),
            Skyline.Hills, Motes.Bokeh, Deco.Sprinkles, new("#fff27a"), new("#7af7ff")),
    };

    /// <summary>The look for a theme id; All is in ThemeId order.</summary>
    public static ArenaTheme Of(ThemeId id) => All[(int)id];

    /// <summary>The seed picks the theme in Logic (the map generator fits the arena extras to it); this is its look.</summary>
    public static ArenaTheme ForSeed(int seed) => Of(ThemeRules.ForSeed(seed));
}
