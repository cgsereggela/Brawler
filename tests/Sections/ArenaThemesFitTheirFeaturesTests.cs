using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class ArenaThemesFitTheirFeaturesTests
{
    public static void Run()
    {
        // Theme choice lives in Logic; it hashes the seed so neighboring seeds get different themes.
        bool sameAsBefore = Enumerable.Range(-500, 5000).All(seed =>
            (int)ThemeRules.ForSeed(seed) == (int)((uint)(seed * 2654435761u) >> 8) % ThemeRules.Count);
        Check(sameAsBefore && ThemeRules.Count == Enum.GetValues<ThemeId>().Length, "ThemeRules.ForSeed hashes the seed onto the themes");

        // Godot's ArenaTheme.All must list the same themes in ThemeId order, or the HUD name and colors would not match the rules.
        string dir = AppContext.BaseDirectory, themeFile = null;
        while (dir != null && themeFile == null)
        {
            var f = System.IO.Path.Combine(dir, "godot", "scripts", "Game", "ArenaTheme.cs");
            if (System.IO.File.Exists(f)) themeFile = f;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        var godotNames = themeFile == null ? new List<string>()
            : System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText(themeFile), "new\\(\"([^\"#][^\"]*)\",")
                .Select(m => m.Groups[1].Value).ToList();
        Check(godotNames.SequenceEqual(ThemeRules.Names), "Godot ArenaTheme.All matches ThemeRules.Names in order", string.Join(", ", godotNames));

        int maps = 1400, wrong = 0;
        var magma = new int[ThemeRules.Count]; var poison = new int[ThemeRules.Count]; var sawN = new int[ThemeRules.Count];
        var barrelN = new int[ThemeRules.Count]; var seen = new int[ThemeRules.Count];
        for (int seed = 1; seed <= maps; seed++)
        {
            var m = MapGenerator.Generate(seed * 7919, 4);
            var theme = ThemeRules.ForSeed(m.Seed); var rules = ThemeRules.Rules(theme); int t = (int)theme;
            seen[t]++;
            foreach (var h in m.Hazards)
            {
                if (!rules.Allows(h.Kind)) wrong++;
                if (h.Kind == HazardKind.Magma) magma[t]++; else poison[t]++;
            }
            if (m.Saws.Count > 0 && !rules.AllowsSaws) wrong++;
            sawN[t] += m.Saws.Count;
            barrelN[t] += m.Props.Count(p => p.Kind == PropKind.Barrel);
        }
        string Row(int t) => $"{ThemeRules.Names[t]}: {seen[t]} maps, {magma[t]} magma, {poison[t]} poison, {sawN[t]} saws, {barrelN[t]} barrels";
        Check(wrong == 0, "No arena gets a feature its theme forbids", $"{wrong} bad");
        Check(seen.All(n => n > maps / 14), "Every theme comes up", string.Join(" / ", seen));
        var dr = (int)ThemeId.DeepReef; var tg = (int)ThemeId.ToxicGrove; var mc = (int)ThemeId.MagmaCore; var fb = (int)ThemeId.FrostBite;
        Check(sawN[dr] == 0 && magma[dr] == 0 && magma[tg] == 0 && poison[mc] == 0 && magma[fb] + poison[fb] == 0,
            "Deep Reef: no saws or magma; Toxic Grove: no magma; Magma Core: no poison; Frost Bite: no pools",
            $"{Row(dr)}; {Row(tg)}; {Row(mc)}; {Row(fb)}");
        // Variety: every theme still gets at least two kinds of hazard-ish extras (pools, saws or barrels).
        bool varied = Enumerable.Range(0, ThemeRules.Count).All(t =>
            (magma[t] + poison[t] > 0 ? 1 : 0) + (sawN[t] > 0 ? 1 : 0) + (barrelN[t] > 0 ? 1 : 0) >= 2);
        Check(varied, "Every theme keeps some variety", string.Join("; ", Enumerable.Range(0, ThemeRules.Count).Select(Row)));

        // Same seed, same arena, themed extras included.
        var a1 = MapGenerator.Generate(4242, 3); var a2 = MapGenerator.Generate(4242, 3);
        bool same = a1.Props.SequenceEqual(a2.Props) && a1.Hazards.SequenceEqual(a2.Hazards) && a1.Saws.SequenceEqual(a2.Saws)
            && a1.Movers.SequenceEqual(a2.Movers);
        Check(same, "Themed extras are deterministic from the seed");
    }
}
