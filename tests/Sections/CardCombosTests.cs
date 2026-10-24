using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class CardCombosTests
{
    public static void Run()
    {
        bool idsOk = Combos.All.Select((c, i) => c.Id == i).All(b => b);
        Check(idsOk, "Combo ids are their index (they're sent as ids, never reordered)");
        var bad = Combos.All.Where(c => c.A.Length == 0 || c.B.Length == 0 || c.A.Concat(c.B).Any(id => CardRegistry.Get(id) == null || CardRegistry.IsCustom(id))
            || c.A.Intersect(c.B).Any() || c.Mods.Length == 0).Select(c => c.Name).ToList();
        Check(bad.Count == 0, "Every combo names two groups of real built-in cards that don't overlap, and does something", string.Join(", ", bad));
        Check(Combos.All.Select(c => c.Name).Distinct().Count() == Combos.All.Length && Combos.All.All(c => c.Name.Length <= 16 && c.Description.Split('\n').Length <= 3),
            "Combo names are unique and short; descriptions fit in three lines");

        Check(Combos.Active(new[] { 1, 6 }).Select(c => c.Name).SequenceEqual(new[] { "Bounce Bombs" }), "Bouncy + Explosive unlocks Bounce Bombs");
        Check(Combos.Active(new[] { 39, 6 }).Any(c => c.Id == 0), "Any card from a group counts (Ricochet works like Bouncy)");
        Check(Combos.Active(new[] { 1, 1, 39 }).Count == 0 && Combos.Active(new int[0]).Count == 0, "Cards from only one side unlock nothing");
        Check(Combos.Active(new[] { 1, 39, 6, 6 }).Count(c => c.Id == 0) == 1, "Duplicates don't unlock a combo twice");
        Check(Combos.Unlocks(new[] { 1 }, 6).Any(c => c.Id == 0) && Combos.Unlocks(new[] { 1, 6 }, 39).Count == 0 && Combos.Unlocks(new[] { 6 }, 6).Count == 0,
            "Only the pick that completes a combo counts as finding it");
        var two = Combos.Unlocks(new[] { 1, 62 }, 6).Select(c => c.Id).OrderBy(i => i).ToList();
        Check(two.SequenceEqual(new[] { 0, 1 }), "One pick can complete two combos at once", string.Join(",", two));

        var with = StatBlock.Compute(CardRegistry.Resolve(new[] { 1, 6 }));
        var without = StatBlock.Compute(CardRegistry.Resolve(new[] { 1, 6 }), combos: false);
        Check(with[Stat.BounceBlast] == 1 && without[Stat.BounceBlast] == 0, "A combo's mods land in the computed stats");
        var deadeye = StatBlock.Compute(CardRegistry.Resolve(new[] { 30, 40 }));
        var plain = StatBlock.Compute(CardRegistry.Resolve(new[] { 30, 40 }), combos: false);
        Check(Math.Abs(deadeye[Stat.Damage] - plain[Stat.Damage] * 1.2f) < 1e-3 && deadeye[Stat.Pierce] == plain[Stat.Pierce] + 1,
            "Deadeye: multipliers stack with the cards' own, adds add", $"{deadeye[Stat.Damage]} vs {plain[Stat.Damage]}");
        var order = StatBlock.Compute(CardRegistry.Resolve(new[] { 40, 30 }));
        Check(Enum.GetValues<Stat>().All(st => Math.Abs(order[st] - deadeye[st]) < 1e-3), "Pick order doesn't change a combo's result");
        Check(StatSummary.Lines(with).Any(l => l.Label == "Bounce blasts"), "The stat summary shows combo-only stats");
    }
}
