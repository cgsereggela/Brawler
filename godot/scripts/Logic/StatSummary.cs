using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Brawler.Logic;

/// <summary>One readable stat for the HUD: label, formatted value, and whether cards moved it off the base value.</summary>
public readonly record struct StatLine(string Label, string Value, bool Changed);

/// <summary>Turns a stat block into short, player-facing lines (the HUD's hover card).</summary>
public static class StatSummary
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(float v, string fmt = "0.#") => v.ToString(fmt, Inv);
    private static string Pct(float v) => (v * 100).ToString("0", Inv) + "%";
    private static string Sec(float v) => v.ToString("0.0#", Inv) + "s";

    /// <summary>Always shown: what every loadout has. Labels and formatters, keyed by the stats they read.</summary>
    private static readonly (string Label, Stat[] Reads, Func<StatBlock, string> Show)[] Core =
    {
        ("HP", [Stat.MaxHp], s => N(s[Stat.MaxHp], "0")),
        ("Damage", [Stat.Damage], s => N(s[Stat.Damage])),
        ("Ammo", [Stat.Ammo], s => s.Int(Stat.Ammo).ToString(Inv)),
        ("Reload", [Stat.ReloadTime], s => Sec(s[Stat.ReloadTime])),
        ("Fire rate", [Stat.FireCooldown], s => N(1 / s[Stat.FireCooldown]) + "/s"),
        ("Bullet speed", [Stat.BulletSpeed], s => N(s[Stat.BulletSpeed], "0")),
        ("Move speed", [Stat.MoveSpeed], s => N(s[Stat.MoveSpeed], "0")),
        ("Jump", [Stat.JumpVelocity, Stat.Gravity], s => N(s[Stat.JumpVelocity] * s[Stat.JumpVelocity] / (2 * s[Stat.Gravity]), "0") + "px"),
        ("Block", [Stat.BlockCooldown], s => s.CannotBlock ? "can't" : Sec(s[Stat.BlockCooldown])),
    };

    /// <summary>Shown only once cards change them from base.</summary>
    private static readonly Dictionary<Stat, (string Label, Func<float, string> Show)> Extra = new()
    {
        [Stat.Size] = ("Size", v => "x" + N(v, "0.0#")),
        [Stat.BlockDuration] = ("Block time", Sec),
        [Stat.Lifesteal] = ("Lifesteal", Pct),
        [Stat.TeleportOnBlock] = ("Block teleport", _ => "yes"),
        [Stat.Projectiles] = ("Bullets per shot", v => N(v, "0")),
        [Stat.Spread] = ("Spread", v => N(v, "0.#") + "°"),
        [Stat.BurstCount] = ("Burst", v => N(v, "0")),
        [Stat.BurstInterval] = ("Burst gap", Sec),
        [Stat.BulletGravity] = ("Bullet gravity", v => N(v, "0")),
        [Stat.BulletRadius] = ("Bullet size", v => N(v, "0.#")),
        [Stat.Bounces] = ("Bounces", v => N(v, "0")),
        [Stat.Pierce] = ("Pierce", v => N(v, "0")),
        [Stat.Homing] = ("Homing", v => N(v, "0.#")),
        [Stat.ExplosionRadius] = ("Explosion", v => N(v, "0") + "px"),
        [Stat.SplitCount] = ("Splits", v => N(v, "0")),
        [Stat.Knockback] = ("Knockback", v => N(v, "0")),
        [Stat.Lifetime] = ("Bullet life", Sec),
        [Stat.AirJumps] = ("Air jumps", v => N(v, "0")),
        [Stat.Accel] = ("Acceleration", v => "x" + N(v, "0.0#")),
        [Stat.WallJump] = ("Wall jump", v => "x" + N(v, "0.0#")),
        [Stat.Glide] = ("Glide", _ => "yes"),
        [Stat.Jetpack] = ("Jetpack", Sec),
        [Stat.Dash] = ("Dash", v => N(v, "0")),
        [Stat.Armor] = ("Armor", Pct),
        [Stat.Regen] = ("Regen", v => N(v) + "/s"),
        [Stat.KnockbackTaken] = ("Knockback taken", v => "x" + N(v, "0.0#")),
        [Stat.ExtraLives] = ("Extra lives", v => N(v, "0")),
        [Stat.Thorns] = ("Thorns", Pct),
        [Stat.Adrenaline] = ("Adrenaline", Pct),
        [Stat.Recoil] = ("Recoil", v => N(v, "0")),
        [Stat.Boomerang] = ("Boomerang", _ => "yes"),
        [Stat.Phase] = ("Phasing bullets", _ => "yes"),
        [Stat.Sticky] = ("Sticky mines", Sec),
        [Stat.Guided] = ("Guided", v => N(v, "0.#")),
        [Stat.Chain] = ("Chain", Pct),
        [Stat.Berserk] = ("Berserk", Pct),
        [Stat.Wild] = ("Wild bullets", _ => "yes"),
        [Stat.Leech] = ("Leech", v => N(v) + "/s"),
        [Stat.Kamikaze] = ("Kamikaze", v => N(v, "0") + "px"),
        [Stat.Pinball] = ("Pinball", _ => "yes"),
        [Stat.Warp] = ("Warp", _ => "yes"),
        [Stat.Reflect] = ("Reflect", _ => "yes"),
        [Stat.BlockHeal] = ("Block heal", Pct),
        [Stat.BlockReload] = ("Block reload", _ => "yes"),
        [Stat.BlockRefund] = ("Block refund", Pct),
        [Stat.Counter] = ("Counter", Pct),
        [Stat.ShieldBash] = ("Shield bash", v => N(v, "0")),
        [Stat.Shockwave] = ("Shockwave", v => N(v, "0") + "px"),
        [Stat.BlockSpikes] = ("Block spikes", Pct),
        [Stat.BounceBlast] = ("Bounce blasts", _ => "yes"),
    };

    private static readonly StatBlock Base = StatBlock.Base();

    private static bool Differs(StatBlock s, Stat st) => MathF.Abs(s[st] - Base[st]) > 1e-4f;

    /// <summary>The core stats in a fixed order, then every other stat that cards changed, in enum order.</summary>
    public static List<StatLine> Lines(StatBlock s)
    {
        var lines = Core.Select(c => new StatLine(c.Label, c.Show(s), c.Reads.Any(st => Differs(s, st)))).ToList();
        foreach (var st in Enum.GetValues<Stat>())
            if (Extra.TryGetValue(st, out var e) && Differs(s, st))
                lines.Add(new StatLine(e.Label, e.Show(s[st]), true));
        return lines;
    }

    /// <summary>Whether the summary can show this stat (every stat should be, so a new one is never invisible).</summary>
    public static bool Covers(Stat st) => Extra.ContainsKey(st) || Core.Any(c => c.Reads.Contains(st));

    /// <summary>Owned cards grouped by id in first-pick order, with how many of each.</summary>
    public static List<(CardDef Card, int Count)> Stacks(IEnumerable<int> cardIds) =>
        CardRegistry.Resolve(cardIds).GroupBy(c => c.Id).Select(g => (g.First(), g.Count())).ToList();
}
