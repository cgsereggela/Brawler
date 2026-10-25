using System;
using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

/// <summary>Every tunable number a card can touch. Player and bullet stats live in one block.</summary>
public enum Stat
{
    // Player
    MaxHp,
    MoveSpeed,
    JumpVelocity,
    Gravity,
    Size,
    BlockCooldown,
    BlockDuration,
    Lifesteal,
    TeleportOnBlock,
    // Weapon
    FireCooldown,
    Ammo,
    ReloadTime,
    Projectiles,
    Spread,
    BurstCount,
    BurstInterval,
    // Bullet
    BulletSpeed,
    BulletGravity,
    Damage,
    BulletRadius,
    Bounces,
    Pierce,
    Homing,
    ExplosionRadius,
    SplitCount,
    Knockback,
    Lifetime,
    // Movement
    AirJumps,        // extra jumps in mid-air
    Accel,           // multiplier on ground/air acceleration
    WallJump,        // multiplier on wall-jump height (and slower wall slides)
    Glide,           // 1 = holding jump caps fall speed
    Jetpack,         // seconds of thrust per landing
    Dash,            // block dashes this far (px/s burst)
    // Body
    Armor,           // fraction of incoming damage ignored
    Regen,           // HP per second
    KnockbackTaken,  // multiplier on knockback received
    ExtraLives,      // revives per battle
    Thorns,          // fraction of damage taken reflected to the attacker
    Adrenaline,      // speed and fire-rate bonus while under 40% HP
    // Chaos
    Recoil,          // firing pushes you backward
    Boomerang,       // 1 = bullets fly back to you
    Phase,           // 1 = bullets pass through walls
    Sticky,          // seconds a bullet sits as a mine after hitting a wall
    Guided,          // turn rate toward your crosshair
    Chain,           // fraction of hit damage that arcs to the next enemy
    Berserk,         // extra damage at low HP (up to this fraction)
    Wild,            // 1 = every bullet gets random size, speed and damage
    Leech,           // damage per second to enemies near you (healing you)
    Kamikaze,        // explosion radius when you die
    Pinball,         // 1 = your body bounces off surfaces
    Warp,            // 1 = you teleport to where your bullets land
    // Block
    Reflect,         // 1 = blocked bullets fly back at their shooter
    BlockHeal,       // fraction of a blocked hit's damage healed
    BlockReload,     // 1 = blocking a hit refills the magazine
    BlockRefund,     // fraction of the block cooldown refunded per blocked hit
    Counter,         // bonus damage for a few seconds after blocking a hit
    ShieldBash,      // damage to enemies next to you when you start a block
    Shockwave,       // radius in which starting a block erases enemy bullets
    BlockSpikes,     // fraction of a blocked hit's damage dealt back to the attacker
    // Combos
    BounceBlast,     // 1 = explosive bullets also explode on every bounce
}

public enum ModOp { Add, Mul }

public readonly record struct StatMod(Stat Stat, ModOp Op, float Value);

public sealed class StatBlock
{
    public static readonly int Count = Enum.GetValues<Stat>().Length;
    private readonly float[] _v = new float[Count];

    public float this[Stat s]
    {
        get => _v[(int)s];
        set => _v[(int)s] = value;
    }

    public int Int(Stat s) => (int)MathF.Round(_v[(int)s]);

    /// <summary>"Cannot block" cards (Berserk, Iron Will, All In) add 999 s of block cooldown; no real loadout gets near this.</summary>
    public const float NoBlockCooldown = 100;
    public bool CannotBlock => this[Stat.BlockCooldown] >= NoBlockCooldown;

    public static StatBlock Base()
    {
        var b = new StatBlock();
        b[Stat.MaxHp] = 100;
        // 25% faster and 25% higher than the original 115 / 292 (an ~82 px apex; height goes with v^2).
        b[Stat.MoveSpeed] = 144;
        b[Stat.JumpVelocity] = 326.5f;
        b[Stat.Gravity] = 650;
        b[Stat.Size] = 1;
        b[Stat.BlockCooldown] = 4f;
        b[Stat.BlockDuration] = 0.3f;
        b[Stat.Lifesteal] = 0;
        b[Stat.TeleportOnBlock] = 0;

        b[Stat.FireCooldown] = 0.3f;
        b[Stat.Ammo] = 3;
        b[Stat.ReloadTime] = 1.5f;
        b[Stat.Projectiles] = 1;
        b[Stat.Spread] = 0;
        b[Stat.BurstCount] = 1;
        b[Stat.BurstInterval] = 0.07f;

        b[Stat.BulletSpeed] = 380;
        b[Stat.BulletGravity] = 320;
        b[Stat.Damage] = 25;
        b[Stat.BulletRadius] = 2;
        b[Stat.Bounces] = 0;
        b[Stat.Pierce] = 0;
        b[Stat.Homing] = 0;
        b[Stat.ExplosionRadius] = 0;
        b[Stat.SplitCount] = 0;
        b[Stat.Knockback] = 140;
        b[Stat.Lifetime] = 4f;
        b[Stat.Accel] = 1;
        b[Stat.WallJump] = 1;
        b[Stat.KnockbackTaken] = 1;
        return b;
    }

