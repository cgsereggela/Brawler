using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class CustomCardsTests
{
    public static void Run()
    {
        var log = new List<string>();
        bool Logged(string part) => log.Exists(l => l.Contains(part));

        // A valid single card and a valid list, case-insensitive names, description as a list of lines.
        var one = CustomCards.Parse("""
            { "id": 1000, "name": "Pogo", "description": "+2 mid-air jumps", "category": "movement",
              "mods": [ { "stat": "airjumps", "op": "ADD", "value": 2 }, { "stat": "MaxHp", "op": "mul", "value": 0.9 } ] }
            """, "pogo.json", log);
        Check(one.Count == 1 && one[0].Id == 1000 && one[0].Name == "Pogo" && one[0].Category == CardCategory.Movement
              && one[0].Mods.Length == 2 && one[0].Mods[0] == new StatMod(Stat.AirJumps, ModOp.Add, 2)
              && one[0].Mods[1] == new StatMod(Stat.MaxHp, ModOp.Mul, 0.9f) && one[0].EffectId == null && log.Count == 0,
            "Custom card: a valid card parses", string.Join(" | ", log));
        var list = CustomCards.Parse("""
            // comments and trailing commas are fine
            [
              { "id": 1001, "name": "Blinker", "description": ["Blink on block", "-10% HP"], "effect": "Blink",
                "mods": [ { "stat": "TeleportOnBlock", "op": "add", "value": 1 }, ], },
              { "ID": 1002, "Name": "Bare Effect", "Effect": "dash" },
            ]
            """, "list.json", log);
        Check(list.Count == 2 && list[0].Description == "Blink on block\n-10% HP" && list[0].EffectId == "blink"
              && list[0].Category == CardCategory.Chaos && list[1].Name == "Bare Effect" && list[1].Mods.Length == 0 && list[1].EffectId == "dash",
            "Custom card: a list parses; description lines join; effect id lower-cased; category defaults to Chaos; field names ignore case");

        // Each kind of error skips just that card (or the file) with the file, the card and the reason in the log.
        (string json, string expect, string what)[] bad =
        {
            ("{ not json", "not valid JSON", "broken JSON"),
            ("42", "expected a card", "neither object nor list"),
            ("""[ 7 ]""", "not a card object", "list entry that is not an object"),
            ("""{ "name": "X", "mods": [] }""", "whole-number \"id\"", "missing id"),
            ("""{ "id": 1000.5, "name": "X", "effect": "dash" }""", "whole-number \"id\"", "fractional id"),
            ("""{ "id": 1003, "effect": "dash" }""", "needs a \"name\"", "missing name"),
            ("""{ "id": 1003, "name": "  ", "effect": "dash" }""", "name is empty", "blank name"),
            ("""{ "id": 1003, "name": "X", "category": "Magic", "effect": "dash" }""", "\"category\" must be one of", "unknown category"),
            ("""{ "id": 1003, "name": "X", "mods": [ { "stat": "Mana", "op": "add", "value": 1 } ] }""", "unknown stat \"Mana\"", "unknown stat"),
            ("""{ "id": 1003, "name": "X", "mods": [ { "stat": "3", "op": "add", "value": 1 } ] }""", "unknown stat \"3\"", "numeric stat name"),
            ("""{ "id": 1003, "name": "X", "mods": [ { "stat": "Damage", "op": "pow", "value": 2 } ] }""", "\"op\" must be", "unknown op"),
            ("""{ "id": 1003, "name": "X", "mods": [ { "stat": "Damage", "op": "add", "value": "lots" } ] }""", "\"value\" must be a number", "text value"),
            ("""{ "id": 1003, "name": "X", "mods": { "stat": "Damage" } }""", "\"mods\" must be a list", "mods not a list"),
            ("""{ "id": 1003, "name": "X", "effect": "teleport" }""", "unknown effect \"teleport\"", "unknown effect"),
            ("""{ "id": 1003, "name": "X" }""", "would do nothing", "no mods and no effect"),
            ("""{ "id": 1003, "name": "X", "description": 5, "effect": "dash" }""", "\"description\" must be", "description not text"),
        };
        foreach (var (json, expect, what) in bad)
        {
            log.Clear();
            var got = CustomCards.Parse(json, "bad.json", log);
            Check(got.Count == 0 && Logged(expect) && log.TrueForAll(l => l.StartsWith("bad.json")), $"Custom card error: {what} is skipped and logged",
                string.Join(" | ", log));
        }
        log.Clear();
        var mixed = CustomCards.Parse("""[ { "id": 1004, "name": "Good", "effect": "dash" }, { "id": 1005, "name": "Bad", "effect": "nope" } ]""", "mixed.json", log);
        Check(mixed.Count == 1 && mixed[0].Id == 1004 && Logged("mixed.json: card #2 'Bad' (id 1005): unknown effect"),
            "Custom card: one bad card in a list doesn't take the good ones with it", string.Join(" | ", log));
        log.Clear();
        var typo = CustomCards.Parse("""{ "id": 1006, "name": "Typo", "effect": "dash", "mod": [] }""", "typo.json", log);
        Check(typo.Count == 1 && Logged("unknown field \"mod\" ignored"), "Custom card: an unknown field is a warning, not an error");
        log.Clear();
        var rareCustom = CustomCards.Parse("""{ "id": 1007, "name": "Shiny", "rarity": "rare", "category": "curse", "effect": "dash" }""", "rare.json", log);
        Check(rareCustom.Count == 1 && rareCustom[0].IsRare && rareCustom[0].IsCurse && log.Count == 0, "Custom card: rarity and the Curse category parse", string.Join(" | ", log));
        var badRarity = CustomCards.Parse("""{ "id": 1008, "name": "Meh", "rarity": "epic", "effect": "dash" }""", "rare.json", log);
        Check(badRarity.Count == 0 && Logged("\"rarity\" must be one of Common, Rare"), "Custom card: an unknown rarity is skipped and logged");

        // Id range: below 1000 is reserved for built-ins, and ids must fit the 16-bit card pick message.
        foreach (int id in new[] { 0, 1, 999, 32768, -5 })
        {
            log.Clear();
            var got = CustomCards.Parse($$"""{ "id": {{id}}, "name": "X", "effect": "dash" }""", "range.json", log);
            Check(got.Count == 0 && Logged("id must be from 1000 to 32767"), $"Custom card: id {id} is out of range");
        }
        log.Clear();
        Check(CustomCards.Parse("""[ { "id": 1000, "name": "Lo", "effect": "dash" }, { "id": 32767, "name": "Hi", "effect": "dash" } ]""", "edge.json", log).Count == 2,
            "Custom card: ids 1000 and 32767 are allowed");

        // Duplicates: the first card with an id wins, within a file and across files (files in the order given).
        log.Clear();
        var all = CustomCards.Collect(new[]
        {
            ("a.json", """[ { "id": 1010, "name": "First", "effect": "dash" }, { "id": 1010, "name": "Twin", "effect": "dash" } ]"""),
            ("b.json", """{ "id": 1010, "name": "Late", "effect": "dash" }"""),
            ("c.json", """{ "id": 1011, "name": "Other", "effect": "dash" }"""),
        }, log);
        Check(all.Count == 2 && all[0].Name == "First" && all[1].Id == 1011
              && Logged("a.json: card 'Twin' (id 1010): id already used by 'First' in a.json") && Logged("b.json: card 'Late' (id 1010): id already used by 'First' in a.json"),
            "Custom card: duplicate ids keep the first and warn", string.Join(" | ", log));
        log.Clear();
        var many = CustomCards.Collect(new[] { ("many.json", "[" + string.Join(",", Enumerable.Range(2000, CustomCards.MaxCards + 5)
            .Select(i => $$"""{ "id": {{i}}, "name": "C{{i}}", "effect": "dash" }""")) + "]") }, log);
        Check(many.Count == CustomCards.MaxCards && log.Count == 5 && Logged($"more than {CustomCards.MaxCards} custom cards"), "Custom card: at most 200 per set");

        // Absurd values are clamped (with a log line); long text is cut.
        log.Clear();
        var huge = CustomCards.Parse($$"""
            { "id": 1020, "name": "{{new string('N', 40)}}", "description": "{{new string('d', 300)}}",
              "mods": [ { "stat": "Projectiles", "op": "add", "value": 5000 }, { "stat": "Size", "op": "mul", "value": 1e9 },
                        { "stat": "ExplosionRadius", "op": "add", "value": -99999 }, { "stat": "MaxHp", "op": "add", "value": 1e30 },
                        { "stat": "Damage", "op": "mul", "value": 1.5 } ] }
            """, "huge.json", log)[0];
        Check(huge.Mods[0].Value == 24 && huge.Mods[1].Value == CustomCards.MaxMul && huge.Mods[2].Value == -200
              && huge.Mods[3].Value == CustomCards.DefaultAddLimit && huge.Mods[4].Value == 1.5f && log.Count(l => l.Contains("clamped")) == 4,
            "Custom card: absurd values are clamped per stat, sane ones untouched", string.Join(" | ", huge.Mods.Select(m => m.Value)));
        Check(huge.Name.Length == CustomCards.MaxNameLength && huge.Description.Length == CustomCards.MaxDescriptionLength
              && Logged("name longer than") && Logged("description longer than"), "Custom card: long name and description are cut short");
        log.Clear();
        var tooMany = CustomCards.Parse("""{ "id": 1021, "name": "X", "mods": [""" + string.Join(",", Enumerable.Repeat("""{ "stat": "Damage", "op": "mul", "value": 1.01 }""", 13)) + "] }",
            "mods.json", log);
        Check(tooMany.Count == 0 && Logged($"more than {CustomCards.MaxMods} mods"), "Custom card: too many mods is rejected");
        var boundedStats = Enum.GetValues<Stat>().Where(s => CustomCards.AddLimit(s) < CustomCards.DefaultAddLimit).ToList();
        Check(boundedStats.Contains(Stat.Size) && boundedStats.Contains(Stat.Projectiles) && boundedStats.Contains(Stat.Bounces), "Custom card: count stats have tight limits");

        // Network form: what the client rebuilds is identical to what the host holds, down to the float bits.
        var hostSet = new List<CardDef>(one) { list[0], list[1], huge };
        hostSet.Add(new CardDef { Id = 1030, Name = "Odd floats", Description = "ünïcödé ✓", Category = CardCategory.Body, Rarity = CardRarity.Rare, EffectId = null,
            Mods = new[] { new StatMod(Stat.Damage, ModOp.Mul, 1.1f), new StatMod(Stat.BulletGravity, ModOp.Mul, -0.8f), new StatMod(Stat.Spread, ModOp.Add, 0.12345679f) } });
        log.Clear();
        var back = CustomCards.Deserialize(CustomCards.Serialize(hostSet), log);
        bool Same(CardDef a, CardDef b) => a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Category == b.Category && a.Rarity == b.Rarity
            && a.EffectId == b.EffectId && a.Mods.Length == b.Mods.Length
            && a.Mods.Zip(b.Mods).All(p => p.First.Stat == p.Second.Stat && p.First.Op == p.Second.Op
                                        && BitConverter.SingleToInt32Bits(p.First.Value) == BitConverter.SingleToInt32Bits(p.Second.Value));
        Check(back.Count == hostSet.Count && hostSet.Zip(back).All(p => Same(p.First, p.Second)) && log.Count == 0,
            "Custom card sync: serialize then deserialize gives identical definitions", string.Join(" | ", log));
        Check(CustomCards.Deserialize(CustomCards.Serialize(new List<CardDef>()), log).Count == 0, "Custom card sync: an empty set round-trips");
        log.Clear();
        var bytes = CustomCards.Serialize(hostSet);
        Check(CustomCards.Deserialize(bytes[..(bytes.Length / 2)], log).Count == 0 && Logged("cut short"), "Custom card sync: a truncated message gives no cards, no crash");
        log.Clear();
        var sneaky = new List<CardDef> { new() { Id = 5, Name = "Built-in clash", EffectId = "dash" },
            new() { Id = 1040, Name = "Big", Mods = new[] { new StatMod(Stat.Projectiles, ModOp.Add, 9999) } } };
        var checkedBack = CustomCards.Deserialize(CustomCards.Serialize(sneaky), log);
        Check(checkedBack.Count == 1 && checkedBack[0].Mods[0].Value == 24 && Logged("id must be from"), "Custom card sync: the client re-checks the host's cards");

        // Registry: custom cards join the roll pool after the built-ins; reset brings back exactly the built-ins.
        var builtIns = CardRegistry.All.ToList();
        CardRegistry.SetCustom(hostSet);
        Check(CardRegistry.All.Count == builtIns.Count + hostSet.Count && CardRegistry.Get(1030) == hostSet[^1] && CardRegistry.IsCustom(1030)
              && !CardRegistry.IsCustom(69) && CardRegistry.Custom.SequenceEqual(hostSet), "Custom card registry: registered cards are in All and Get");
        Check(!CardRegistry.Register(new CardDef { Id = 7, Name = "Clash" }) && CardRegistry.Get(7).Name == "Tank"
              && !CardRegistry.Register(new CardDef { Id = 1000, Name = "Again" }), "Custom card registry: taken ids are refused");
        var rolled = new HashSet<int>();
        var rr = new Rng(3);
        for (int i = 0; i < 400; i++) foreach (int id in CardRegistry.RollOptions(rr, 5, null, 0.2f)) rolled.Add(id); // 1030 is rare
        Check(hostSet.All(c => rolled.Contains(c.Id)), "Custom card registry: custom cards come up in rolls");
        var stats = StatBlock.Compute(new[] { CardRegistry.Get(1000) });
        Check(stats.Int(Stat.AirJumps) == 2 && Math.Abs(stats[Stat.MaxHp] - 90) < 0.01f, "Custom card registry: a custom card's mods apply", $"{stats[Stat.AirJumps]}");
        CardRegistry.SetCustom(new[] { hostSet[0] });
        Check(CardRegistry.All.Count == builtIns.Count + 1 && CardRegistry.Get(1030) == null, "Custom card registry: SetCustom replaces the whole custom set");
        CardRegistry.ResetToBuiltIns();
        Check(CardRegistry.All.SequenceEqual(builtIns) && CardRegistry.Get(1000) == null && CardRegistry.Custom.Count == 0
              && builtIns.All(c => CardRegistry.Get(c.Id) == c), "Custom card registry: reset restores exactly the built-ins");
    }
}
