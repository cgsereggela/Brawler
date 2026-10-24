using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class StatsTests
{
    public static void Run()
    {
        var baseStats = StatBlock.Base();
        var none = StatBlock.Compute(Array.Empty<CardDef>());
        Check(none[Stat.Damage] == baseStats[Stat.Damage], "No cards = base stats");

        var tank = CardRegistry.Get(7);
        var once = StatBlock.Compute(new[] { tank });
        Check(Math.Abs(once[Stat.MaxHp] - 145) < 0.01f, "Tank adds 45 HP", $"{once[Stat.MaxHp]}");

        // Adds apply before multipliers regardless of pick order: (100 + 45) * 0.85 = 123.25
        var vamp = CardRegistry.Get(14);
        var ab = StatBlock.Compute(new[] { tank, vamp });
        var ba = StatBlock.Compute(new[] { vamp, tank });
        Check(Math.Abs(ab[Stat.MaxHp] - 123.25f) < 0.01f && Math.Abs(ba[Stat.MaxHp] - 123.25f) < 0.01f,
            "Stacking order: adds before multipliers, order-independent", $"{ab[Stat.MaxHp]} / {ba[Stat.MaxHp]}");

        var bouncy = CardRegistry.Get(1);
        var b3 = StatBlock.Compute(new[] { bouncy, bouncy, bouncy });
        Check(b3.Int(Stat.Bounces) == 6, "Bouncy stacks additively", $"{b3.Int(Stat.Bounces)}");

        var cd = StatBlock.Compute(Enumerable.Repeat(CardRegistry.Get(11), 10));
        Check(cd[Stat.ReloadTime] >= 0.2f, "Clamps keep reload time sane", $"{cd[Stat.ReloadTime]}");

        Check(CardRegistry.All.Select(c => c.Id).Distinct().Count() == CardRegistry.All.Count, "Card ids are unique");
        Check(CardRegistry.All.Count >= 15, "At least 15 cards", $"{CardRegistry.All.Count}");
        foreach (var cat in Enum.GetValues<CardCategory>())
        {
            int n = CardRegistry.All.Count(c => c.Category == cat);
            Check(n >= 12, $"At least 12 {cat} cards", $"{n}");
        }

        var oneShot = StatBlock.Compute(new[] { CardRegistry.Get(13), CardRegistry.Get(42), CardRegistry.Get(65) });
        Check(oneShot.Int(Stat.Ammo) == 1, "One Shot leaves exactly 1 ammo even with ammo cards", $"{oneShot.Int(Stat.Ammo)}");
        var hell = StatBlock.Compute(new[] { CardRegistry.Get(55) });
        Check(hell.Int(Stat.Projectiles) == 12, "Bullet Hell fires 12 bullets", $"{hell.Int(Stat.Projectiles)}");
        var giant = StatBlock.Compute(Enumerable.Repeat(CardRegistry.Get(43), 3));
        Check(giant[Stat.Size] <= 2.5f && giant[Stat.MoveSpeed] >= 40, "Stacked Giants stay inside size and speed clamps", $"{giant[Stat.Size]} / {giant[Stat.MoveSpeed]}");
        var armor = StatBlock.Compute(Enumerable.Repeat(CardRegistry.Get(48), 4));
        Check(armor[Stat.Armor] <= 0.75f, "Armor is capped so nobody is invulnerable", $"{armor[Stat.Armor]}");
        var jumps = StatBlock.Compute(new[] { CardRegistry.Get(18), CardRegistry.Get(19) });
        Check(jumps.Int(Stat.AirJumps) == 3, "Air jumps stack", $"{jumps.Int(Stat.AirJumps)}");
        var upside = StatBlock.Compute(new[] { CardRegistry.Get(56) });
        Check(upside[Stat.BulletGravity] < 0, "Upside Down makes bullet gravity negative", $"{upside[Stat.BulletGravity]}");
        foreach (var c in CardRegistry.All)
        {
            var s = StatBlock.Compute(new[] { c });
            Check(s[Stat.MaxHp] >= 10 && s[Stat.Damage] >= 1 && s[Stat.BulletSpeed] >= 60 && s[Stat.FireCooldown] > 0,
                $"{c.Name} keeps core stats sane");
        }

        var opts = CardRegistry.RollOptions(new Rng(1), 5);
        Check(opts.Distinct().Count() == 5, "Rolled card options are distinct");
    }
}
