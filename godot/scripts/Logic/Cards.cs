using System.Collections.Generic;

namespace Brawler.Logic;

public enum CardCategory { Gun, Body, Movement, Chaos }

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
        Add(1, "Bouncy", "+2 bullet bounces", CardCategory.Gun,
            A(Stat.Bounces, 2));
        Add(2, "Big Bullet", "Huge bullets\n+50% damage\n-20% bullet speed", CardCategory.Gun,
            M(Stat.BulletRadius, 2.2f), M(Stat.Damage, 1.5f), M(Stat.BulletSpeed, 0.8f));
        Add(3, "Pierce", "Bullets pass through\n1 extra player", CardCategory.Gun,
            A(Stat.Pierce, 1));
        Add(4, "Splitter", "Bullets split into 3\non impact\n+10% fire cooldown", CardCategory.Gun,
            A(Stat.SplitCount, 3), M(Stat.FireCooldown, 1.1f));
        Add(5, "Homing", "Bullets curve toward\nenemies\n-20% bullet speed", CardCategory.Gun,
            A(Stat.Homing, 2.5f), M(Stat.BulletSpeed, 0.8f));
        Add(6, "Explosive", "Bullets explode\n-20% damage\n+20% fire cooldown", CardCategory.Gun,
            A(Stat.ExplosionRadius, 28), M(Stat.Damage, 0.8f), M(Stat.FireCooldown, 1.2f));
        Add(7, "Tank", "+45 HP\nBigger body\n-15% move speed", CardCategory.Body,
            A(Stat.MaxHp, 45), M(Stat.Size, 1.25f), M(Stat.MoveSpeed, 0.85f));
        Add(8, "Feather", "Low gravity for you\nand your bullets", CardCategory.Movement,
            M(Stat.Gravity, 0.6f), M(Stat.JumpVelocity, 0.95f), M(Stat.BulletGravity, 0.4f));
        Add(9, "Speedy", "+30% move speed\nSmaller body\n-5% HP", CardCategory.Movement,
            M(Stat.MoveSpeed, 1.3f), M(Stat.Size, 0.85f), M(Stat.MaxHp, 0.95f));
        Add(10, "Shotgun", "+3 bullets per shot\n-45% damage\n+30% fire cooldown", CardCategory.Gun,
            A(Stat.Projectiles, 3), A(Stat.Spread, 0.35f), M(Stat.Damage, 0.55f), M(Stat.FireCooldown, 1.3f));
        Add(11, "Quick Reload", "-50% reload time", CardCategory.Gun,
            M(Stat.ReloadTime, 0.5f));
        Add(12, "Burst", "Fire 3-round bursts\n-40% damage", CardCategory.Gun,
            A(Stat.BurstCount, 2), M(Stat.Damage, 0.6f));
        Add(13, "Big Mag", "+3 ammo\n+15% reload time", CardCategory.Gun,
            A(Stat.Ammo, 3), M(Stat.ReloadTime, 1.15f));
        Add(14, "Vampire", "Heal 35% of damage dealt\n-15% HP", CardCategory.Chaos,
            A(Stat.Lifesteal, 0.35f), M(Stat.MaxHp, 0.85f));
        Add(15, "Blink", "Blocking teleports you\ntoward your cursor\n-15% block cooldown", CardCategory.Chaos,
            "blink", A(Stat.TeleportOnBlock, 1), M(Stat.BlockCooldown, 0.85f));
        Add(16, "Glass Cannon", "+60% damage\n-30% HP", CardCategory.Chaos,
            M(Stat.Damage, 1.6f), M(Stat.MaxHp, 0.7f));

        // ---------- Movement ----------
        Add(17, "Spring Legs", "+25% jump height", CardCategory.Movement,
            M(Stat.JumpVelocity, 1.12f));
        Add(18, "Double Jump", "+1 mid-air jump\n-10 HP", CardCategory.Movement,
            A(Stat.AirJumps, 1), A(Stat.MaxHp, -10));
        Add(19, "Cloud Walker", "+2 mid-air jumps\n-25% damage", CardCategory.Movement,
            A(Stat.AirJumps, 2), M(Stat.Damage, 0.75f));
        Add(20, "Sprinter", "+20% move speed\n+10% jump height", CardCategory.Movement,
            M(Stat.MoveSpeed, 1.2f), M(Stat.JumpVelocity, 1.05f));
        Add(21, "Afterburner", "+55% move speed\n-10% HP", CardCategory.Movement,
            M(Stat.MoveSpeed, 1.55f), M(Stat.Accel, 1.3f), M(Stat.MaxHp, 0.9f));
        Add(22, "Rocket Boots", "+70% jump height\n-5% HP", CardCategory.Movement,
            M(Stat.JumpVelocity, 1.3f), M(Stat.MaxHp, 0.95f));
        Add(23, "Traction", "+80% acceleration\n+10% move speed", CardCategory.Movement,
            M(Stat.Accel, 1.8f), M(Stat.MoveSpeed, 1.1f));
        Add(24, "Ice Skates", "+45% move speed\nbut very slippery", CardCategory.Movement,
            M(Stat.MoveSpeed, 1.45f), M(Stat.Accel, 0.35f));
        Add(25, "Glider", "Hold jump while falling\nto glide\n-15% HP", CardCategory.Movement,
            A(Stat.Glide, 1), M(Stat.MaxHp, 0.85f));
        Add(26, "Wall Runner", "+40% wall-jump height\nSlow wall slides", CardCategory.Movement,
            M(Stat.WallJump, 1.4f));
        Add(27, "Parkour", "+1 mid-air jump\n+15% move speed\n+20% fire cooldown", CardCategory.Movement,
            A(Stat.AirJumps, 1), M(Stat.MoveSpeed, 1.15f), M(Stat.FireCooldown, 1.2f));
        Add(28, "Dash", "Blocking dashes you\ntoward your cursor\n-15% block duration", CardCategory.Movement,
            "dash", A(Stat.Dash, 520), M(Stat.BlockDuration, 0.85f));
        Add(29, "Jetpack", "Jump in mid-air and hold\nto fly briefly\n-15 HP", CardCategory.Movement,
            A(Stat.Jetpack, 0.8f), A(Stat.MaxHp, -15));

        // ---------- Gun: more, bigger, faster or slower bullets ----------
        Add(30, "Sniper", "+100% bullet speed\n+50% damage, flat shots\n+75% fire cooldown\n-1 ammo", CardCategory.Gun,
            A(Stat.Ammo, -1), M(Stat.BulletSpeed, 2f), M(Stat.Damage, 1.5f), M(Stat.BulletGravity, 0.2f), M(Stat.FireCooldown, 1.75f));
        Add(31, "Minigun", "+12 ammo, fires very fast\n-60% damage\nWide spray, slow reload", CardCategory.Gun,
            A(Stat.Ammo, 12), A(Stat.Spread, 0.18f), M(Stat.FireCooldown, 0.3f), M(Stat.Damage, 0.4f), M(Stat.ReloadTime, 1.6f));
        Add(32, "Twin Shot", "+1 bullet per shot\n-25% damage", CardCategory.Gun,
            A(Stat.Projectiles, 1), A(Stat.Spread, 0.12f), M(Stat.Damage, 0.75f));
        Add(33, "Hail Storm", "+6 bullets in a wide fan\n-70% damage\n-20% bullet speed", CardCategory.Gun,
            A(Stat.Projectiles, 6), A(Stat.Spread, 0.9f), M(Stat.Damage, 0.3f), M(Stat.BulletSpeed, 0.8f));
        Add(34, "Rapid Fire", "-35% fire cooldown\n-10% damage", CardCategory.Gun,
            M(Stat.FireCooldown, 0.65f), M(Stat.Damage, 0.9f));
        Add(35, "High Velocity", "+50% bullet speed\n-30% bullet drop", CardCategory.Gun,
            M(Stat.BulletSpeed, 1.5f), M(Stat.BulletGravity, 0.7f));
        Add(36, "Heavy Slugs", "+90% damage\nBigger bullets\n-35% bullet speed", CardCategory.Gun,
            M(Stat.Damage, 1.9f), M(Stat.BulletRadius, 1.6f), M(Stat.BulletSpeed, 0.65f));
        Add(37, "Cannonball", "Giant bullets, +140% damage\nMassive knockback\n-25% speed, -1 ammo", CardCategory.Gun,
            A(Stat.Ammo, -1), M(Stat.BulletRadius, 3.5f), M(Stat.Damage, 2.4f), M(Stat.Knockback, 2.5f),
            M(Stat.BulletSpeed, 0.75f), M(Stat.BulletGravity, 1.2f));
        Add(38, "Needles", "Tiny fast bullets, +3 ammo\n-20% fire cooldown\n-20% damage", CardCategory.Gun,
            A(Stat.Ammo, 3), M(Stat.BulletRadius, 0.5f), M(Stat.BulletSpeed, 1.25f), M(Stat.FireCooldown, 0.8f), M(Stat.Damage, 0.8f));
        Add(39, "Ricochet", "+5 bullet bounces\n-10% damage", CardCategory.Gun,
            A(Stat.Bounces, 5), M(Stat.Damage, 0.9f));
        Add(40, "Marksman", "+30% damage\n+25% bullet speed\n+15% fire cooldown", CardCategory.Gun,
            M(Stat.Damage, 1.3f), M(Stat.BulletSpeed, 1.25f), M(Stat.FireCooldown, 1.15f));
        Add(41, "Impact Rounds", "+150% knockback", CardCategory.Gun,
            M(Stat.Knockback, 2.5f));
        Add(42, "Drum Mag", "+8 ammo\n+60% reload time\n-10% move speed", CardCategory.Gun,
            A(Stat.Ammo, 8), M(Stat.ReloadTime, 1.6f), M(Stat.MoveSpeed, 0.9f));

        // ---------- Body: size, health and toughness ----------
        Add(43, "Giant", "Huge body, +70 HP\nLess knockback\n-40% speed, -15% jump", CardCategory.Body,
            A(Stat.MaxHp, 70), M(Stat.Size, 1.8f), M(Stat.MoveSpeed, 0.6f), M(Stat.JumpVelocity, 0.85f), M(Stat.KnockbackTaken, 0.75f));
        Add(44, "Tiny", "Tiny body\n+35% move speed\n-15% HP", CardCategory.Body,
            M(Stat.Size, 0.6f), M(Stat.MoveSpeed, 1.35f), M(Stat.JumpVelocity, 1.05f), M(Stat.MaxHp, 0.85f));
        Add(45, "Armor Plating", "Take 15% less damage\n-10% move speed", CardCategory.Body,
            A(Stat.Armor, 0.15f), M(Stat.MoveSpeed, 0.9f));
        Add(46, "Regeneration", "Heal 5 HP per second", CardCategory.Body,
            A(Stat.Regen, 5));
        Add(47, "Anchor", "-70% knockback taken\n+30 HP\n-10% jump", CardCategory.Body,
            A(Stat.MaxHp, 30), M(Stat.KnockbackTaken, 0.3f), M(Stat.JumpVelocity, 0.9f));
        Add(48, "Rubber Body", "Take 25% less damage\nbut 2.5x knockback", CardCategory.Body,
            A(Stat.Armor, 0.25f), M(Stat.KnockbackTaken, 2.5f));
        Add(49, "Juggernaut", "+40 HP, 10% less damage\nBigger body\n-25% speed, slower gun", CardCategory.Body,
            A(Stat.MaxHp, 40), A(Stat.Armor, 0.1f), M(Stat.Size, 1.3f), M(Stat.MoveSpeed, 0.75f), M(Stat.FireCooldown, 1.3f));
        Add(50, "Second Wind", "Once per battle, survive\na lethal hit at half HP\n-25% HP", CardCategory.Body,
            A(Stat.ExtraLives, 1), M(Stat.MaxHp, 0.75f));
        Add(51, "Hearty", "+40 HP", CardCategory.Body,
            A(Stat.MaxHp, 40));
        Add(52, "Shield Bearer", "-50% block cooldown\n+50% block duration\n-15% damage", CardCategory.Body,
            M(Stat.BlockCooldown, 0.5f), M(Stat.BlockDuration, 1.5f), M(Stat.Damage, 0.85f));
        Add(53, "Thorns", "Attackers take 40% of\nthe damage they deal you", CardCategory.Body,
            A(Stat.Thorns, 0.4f));
        Add(54, "Adrenaline", "Under 40% HP: +50% move\nspeed and fire rate", CardCategory.Body,
            A(Stat.Adrenaline, 0.5f));

        // ---------- Chaos: these change how you play ----------
        Add(55, "Bullet Hell", "Fire a ring of 12 bullets\n-50% damage\n+20% fire cooldown", CardCategory.Chaos,
            A(Stat.Projectiles, 11), A(Stat.Spread, 5.76f), M(Stat.Damage, 0.5f), M(Stat.FireCooldown, 1.2f));
        Add(56, "Upside Down", "Your bullets fall UP\n+50% damage", CardCategory.Chaos,
            M(Stat.BulletGravity, -0.8f), M(Stat.Damage, 1.5f));
        Add(57, "Boomerang", "Bullets fly back to you\n+20% damage", CardCategory.Chaos,
            A(Stat.Boomerang, 1), M(Stat.Damage, 1.2f));
        Add(58, "Ghost Rounds", "Bullets pass through walls\n-25% damage\nShort range", CardCategory.Chaos,
            A(Stat.Phase, 1), M(Stat.Damage, 0.75f), M(Stat.Lifetime, 0.4f));
        Add(59, "Kamikaze", "Explode hugely when you die\n+25% move speed", CardCategory.Chaos,
            A(Stat.Kamikaze, 85), M(Stat.MoveSpeed, 1.25f));
        Add(60, "Recoil Jet", "Shooting blasts you backward\n+2 ammo\n-20% damage", CardCategory.Chaos,
            A(Stat.Recoil, 300), A(Stat.Ammo, 2), M(Stat.Damage, 0.8f));
        Add(61, "Warp Rounds", "Teleport to where your\nbullets land\n+15% damage", CardCategory.Chaos,
            A(Stat.Warp, 1), M(Stat.Damage, 1.15f));
        Add(62, "Sticky Mines", "Bullets stick to walls\nas mines for 5 s\n-20% bullet speed", CardCategory.Chaos,
            A(Stat.Sticky, 5), M(Stat.BulletSpeed, 0.8f));
        Add(63, "Chain Lightning", "Hits arc to the next enemy\nfor 60% damage\n+20% fire cooldown", CardCategory.Chaos,
            A(Stat.Chain, 0.6f), M(Stat.FireCooldown, 1.2f));
        Add(64, "Berserk", "Up to +150% damage as\nyour HP drops\nCannot block, +20% speed", CardCategory.Chaos,
            A(Stat.Berserk, 1.5f), A(Stat.BlockCooldown, 999), M(Stat.MoveSpeed, 1.2f));
        Add(65, "One Shot", "1 ammo, huge fast bullet\n+180% damage\n-15% reload time", CardCategory.Chaos,
            M(Stat.Ammo, 0), M(Stat.Damage, 2.8f), M(Stat.BulletRadius, 1.8f), M(Stat.BulletSpeed, 1.4f), M(Stat.ReloadTime, 0.85f));
        Add(66, "Wild Rounds", "Every bullet gets a random\nsize, speed and damage", CardCategory.Chaos,
            A(Stat.Wild, 1));
        Add(67, "Pinball", "Your body bounces off\nwalls, floors and ceilings\n+20% move speed", CardCategory.Chaos,
            A(Stat.Pinball, 1), M(Stat.MoveSpeed, 1.2f));
        Add(68, "Mind Control", "Bullets steer hard\ntoward your crosshair", CardCategory.Chaos,
            A(Stat.Guided, 9));
        Add(69, "Leech Field", "Drain 30 HP/s from enemies\nclose to you\n-30% damage", CardCategory.Chaos,
            A(Stat.Leech, 30), M(Stat.Damage, 0.7f));
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
