using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;

int failures = 0;
void Check(bool cond, string name, string detail = "")
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    if (!cond) failures++;
}

// ---------- RNG ----------
{
    var a = new Rng(42); var b = new Rng(42);
    bool same = Enumerable.Range(0, 1000).All(_ => a.NextUInt() == b.NextUInt());
    Check(same, "Rng is deterministic for equal seeds");
    var r = new Rng(7);
    bool inRange = Enumerable.Range(0, 10000).All(_ => { int v = r.Range(3, 9); return v >= 3 && v <= 9; });
    Check(inRange, "Rng.Range stays inclusive within bounds");
}

// ---------- Stats ----------
{
    var baseStats = StatBlock.Base();
    var none = StatBlock.Compute(Array.Empty<CardDef>());
    Check(none[Stat.Damage] == baseStats[Stat.Damage], "No cards = base stats");

    var tank = CardRegistry.Get(7);
    var once = StatBlock.Compute(new[] { tank });
    Check(Math.Abs(once[Stat.MaxHp] - 160) < 0.01f, "Tank adds 60 HP", $"{once[Stat.MaxHp]}");

    // Adds apply before multipliers regardless of pick order: (100 + 60) * 0.85 = 136
    var vamp = CardRegistry.Get(14);
    var ab = StatBlock.Compute(new[] { tank, vamp });
    var ba = StatBlock.Compute(new[] { vamp, tank });
    Check(Math.Abs(ab[Stat.MaxHp] - 136) < 0.01f && Math.Abs(ba[Stat.MaxHp] - 136) < 0.01f,
        "Stacking order: adds before multipliers, order-independent", $"{ab[Stat.MaxHp]} / {ba[Stat.MaxHp]}");

    var bouncy = CardRegistry.Get(1);
    var b3 = StatBlock.Compute(new[] { bouncy, bouncy, bouncy });
    Check(b3.Int(Stat.Bounces) == 6, "Bouncy stacks additively", $"{b3.Int(Stat.Bounces)}");

    var cd = StatBlock.Compute(Enumerable.Repeat(CardRegistry.Get(11), 10));
    Check(cd[Stat.ReloadTime] >= 0.2f, "Clamps keep reload time sane", $"{cd[Stat.ReloadTime]}");

    Check(CardRegistry.All.Select(c => c.Id).Distinct().Count() == CardRegistry.All.Count, "Card ids are unique");
    Check(CardRegistry.All.Count >= 15, "At least 15 cards", $"{CardRegistry.All.Count}");

    var opts = CardRegistry.RollOptions(new Rng(1), 5);
    Check(opts.Distinct().Count() == 5, "Rolled card options are distinct");
}

// ---------- Map generation ----------
{
    var m1 = MapGenerator.Generate(12345, 4);
    var m2 = MapGenerator.Generate(12345, 4);
    bool same = m1.Solids.SequenceEqual(m2.Solids) && m1.Spawns.SequenceEqual(m2.Spawns);
    Check(same, "Same seed produces identical map");

    int seeds = 3000;
    foreach (int players in new[] { 2, 3, 4 })
    {
        int fallbacks = 0, totalAttempts = 0, maxAttempts = 0, bad = 0;
        for (int s = 0; s < seeds; s++)
        {
            var m = MapGenerator.Generate(s * 7919 + players, players);
            if (m.UsedFallback) fallbacks++;
            totalAttempts += m.Attempts;
            maxAttempts = Math.Max(maxAttempts, m.Attempts);
            if (m.Spawns.Count != players || !MapGenerator.SpawnsMutuallyReachable(m)) bad++;
            // Spawns must not be inside a solid.
            foreach (var sp in m.Spawns)
                if (m.Solids.Any(r => r.Contains(sp.X, sp.Y - 8))) bad++;
        }
        Check(bad == 0, $"{players}p maps: valid spawns, mutually reachable", $"{bad} bad");
        Check(fallbacks <= seeds / 200, $"{players}p maps: fallback rate under 0.5%",
            $"{fallbacks}/{seeds} fallbacks, avg attempts {totalAttempts / (float)seeds:F1}, max {maxAttempts}");
    }

    // Reachability primitive.
    var floor = new Surface(8, 632, 344);
    Check(MapGenerator.CanTraverse(floor, new Surface(100, 160, 304)), "Can jump up 40px onto a nearby platform");
    Check(!MapGenerator.CanTraverse(floor, new Surface(100, 160, 260)), "Cannot jump up 84px");
    Check(MapGenerator.CanTraverse(new Surface(100, 160, 260), floor), "Can always drop down");
}

// ---------- Sweep ----------
{
    var rects = new List<RectI> { new(100, 0, 20, 100) };
    var hit = Sweep.Cast(new Vector2(50, 50), new Vector2(100, 0), 2, rects);
    Check(hit is { } h && Math.Abs(h.T - 0.48f) < 0.001f && h.Normal == new Vector2(-1, 0),
        "Sweep hits wall with correct time and normal", hit?.ToString() ?? "miss");
    var miss = Sweep.Cast(new Vector2(50, 150), new Vector2(100, 0), 2, rects);
    Check(miss == null, "Sweep misses when passing below");
    var floorHit = Sweep.Cast(new Vector2(110, -20), new Vector2(0, 40), 1, rects);
    Check(floorHit is { } fh && fh.Normal == new Vector2(0, -1), "Sweep detects top-face hit");
    var refl = Sweep.Reflect(new Vector2(3, 4), new Vector2(-1, 0));
    Check(refl == new Vector2(-3, 4), "Reflect flips the normal component");
    Check(Sweep.SegmentTouchesRect(new Vector2(0, 5), new Vector2(20, 5), 1, 10, 0, 4, 10), "Segment crosses player box");
}

