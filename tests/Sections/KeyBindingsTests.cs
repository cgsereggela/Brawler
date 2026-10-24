using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class KeyBindingsTests
{
    public static void Run()
    {
        // A stand-in for the engine's key names: single letters are their char code, plus a couple of named keys. Esc is reserved.
        var names = new Dictionary<string, long> { ["Space"] = 32, ["Shift"] = 4194325, ["Left"] = 4194319 };
        string KeyName(long c) => names.FirstOrDefault(p => p.Value == c).Key ?? ((char)c).ToString();
        long KeyCode(string s) => names.TryGetValue(s, out long c) ? c : s.Length == 1 && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]) : 0;
        Binding K(char c) => Binding.Key(c);

        var defaults = new BindTable();
        defaults.Assign("move_left", 0, K('A')); defaults.Assign("move_left", 1, Binding.Key(4194319));
        defaults.Assign("jump", 0, K('W')); defaults.Assign("jump", 1, Binding.Key(32));
        defaults.Assign("shoot", 0, Binding.Mouse(1));
        defaults.Assign("block", 0, Binding.Key(4194325)); defaults.Assign("block", 1, Binding.Mouse(2));

        var t = defaults.Clone();
        string from = t.Assign("jump", 0, K('K'));
        Check(from == null && t.Get("jump", 0) == K('K') && t.Get("jump", 1) == Binding.Key(32), "Rebinding a free key replaces just that slot");
        from = t.Assign("jump", 2, Binding.Key(4194325));
        Check(from == "block" && t.Find(Binding.Key(4194325)) == ("jump", 2) && t.Get("block", 0).IsNone && t.Get("block", 1) == Binding.Mouse(2),
            "A key taken from another action is moved out of it");
        from = t.Assign("jump", 1, K('K'));
        Check(from == null && t.Get("jump", 1) == K('K') && t.Get("jump", 0).IsNone, "Rebinding within an action moves the key, no duplicate");
        t.Assign("jump", 0, Binding.Mouse(1));
        Check(t.IsUnbound("shoot") && !t.IsUnbound("jump"), "Taking an action's only input leaves it unbound");
        Check(defaults.Get("jump", 0) == K('W') && defaults.Get("shoot", 0) == Binding.Mouse(1), "Clone is independent");
        Check(Binding.Mouse(4).IsNone && Binding.Mouse(5).IsNone && Binding.MouseNumber(8) == 4 && Binding.MouseButton(5) == 9,
            "Mouse wheel can't be bound; side buttons read as Mouse 4 and 5");

        // Round trip through tokens.
        var saved = BindTable.Actions.ToDictionary(a => a, a => t.Tokens(a, KeyName));
        Check(saved["jump"].SequenceEqual(new[] { "Mouse 1", "K", "Shift" }) && saved["shoot"].All(s => s == ""), "Tokens are readable",
            string.Join(",", saved["jump"]));
        var back = BindTable.Load(a => saved[a], KeyCode, defaults);
        Check(back.SameAs(t), "Save then load gives the same table");

        // Bad data: each broken action falls back to its defaults; good actions are kept.
        var bad = new Dictionary<string, string[]>
        {
            ["move_left"] = new[] { "L", "" }, ["jump"] = new[] { "W", "Nonsense" }, ["shoot"] = new[] { "Mouse 9" },
            ["block"] = new[] { "a", "b", "c", "d" },
        };
        var loaded = BindTable.Load(a => bad.TryGetValue(a, out var v) ? v : null, KeyCode, defaults);
        Check(loaded.Get("move_left", 0) == K('L') && loaded.Get("move_left", 1).IsNone, "Valid saved row is kept, short rows pad with empty slots");
        Check(loaded.Get("jump", 0) == K('W') && loaded.Get("jump", 1) == Binding.Key(32), "Unknown key name falls back to defaults");
        Check(loaded.Get("shoot", 0) == Binding.Mouse(1) && loaded.Get("block", 0) == Binding.Key(4194325), "Bad mouse number or too many slots fall back to defaults");
        Check(loaded.IsUnbound("move_right"), "Missing action uses its (empty) defaults");

        // Duplicates in a hand-edited file stay with the first action.
        var dup = BindTable.Load(a => a == "move_left" ? new[] { "W" } : null, KeyCode, defaults);
        Check(dup.Get("move_left", 0) == K('W') && dup.Get("jump", 0).IsNone && dup.Get("jump", 1) == Binding.Key(32), "A key listed twice goes to the first action only");
    }
}
