using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

/// <summary>
/// A card synergy: hold one card from <see cref="A"/> and one from <see cref="B"/> and you also get <see cref="Mods"/>.
/// Combos come straight from the cards a player holds, so every peer works them out the same way with nothing extra
/// sent over the network.
/// </summary>
public sealed class ComboDef
{
    public int Id { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public int[] A { get; init; } = [];
    public int[] B { get; init; } = [];
    public StatMod[] Mods { get; init; } = [];

    public bool ActiveFor(IReadOnlyCollection<int> cards) => A.Any(cards.Contains) && B.Any(cards.Contains);
}

/// <summary>Every combo. Ids are indices into <see cref="All"/>; only ever add to the end.</summary>
public static class Combos
{
    public static readonly ComboDef[] All =
    {
        // Bouncy / Ricochet + Explosive
        new() { Id = 0, Name = "Bounce Bombs", Description = "Bullets explode on every bounce", A = [1, 39], B = [6],
            Mods = [A(Stat.BounceBlast, 1)] },
        // Sticky Mines + Explosive
        new() { Id = 1, Name = "Minefield", Description = "Mines last 3 s longer\n+30% explosion size", A = [62], B = [6],
            Mods = [A(Stat.Sticky, 3), M(Stat.ExplosionRadius, 1.3f)] },
        // Vampire + Leech Field
        new() { Id = 2, Name = "Blood Pact", Description = "+15% lifesteal\n+15 HP/s leech", A = [14], B = [69],
            Mods = [A(Stat.Lifesteal, 0.15f), A(Stat.Leech, 15)] },
        // Sniper + Marksman / High Velocity
        new() { Id = 3, Name = "Deadeye", Description = "Bullets pierce 1 more player\n+20% damage", A = [30], B = [40, 35],
            Mods = [A(Stat.Pierce, 1), M(Stat.Damage, 1.2f)] },
        // Double Jump / Cloud Walker / Parkour + Feather / Rocket Boots
        new() { Id = 4, Name = "Moon Boots", Description = "+1 mid-air jump", A = [18, 19, 27], B = [8, 22],
            Mods = [A(Stat.AirJumps, 1)] },
        // Shotgun / Hail Storm + Burst
        new() { Id = 5, Name = "Lead Storm", Description = "-20% fire cooldown", A = [10, 33], B = [12],
            Mods = [M(Stat.FireCooldown, 0.8f)] },
        // Homing / Mind Control + Splitter
        new() { Id = 6, Name = "Seeker Swarm", Description = "Bullets and their splits\nhome in much harder", A = [5, 68], B = [4],
            Mods = [A(Stat.Homing, 2.5f)] },
        // Reflector + Spiked Shield / Bulwark
        new() { Id = 7, Name = "Mirror Wall", Description = "+50% block duration", A = [70], B = [81, 72],
            Mods = [M(Stat.BlockDuration, 1.5f)] },
        // Berserk + Adrenaline
        new() { Id = 8, Name = "Rampage", Description = "+15% move speed\n+15% lifesteal", A = [64], B = [54],
            Mods = [M(Stat.MoveSpeed, 1.15f), A(Stat.Lifesteal, 0.15f)] },
        // Boomerang + Pierce
        new() { Id = 9, Name = "Returning Blade", Description = "Bullets pierce 1 more player\n+20% damage", A = [57], B = [3],
            Mods = [A(Stat.Pierce, 1), M(Stat.Damage, 1.2f)] },
        // Tank / Giant + Regeneration
        new() { Id = 10, Name = "Troll Blood", Description = "+5 HP per second regen", A = [7, 43], B = [46],
            Mods = [A(Stat.Regen, 5)] },
        // Quick Guard + Momentum
        new() { Id = 11, Name = "Iron Curtain", Description = "-20% block cooldown", A = [73], B = [77],
            Mods = [M(Stat.BlockCooldown, 0.8f)] },
    };

    public static ComboDef Get(int id) => id >= 0 && id < All.Length ? All[id] : null;

    /// <summary>The combos these cards unlock, in id order. Holding a card twice doesn't unlock anything twice.</summary>
    public static List<ComboDef> Active(IEnumerable<int> cards)
    {
        var held = cards as IReadOnlyCollection<int> ?? cards.ToHashSet();
        return All.Where(c => c.ActiveFor(held)).ToList();
    }

    /// <summary>The combos that taking <paramref name="card"/> would unlock on top of <paramref name="held"/>.</summary>
    public static List<ComboDef> Unlocks(IEnumerable<int> held, int card)
    {
        var before = held.ToHashSet();
        if (before.Contains(card)) return new List<ComboDef>();
        var after = new HashSet<int>(before) { card };
        return All.Where(c => !c.ActiveFor(before) && c.ActiveFor(after)).ToList();
    }

    /// <summary>Every card that is part of some combo.</summary>
    public static bool InAny(int card) => All.Any(c => c.A.Contains(card) || c.B.Contains(card));

    private static StatMod A(Stat s, float v) => new(s, ModOp.Add, v);
    private static StatMod M(Stat s, float v) => new(s, ModOp.Mul, v);
}