// ---------- Card stats ----------
{
    // 10 two-player battles: the Homing (5) holder always beats the Tank (7) holder.
    MatchRecord Make(int seed)
    {
        var m = new MatchRecord { Id = $"m{seed}", Mode = "sim", CardsHash = StatsJson.CardsHash(), Complete = true, Winner = 0 };
        m.Players.Add(new PlayerRec { Slot = 0, Bot = true, FinalCards = { 5, 5 } });
        m.Players.Add(new PlayerRec { Slot = 1, Bot = false, FinalCards = { 7 } });
        for (int i = 0; i < 10; i++)
        {
            var b = new BattleRec { N = i + 1, Round = 1, FightSec = 4, Winner = 0 };
            b.Players.Add(new BattlePlayerRec { Slot = 0, Cards = { 5, 5 }, DamageDealt = 100 });
            b.Players.Add(new BattlePlayerRec { Slot = 1, Cards = { 7 }, DiedAt = 4 });
            m.Battles.Add(b);
        }
        m.Picks.Add(new PickRec { Slot = 1, Bot = false, Offered = { 7, 1, 2 }, Picked = 7 });
        return m;
    }

    var line = StatsJson.ToLine(Make(1));
    var parsed = StatsJson.ParseLines(new[] { line, "{broken", "" }, out int skipped);
    Check(parsed.Count == 1 && skipped == 1 && parsed[0].Battles.Count == 10 && parsed[0].Battles[0].Players[0].Cards.Count == 2,
        "Stats JSON round-trips and skips broken lines", $"parsed {parsed.Count}, skipped {skipped}");

    var data = StatsReport.Build(new[] { Make(1), Make(2) }, new StatsFilter { MinSamples = 5 });
    var homing = data.Cards.First(c => c.Card.Id == 5);
    var tank = data.Cards.First(c => c.Card.Id == 7);
    Check(Math.Abs(homing.Battle.Ratio - 2.0) < 1e-9 && tank.Battle.Ratio == 0, "Win ratio vs fair share",
        $"homing {homing.Battle.Ratio}, tank {tank.Battle.Ratio}");
    Check(homing.Battle.N == 20 && homing.ByStacks.ContainsKey(2) && !homing.ByStacks.ContainsKey(1), "Stacks count once per battle, bucketed by copies");
    Check(Math.Abs(homing.AvgWinSec - 4) < 1e-9 && Math.Abs(homing.Per(homing.Dealt) - 100) < 1e-9, "Win time and per-battle damage");
    Check(Math.Abs(tank.PickRatioHuman - 3.0) < 1e-9 && tank.Offered == 2, "Pick ratio vs random choice", $"{tank.PickRatioHuman}");
    Check(Math.Abs(homing.Match.Ratio - 2.0) < 1e-9, "Match win ratio from final cards");

    var humans = StatsReport.Build(new[] { Make(1) }, new StatsFilter { Bots = false });
    Check(humans.Cards.First(c => c.Card.Id == 5).Battle.N == 0 && humans.Cards.First(c => c.Card.Id == 7).Battle.N == 10,
        "Human/bot filter applies per player");
    var other = StatsReport.Build(new[] { Make(1) }, new StatsFilter { CardsHash = "deadbeef" });
    Check(other.Matches == 0, "Card-table hash filter excludes other balance versions");
    Check(StatsReport.Markdown(data).Contains("| Homing |"), "Markdown report renders");
}

// ---------- Appearance ----------
{
    var a = Appearance.Default();
    a[LookPart.Eyes] = 5;
    a[LookPart.Hat] = 9;
    a[LookPart.HatColor] = 3;
    Check(Appearance.FromBytes(a.ToBytes()).Equals(a) && Appearance.Decode(a.Encode()).Equals(a), "Look round-trips through bytes and profile text");

    var w = new Appearance();
    w[LookPart.Hat] = -1;
    w[LookPart.Eyes] = Appearance.Count(LookPart.Eyes);
    Check(w.Hat == Appearance.Count(LookPart.Hat) - 1 && w.Eyes == 0, "Look options wrap in both directions");

    var future = a.ToBytes().Concat(new byte[] { 7, 7 }).ToArray();
    var old = a.ToBytes().Take(3).ToArray();
    Check(Appearance.FromBytes(future).Equals(a) && Appearance.FromBytes(old).Hat == 0 && Appearance.FromBytes(new byte[] { 200 }).Eyes == 200 % Appearance.Count(LookPart.Eyes),
        "Look bytes tolerate extra, missing and out-of-range parts");
    Check(Appearance.Decode("garbage,,x").Eyes == 0 && Appearance.Decode("").Equals(Appearance.Default()), "Bad profile text falls back safely");

    var rng = new Rng(3);
    bool inRange = Enumerable.Range(0, 500).Select(_ => Appearance.Random(rng))
        .All(l => Enum.GetValues<LookPart>().All(p => l[p] >= 0 && l[p] < Appearance.Count(p)));
    Check(inRange, "Random looks stay within every part's options");
    Check(Appearance.Count(LookPart.HairColor) == Appearance.Dyes.Length, "Dye names match the dye palette");
}

Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
