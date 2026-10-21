namespace Brawler.Logic;

/// <summary>The arena themes, in the same order as Game.ArenaTheme.All (which holds their colors and dressing).</summary>
public enum ThemeId { NeonNight, SunsetPeaks, DeepReef, ToxicGrove, MagmaCore, FrostBite, CandyClouds, DungeonDepths, ElderForest, Mainframe }

/// <summary>
/// Which arena extras suit a theme, and how often. Pools roll <see cref="PoolChance"/> and then pick magma or poison
/// by weight (a zero weight forbids that kind); sawblades and barrels roll their own chances. Crates, glass and
/// moving platforms fit everywhere, so they are not listed.
/// </summary>
public readonly record struct FeatureRules(float PoolChance, float MagmaWeight, float PoisonWeight, float SawChance, float BarrelChance)
{
    public bool Allows(HazardKind kind) => PoolChance > 0 && (kind == HazardKind.Magma ? MagmaWeight : PoisonWeight) > 0;
    public bool AllowsSaws => SawChance > 0;
}

/// <summary>
/// Picks each arena's theme from its seed, so the map generator can dress the arena to match and every peer agrees.
/// </summary>
public static class ThemeRules
{
    public static readonly string[] Names =
        { "Neon Night", "Sunset Peaks", "Deep Reef", "Toxic Grove", "Magma Core", "Frost Bite", "Candy Clouds",
          "Dungeon Depths", "Elder Forest", "Mainframe" };

    //                                   pools  magma poison  saws  barrels
    private static readonly FeatureRules[] Table =
    {
        /* Neon Night: a factory town, saws and sludge */ new(0.55f, 0.5f, 1f, 0.65f, 0.6f),
        /* Sunset Peaks: a bit of everything */            new(0.6f,  1f,   1f, 0.5f,  0.6f),
        /* Deep Reef: no saws or magma underwater */       new(0.7f,  0f,   1f, 0f,    0.75f),
        /* Toxic Grove: poison everywhere, no magma */     new(0.85f, 0f,   1f, 0.4f,  0.7f),
        /* Magma Core: magma, no poison, lots of fire */   new(0.85f, 1f,   0f, 0.5f,  0.8f),
        /* Frost Bite: no pools at all, more saws */       new(0f,    0f,   0f, 0.7f,  0.6f),
        /* Candy Clouds: sour goo, few saws */             new(0.5f,  0f,   1f, 0.35f, 0.5f),
        /* Dungeon Depths: slime pits, traps, a little lava */ new(0.55f, 0.3f, 1f, 0.6f, 0.7f),
        /* Elder Forest: swamp pools, sawmill blades */  new(0.6f,  0f,   1f, 0.45f, 0.6f),
        /* Mainframe: fan saws, coolant leaks, no magma */ new(0.35f, 0f,   1f, 0.75f, 0.5f),
    };

    public static int Count => Names.Length;

    public static ThemeId ForSeed(int seed) => (ThemeId)((int)((uint)(seed * 2654435761u) >> 8) % Count);

    public static FeatureRules Rules(ThemeId theme) => Table[(int)theme];

    public static string Name(ThemeId theme) => Names[(int)theme];
}
