using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

public enum CardCategory { Gun, Body, Movement, Chaos, Block, Curse }

/// <summary>
/// Rare cards turn up in a pick only now and then (MatchSettings.RareChance per offered card) and in cursed deals.
/// Curse cards (CardCategory.Curse) are never offered on their own: they only come bundled with a rare in a deal.
/// </summary>
public enum CardRarity { Common, Rare }

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
    public CardRarity Rarity { get; init; }
    public bool IsRare => Rarity == CardRarity.Rare;
    public bool IsCurse => Category == CardCategory.Curse;
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
            "dash", A(Stat.Dash, 650), M(Stat.BlockDuration, 0.85f));
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

        // ---------- Block: these change what your block does, or trade it away ----------
        Add(70, "Reflector", "Blocked bullets fly\nback at the shooter\n+20% block cooldown", CardCategory.Block,
            A(Stat.Reflect, 1), M(Stat.BlockCooldown, 1.2f));
        Add(71, "Shield Bash", "Blocking hits nearby\nenemies for 30 and\nknocks them back", CardCategory.Block,
            A(Stat.ShieldBash, 30));
        Add(72, "Bulwark", "+100% block duration\n+15% block cooldown", CardCategory.Block,
            M(Stat.BlockDuration, 2f), M(Stat.BlockCooldown, 1.15f));
        Add(73, "Quick Guard", "-40% block cooldown\n-25% block duration", CardCategory.Block,
            M(Stat.BlockCooldown, 0.6f), M(Stat.BlockDuration, 0.75f));
        Add(74, "Riposte", "After a blocked hit\n+60% damage for 2 s", CardCategory.Block,
            A(Stat.Counter, 0.6f));
        Add(75, "Absorb", "Blocked hits heal\n75% of their damage\n-10% HP", CardCategory.Block,
            A(Stat.BlockHeal, 0.75f), M(Stat.MaxHp, 0.9f));
        Add(76, "Reactive Reload", "Blocking a hit refills\nyour ammo\n-10% reload time", CardCategory.Block,
            A(Stat.BlockReload, 1), M(Stat.ReloadTime, 0.9f));
        Add(77, "Momentum", "Blocked hits refund\nhalf your block\ncooldown", CardCategory.Block,
            A(Stat.BlockRefund, 0.5f));
        Add(78, "Shockwave", "Blocking erases\nnearby enemy bullets\n+10% block cooldown", CardCategory.Block,
            A(Stat.Shockwave, 60), M(Stat.BlockCooldown, 1.1f));
        Add(79, "Iron Will", "Cannot block\n+35 HP\nTake 10% less damage", CardCategory.Block,
            A(Stat.BlockCooldown, 999), A(Stat.MaxHp, 35), A(Stat.Armor, 0.1f));
        Add(80, "All In", "Cannot block\n+30% damage\n-20% fire cooldown", CardCategory.Block,
            A(Stat.BlockCooldown, 999), M(Stat.Damage, 1.3f), M(Stat.FireCooldown, 0.8f));
        Add(81, "Spiked Shield", "Blocked hits deal\nfull damage back\nto the attacker", CardCategory.Block,
            A(Stat.BlockSpikes, 1));

        // ---------- Rare: build-defining cards that turn up now and then (MatchSettings.RareChance) ----------
        Rare(82, "Arsenal", "+2 bullets per shot\n+4 ammo\n-15% fire cooldown", CardCategory.Gun,
            A(Stat.Projectiles, 2), A(Stat.Spread, 0.2f), A(Stat.Ammo, 4), M(Stat.FireCooldown, 0.85f));
        Rare(83, "Seeker Warheads", "Homing explosive bullets\n+20% damage\n+15% fire cooldown", CardCategory.Gun,
            A(Stat.Homing, 3), A(Stat.ExplosionRadius, 34), M(Stat.Damage, 1.2f), M(Stat.FireCooldown, 1.15f));
        Rare(84, "Hydra", "Bullets split into 4\non impact\n+2 bullet bounces", CardCategory.Gun,
            A(Stat.SplitCount, 4), A(Stat.Bounces, 2));
        Rare(85, "Overclock", "-45% fire cooldown\n-50% reload time\n+3 ammo", CardCategory.Gun,
            M(Stat.FireCooldown, 0.55f), M(Stat.ReloadTime, 0.5f), A(Stat.Ammo, 3));
        Rare(86, "Titan", "+90 HP\nTake 20% less damage\nHuge, hard to push\n-25% move speed", CardCategory.Body,
            A(Stat.MaxHp, 90), A(Stat.Armor, 0.2f), M(Stat.Size, 1.6f), M(Stat.KnockbackTaken, 0.2f), M(Stat.MoveSpeed, 0.75f));
        Rare(87, "Phoenix", "Survive 2 lethal hits\nper battle, at half HP\n+10 HP", CardCategory.Body,
            A(Stat.ExtraLives, 2), A(Stat.MaxHp, 10));
        Rare(88, "Skywalker", "+2 mid-air jumps\nHold jump to glide\n+20% move speed", CardCategory.Movement,
            A(Stat.AirJumps, 2), A(Stat.Glide, 1), M(Stat.MoveSpeed, 1.2f), M(Stat.JumpVelocity, 1.08f));
        Rare(89, "Storm Caller", "Hits arc to the next\nenemy for full damage\n+2 bullet bounces", CardCategory.Chaos,
            A(Stat.Chain, 1f), A(Stat.Bounces, 2));
        Rare(90, "Vampire Lord", "50% lifesteal\nDrain 20 HP/s nearby\n+20 HP", CardCategory.Chaos,
            A(Stat.Lifesteal, 0.5f), A(Stat.Leech, 20), A(Stat.MaxHp, 20));
        Rare(91, "Mirror Guard", "Blocks reflect bullets\n-40% block cooldown\nBlocks refund half", CardCategory.Block,
            A(Stat.Reflect, 1), M(Stat.BlockCooldown, 0.6f), A(Stat.BlockRefund, 0.5f));

        // ---------- Curse: pure downsides, only ever taken as half of a cursed deal ----------
        Add(92, "Brittle", "-25% HP", CardCategory.Curse,
            M(Stat.MaxHp, 0.75f));
        Add(93, "Lead Boots", "-20% move speed\n-15% jump height", CardCategory.Curse,
            M(Stat.MoveSpeed, 0.8f), M(Stat.JumpVelocity, 0.92f));
        Add(94, "Butterfingers", "+60% reload time", CardCategory.Curse,
            M(Stat.ReloadTime, 1.6f));
        Add(95, "Rusty Trigger", "+35% fire cooldown", CardCategory.Curse,
            M(Stat.FireCooldown, 1.35f));
        Add(96, "Blunt Rounds", "-20% damage", CardCategory.Curse,
            M(Stat.Damage, 0.8f));
        Add(97, "Greased Soles", "-60% acceleration", CardCategory.Curse,
            M(Stat.Accel, 0.4f));
        Add(98, "Heavy Shield", "+75% block cooldown", CardCategory.Curse,
            M(Stat.BlockCooldown, 1.75f));
        Add(99, "Glass Jaw", "Take 20% more damage", CardCategory.Curse,
            A(Stat.Armor, -0.2f));
        Add(100, "Big Target", "+35% body size", CardCategory.Curse,
            M(Stat.Size, 1.35f));
        Add(101, "Small Mag", "-2 ammo", CardCategory.Curse,
            A(Stat.Ammo, -2));
        Add(102, "Short Fuse", "Bullets fizzle out\nafter 1 second", CardCategory.Curse,
            M(Stat.Lifetime, 0.25f));
        Add(103, "Pushover", "Take 2x knockback", CardCategory.Curse,
            M(Stat.KnockbackTaken, 2f));

        BuiltInCount = All.Count;
    }

    /// <summary>Built-in cards are the first this many entries of <see cref="All"/>; custom cards follow them.</summary>
    private static readonly int BuiltInCount;

    public static bool IsCustom(int id) => id >= CustomCards.MinId;

    /// <summary>The custom cards in play right now (the local set, or the host's while in someone else's lobby).</summary>
    public static IReadOnlyList<CardDef> Custom => All.GetRange(BuiltInCount, All.Count - BuiltInCount);

    /// <summary>
    /// Adds a card at runtime, after the built-ins, so it can be rolled. False if its id is taken.
    /// Callers validate first (CustomCards.Validate); this only guards the id.
    /// </summary>
    public static bool Register(CardDef card)
    {
        if (card == null || ById.ContainsKey(card.Id)) return false;
        All.Add(card);
        ById[card.Id] = card;
        return true;
    }

    /// <summary>Drops every runtime-registered card, leaving the built-ins.</summary>
    public static void ResetToBuiltIns()
    {
        for (int i = BuiltInCount; i < All.Count; i++) ById.Remove(All[i].Id);
        All.RemoveRange(BuiltInCount, All.Count - BuiltInCount);
    }

    /// <summary>Replaces the custom set: built-ins plus exactly these cards, in this order.</summary>
    public static void SetCustom(IEnumerable<CardDef> cards)
    {
        ResetToBuiltIns();
        foreach (var c in cards) Register(c);
    }

    public static CardDef Get(int id) => ById.TryGetValue(id, out var c) ? c : null;

    public static IEnumerable<CardDef> Resolve(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            if (ById.TryGetValue(id, out var c)) yield return c;
    }

    /// <summary>
    /// Picks <paramref name="count"/> distinct random cards, skipping <paramref name="disabled"/> ones (see CardPool).
    /// If the pool runs short the offer is just smaller; if nothing at all is enabled, every card is back in play,
    /// so a pick can never come up empty.
    /// Each card in the offer is a rare one with probability <paramref name="rareChance"/> (0..1) while enabled rares
    /// are left; curses are never offered here (see <see cref="RollDeal"/>).
    /// </summary>
    public static List<int> RollOptions(Rng rng, int count, IReadOnlySet<int> disabled = null, float rareChance = 0)
    {
        var pool = CardPool.Allowed(disabled);
        var rares = rareChance > 0 ? CardPool.AllowedRares(disabled) : new List<int>();
        if (pool.Count == 0 && rares.Count == 0) pool = CardPool.Allowed(null);
        var result = new List<int>();
        while (result.Count < count && (pool.Count > 0 || rares.Count > 0))
        {
            // No rng draw for the rare roll when rares are off, so a match without them rolls exactly as before.
            bool rare = rares.Count > 0 && (pool.Count == 0 || rng.Range(0f, 1f) < rareChance);
            var from = rare ? rares : pool;
            int i = rng.Range(0, from.Count - 1);
            result.Add(from[i]);
            from.RemoveAt(i);
        }
        return result;
    }

    /// <summary>
    /// A cursed deal: a random enabled rare card that isn't already in <paramref name="offered"/>, bundled with a random
    /// enabled curse. Null when either pool is empty.
    /// </summary>
    public static (int Rare, int Curse)? RollDeal(Rng rng, IReadOnlySet<int> disabled, IReadOnlyCollection<int> offered)
    {
        var rares = CardPool.AllowedRares(disabled).Where(id => !offered.Contains(id)).ToList();
        var curses = CardPool.AllowedCurses(disabled);
        if (rares.Count == 0 || curses.Count == 0) return null;
        return (rares[rng.Range(0, rares.Count - 1)], curses[rng.Range(0, curses.Count - 1)]);
    }

    private static StatMod A(Stat s, float v) => new(s, ModOp.Add, v);
    private static StatMod M(Stat s, float v) => new(s, ModOp.Mul, v);

    private static void Add(int id, string name, string desc, CardCategory cat, params StatMod[] mods)
        => Put(id, name, desc, cat, null, CardRarity.Common, mods);

    private static void Add(int id, string name, string desc, CardCategory cat, string effect, params StatMod[] mods)
        => Put(id, name, desc, cat, effect, CardRarity.Common, mods);

    private static void Rare(int id, string name, string desc, CardCategory cat, params StatMod[] mods)
        => Put(id, name, desc, cat, null, CardRarity.Rare, mods);

    private static void Put(int id, string name, string desc, CardCategory cat, string effect, CardRarity rarity, StatMod[] mods)
    {
        var c = new CardDef { Id = id, Name = name, Description = desc, Category = cat, Mods = mods, EffectId = effect, Rarity = rarity };
        All.Add(c);
        ById[id] = c;
    }
}
