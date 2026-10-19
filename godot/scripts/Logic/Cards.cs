using System.Collections.Generic;

namespace Brawler.Logic;

public enum CardCategory { Bullet, Body, Weapon, Chaos }

/// <summary>
/// A card is a list of stat modifiers plus an optional effect id.
/// Effect ids map to hook implementations on the Godot side (see Game/CardEffects.cs).
/// </summary>
public sealed class CardDef
{
    public int Id { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public CardCategory Category { get; init; }
    public StatMod[] Mods { get; init; } = [];
    public string EffectId { get; init; }
}

public static class CardRegistry
{
    public static readonly List<CardDef> All = new();
    private static readonly Dictionary<int, CardDef> ById = new();

    static CardRegistry()
    {
        // To add a card: append a new Add(...) line. Ids must stay stable across versions of a session.
        Add(1, "Bouncy", "+2 bullet bounces\n-10% damage", CardCategory.Bullet,
            A(Stat.Bounces, 2), M(Stat.Damage, 0.9f));
        Add(2, "Big Bullet", "Huge bullets\n+40% damage\n-20% bullet speed", CardCategory.Bullet,
            M(Stat.BulletRadius, 2.2f), M(Stat.Damage, 1.4f), M(Stat.BulletSpeed, 0.8f));
        Add(3, "Pierce", "Bullets pass through\n1 extra player", CardCategory.Bullet,
            A(Stat.Pierce, 1), M(Stat.Damage, 0.9f));
        Add(4, "Splitter", "Bullets split into 3\non impact\n+15% fire cooldown", CardCategory.Bullet,
            A(Stat.SplitCount, 3), M(Stat.FireCooldown, 1.15f));
        Add(5, "Homing", "Bullets curve toward\nenemies\n-15% bullet speed", CardCategory.Bullet,
            A(Stat.Homing, 3.5f), M(Stat.BulletSpeed, 0.85f));
        Add(6, "Explosive", "Bullets explode\n-20% damage\n+20% fire cooldown", CardCategory.Bullet,
            A(Stat.ExplosionRadius, 28), M(Stat.Damage, 0.8f), M(Stat.FireCooldown, 1.2f));
        Add(7, "Tank", "+60 HP\nBigger body\n-15% move speed", CardCategory.Body,
            A(Stat.MaxHp, 60), M(Stat.Size, 1.25f), M(Stat.MoveSpeed, 0.85f));
        Add(8, "Feather", "Low gravity for you\nand your bullets", CardCategory.Body,
            M(Stat.Gravity, 0.6f), M(Stat.JumpVelocity, 0.95f), M(Stat.BulletGravity, 0.4f));
        Add(9, "Speedy", "+30% move speed\nSmaller body\n-15% HP", CardCategory.Body,
            M(Stat.MoveSpeed, 1.3f), M(Stat.Size, 0.85f), M(Stat.MaxHp, 0.85f));
        Add(10, "Shotgun", "+3 bullets per shot\n-45% damage\n+30% fire cooldown", CardCategory.Weapon,
            A(Stat.Projectiles, 3), A(Stat.Spread, 0.35f), M(Stat.Damage, 0.55f), M(Stat.FireCooldown, 1.3f));
        Add(11, "Quick Reload", "-50% reload time", CardCategory.Weapon,
            M(Stat.ReloadTime, 0.5f));
        Add(12, "Burst", "Fire 3-round bursts\n-30% damage", CardCategory.Weapon,
            A(Stat.BurstCount, 2), M(Stat.Damage, 0.7f));
        Add(13, "Big Mag", "+3 ammo\n+15% reload time", CardCategory.Weapon,
            A(Stat.Ammo, 3), M(Stat.ReloadTime, 1.15f));
        Add(14, "Vampire", "Heal 35% of damage dealt\n-15% HP", CardCategory.Chaos,
            A(Stat.Lifesteal, 0.35f), M(Stat.MaxHp, 0.85f));
        Add(15, "Blink", "Blocking teleports you\ntoward your cursor\n-15% block cooldown", CardCategory.Chaos,
            "blink", A(Stat.TeleportOnBlock, 1), M(Stat.BlockCooldown, 0.85f));
        Add(16, "Glass Cannon", "+60% damage\n-30% HP", CardCategory.Chaos,
            M(Stat.Damage, 1.6f), M(Stat.MaxHp, 0.7f));
    }

    public static CardDef Get(int id) => ById.TryGetValue(id, out var c) ? c : null;

    public static IEnumerable<CardDef> Resolve(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            if (ById.TryGetValue(id, out var c)) yield return c;
    }

    /// <summary>Picks <paramref name="count"/> distinct random cards.</summary>
    public static List<int> RollOptions(Rng rng, int count)
    {
        var pool = new List<int>();
        foreach (var c in All) pool.Add(c.Id);
        var result = new List<int>();
        while (result.Count < count && pool.Count > 0)
        {
            int i = rng.Range(0, pool.Count - 1);
            result.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return result;
    }

    private static StatMod A(Stat s, float v) => new(s, ModOp.Add, v);
    private static StatMod M(Stat s, float v) => new(s, ModOp.Mul, v);

    private static void Add(int id, string name, string desc, CardCategory cat, params StatMod[] mods)
        => Add(id, name, desc, cat, null, mods);

    private static void Add(int id, string name, string desc, CardCategory cat, string effect, params StatMod[] mods)
    {
        var c = new CardDef { Id = id, Name = name, Description = desc, Category = cat, Mods = mods, EffectId = effect };
        All.Add(c);
        ById[id] = c;
    }
}
