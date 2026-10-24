using System;

namespace Brawler.Logic;

/// <summary>
/// Test hook (--cardcheck): counts card effects as they happen in a match, by the stat behind them (a bullet bounced,
/// split or exploded, a lifesteal heal, a blink, a revive...). Off, and close to free, unless a check turns it on.
/// </summary>
public static class EffectTally
{
    public static bool On;
    private static readonly int[] Counts = new int[Enum.GetValues<Stat>().Length];

    public static void Hit(Stat s)
    {
        if (On) Counts[(int)s]++;
    }

    public static int Count(Stat s) => Counts[(int)s];

    public static void Clear() => Array.Clear(Counts);

    /// <summary>
    /// The stats whose effect shows up as an event the game tallies. The rest (HP, speed, damage, sizes, timings...)
    /// change a number, and only that the number took hold can be checked.
    /// </summary>
    public static bool Observable(Stat s) => s is Stat.Bounces or Stat.Pierce or Stat.Homing or Stat.ExplosionRadius
        or Stat.SplitCount or Stat.Boomerang or Stat.Phase or Stat.Sticky or Stat.Guided or Stat.Reflect or Stat.BounceBlast
        or Stat.Lifesteal or Stat.Armor or Stat.Thorns or Stat.Chain or Stat.Warp or Stat.Kamikaze or Stat.ExtraLives
        or Stat.BlockSpikes or Stat.BlockHeal or Stat.BlockReload or Stat.BlockRefund or Stat.Counter or Stat.Shockwave
        or Stat.ShieldBash or Stat.TeleportOnBlock or Stat.Dash or Stat.Regen or Stat.Leech or Stat.Recoil or Stat.Pinball
        or Stat.Berserk or Stat.Wild or Stat.Adrenaline or Stat.AirJumps or Stat.WallJump or Stat.Glide or Stat.Jetpack;
}
