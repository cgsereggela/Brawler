using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class CardPoolTests
{
    public static void Run()
    {
        var allIds = CardRegistry.All.Select(c => c.Id).ToList();
        var disabled = new HashSet<int>(allIds.Where(id => id % 3 == 0)) { 1000, 4242 };
        var rng = new Rng(99);
        bool clean = true, distinct = true, full = true;
        for (int i = 0; i < 2000; i++)
        {
            var opts = CardRegistry.RollOptions(rng, 1 + i % CardPool.MaxChoices, disabled);
            clean &= !opts.Any(disabled.Contains) && opts.All(id => CardRegistry.Get(id) != null);
            distinct &= opts.Distinct().Count() == opts.Count;
            full &= opts.Count == 1 + i % CardPool.MaxChoices;
        }
        Check(clean, "Rolls never offer disabled cards");
        Check(distinct, "Rolls never repeat a card");
        Check(full, "Rolls fill every offer while the pool is big enough");

        var everyCard = new HashSet<int>();
        var r2 = new Rng(5);
        for (int i = 0; i < 500; i++) everyCard.UnionWith(CardRegistry.RollOptions(r2, 5, disabled));
        Check(everyCard.SetEquals(CardPool.Allowed(disabled)), "Every enabled card still comes up", $"{everyCard.Count}/{CardPool.Allowed(disabled).Count}");

        // Running short: only 3 cards left for a 5-card offer gives those 3; nothing left falls back to every card.
        var three = new HashSet<int>(allIds.Skip(3));
        var shortOffer = CardRegistry.RollOptions(new Rng(1), 5, three);
        Check(shortOffer.Count == 3 && shortOffer.OrderBy(id => id).SequenceEqual(allIds.Take(3).OrderBy(id => id)),
            "A short pool offers what it has", string.Join(",", shortOffer));
        var none = CardRegistry.RollOptions(new Rng(1), 5, new HashSet<int>(allIds));
        Check(none.Count == 5 && none.Distinct().Count() == 5, "An empty pool falls back to every card rather than an empty offer");
        Check(CardRegistry.RollOptions(new Rng(3), 5).SequenceEqual(CardRegistry.RollOptions(new Rng(3), 5, new HashSet<int>())),
            "No disabled cards rolls exactly as before");
        Check(CardPool.EnabledCount(disabled) == allIds.Count - allIds.Count(id => id % 3 == 0), "Unknown ids don't count against the pool");
        Check(CardPool.MinEnabled <= CardRegistry.All.Count, "The pool minimum fits the registry");

        // Wire: 2 bytes per id, sorted, round trips ids >= 1000 and unknown ones; junk is tolerated.
        var bytes = CardPool.Encode(disabled);
        Check(bytes.Length == disabled.Count * 2, "Encoding is 2 bytes per id", $"{bytes.Length}");
        Check(CardPool.Decode(bytes).SetEquals(disabled), "Disabled list round trips, ids >= 1000 included");
        var big = new HashSet<int> { 0, 69, 255, 256, 1000, 1001, 65535 };
        Check(CardPool.Decode(CardPool.Encode(big)).SetEquals(big), "Edge ids round trip (0, 255, 256, 65535)");
        Check(CardPool.Decode(CardPool.Encode(new[] { -1, 70000, 5, 5 })).SetEquals(new[] { 5 }), "Out-of-range ids are dropped, duplicates merged");
        Check(CardPool.Decode(new byte[] { 7, 0, 9 }).SetEquals(new[] { 7 }) && CardPool.Decode(null).Count == 0 && CardPool.Encode(null).Length == 0,
            "Truncated or empty data decodes safely");
    }
}