    /// <summary>
    /// Base stats + cards in pick order: all additive mods first, then all multipliers, then clamps.
    /// The fixed order keeps stacking identical on every peer. Combos the cards unlock add their mods after the
    /// cards' own, in combo id order.
    /// </summary>
    public static StatBlock Compute(IEnumerable<CardDef> cards, bool combos = true)
    {
        var b = Base();
        var list = new List<CardDef>(cards);
        var mods = new List<StatMod>();
        foreach (var c in list) mods.AddRange(c.Mods);
        if (combos)
            foreach (var combo in Combos.Active(list.Select(c => c.Id)))
                mods.AddRange(combo.Mods);
        foreach (var m in mods)
            if (m.Op == ModOp.Add) b[m.Stat] += m.Value;
        foreach (var m in mods)
            if (m.Op == ModOp.Mul) b[m.Stat] *= m.Value;
        Clamp(b);
        return b;
    }

    private static void Clamp(StatBlock b)
    {
        b[Stat.MaxHp] = MathF.Max(10, b[Stat.MaxHp]);
        b[Stat.MoveSpeed] = Math.Clamp(b[Stat.MoveSpeed], 50, 400);
        b[Stat.Gravity] = MathF.Max(150, b[Stat.Gravity]);
        b[Stat.Size] = Math.Clamp(b[Stat.Size], 0.5f, 2.5f);
        b[Stat.BlockCooldown] = MathF.Max(0.6f, b[Stat.BlockCooldown]);
        b[Stat.FireCooldown] = MathF.Max(0.05f, b[Stat.FireCooldown]);
        b[Stat.Ammo] = Math.Clamp(b[Stat.Ammo], 1, 40);
        b[Stat.ReloadTime] = MathF.Max(0.2f, b[Stat.ReloadTime]);
        b[Stat.Projectiles] = Math.Clamp(b[Stat.Projectiles], 1, 24);
        b[Stat.BurstCount] = Math.Clamp(b[Stat.BurstCount], 1, 12);
        b[Stat.BulletSpeed] = Math.Clamp(b[Stat.BulletSpeed], 60, 1200);
        b[Stat.Damage] = MathF.Max(1, b[Stat.Damage]);
        b[Stat.BulletRadius] = Math.Clamp(b[Stat.BulletRadius], 1, 14);
        b[Stat.SplitCount] = Math.Clamp(b[Stat.SplitCount], 0, 12);
        b[Stat.JumpVelocity] = Math.Clamp(b[Stat.JumpVelocity], 168, 805);
        b[Stat.AirJumps] = Math.Clamp(b[Stat.AirJumps], 0, 5);
        b[Stat.Accel] = Math.Clamp(b[Stat.Accel], 0.2f, 4f);
        b[Stat.Armor] = Math.Clamp(b[Stat.Armor], -0.5f, 0.75f); // below 0 (Glass Jaw) is extra damage taken
        b[Stat.KnockbackTaken] = Math.Clamp(b[Stat.KnockbackTaken], 0.1f, 4f);
        b[Stat.Thorns] = Math.Clamp(b[Stat.Thorns], 0, 1.5f);
        b[Stat.Lifetime] = Math.Clamp(b[Stat.Lifetime], 0.5f, 8f);
        b[Stat.BlockHeal] = Math.Clamp(b[Stat.BlockHeal], 0, 2f);
        b[Stat.BlockRefund] = Math.Clamp(b[Stat.BlockRefund], 0, 1f);
        b[Stat.Counter] = Math.Clamp(b[Stat.Counter], 0, 3f);
        b[Stat.Shockwave] = Math.Clamp(b[Stat.Shockwave], 0, 160f);
        b[Stat.BlockSpikes] = Math.Clamp(b[Stat.BlockSpikes], 0, 3f);
    }
}
