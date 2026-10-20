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

// ---------- Jump rules ----------
{
    // Jumping from the ground spends the ground jump; card air jumps still follow.
    var j = new JumpCharges(); j.Land(1);
    var seq = new[] { j.Use(true, false, false, true), j.Use(false, false, false, true), j.Use(false, false, false, true) };
    Check(seq[0] == JumpKind.Ground && seq[1] == JumpKind.Air && seq[2] == JumpKind.None, "Ground jump, then air jumps", string.Join(",", seq));

    // Walking off a ledge keeps the ground jump for any time in the air (no coyote needed), then air jumps apply.
    j = new JumpCharges(); j.Land(1);
    var ledge = new[] { j.Use(false, false, false, true), j.Use(false, false, false, true), j.Use(false, false, false, true) };
    Check(ledge[0] == JumpKind.Ledge && ledge[1] == JumpKind.Air && ledge[2] == JumpKind.None, "Ledge jump kept after walking off", string.Join(",", ledge));
    j = new JumpCharges(); j.Land(0);
    Check(j.Use(false, false, false, true) == JumpKind.Ledge && j.Use(false, false, false, true) == JumpKind.None, "Ledge jump without air-jump cards, once");

    // A wall beats the ledge jump and costs nothing; the ledge jump is still there afterwards.
    j = new JumpCharges(); j.Land(0);
    Check(j.Use(false, true, false, true) == JumpKind.Wall && j.Left == 1 && j.Use(false, false, false, true) == JumpKind.Ledge, "Wall jump keeps the ledge jump");
    // ...but just off the floor a press is still a ground jump.
    j = new JumpCharges(); j.Land(0);
    Check(j.Use(true, true, false, true) == JumpKind.Ground && j.Left == 0, "Coyote ground jump beats wall jump");

    // Jetpack only once jumps run out, and only on a fresh press; landing restores everything.
    j = new JumpCharges(); j.Land(0); j.Ground = false;
    Check(j.Use(false, false, true, false) == JumpKind.None && j.Use(false, false, true, true) == JumpKind.Jet, "Jetpack after jumps, fresh press only");
    j.Land(2);
    Check(j.Ground && j.Air == 2 && j.Left == 3, "Landing restores ground and air jumps");
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

    var styles = new HashSet<MapStyle>();
    int pads = 0, badPads = 0;
    for (int s = 0; s < 400; s++)
    {
        var m = MapGenerator.Generate(s * 31 + 5, 4);
        styles.Add(m.Style);
        pads += m.Pads.Count;
        foreach (var p in m.Pads)
        {
            if (!m.Surfaces.Any(sf => sf.Y == p.Y && p.X1 >= sf.X1 && p.X2 <= sf.X2)) badPads++;
            if (m.Spawns.Any(sp => sp.Y == p.Y && sp.X > p.X1 - 24 && sp.X < p.X2 + 24)) badPads++;
        }
    }
    Check(styles.Count == Enum.GetValues<MapStyle>().Length, "Every map style shows up", string.Join(",", styles));
    Check(pads > 100 && badPads == 0, "Jump pads sit on surfaces, away from spawns", $"{pads} pads, {badPads} bad");
    Check(MapLayout.Width == 800 && MapLayout.Height == 450, "Arena is 800x450");

    // A pad launch still clears the planned climb under the softer gravity.
    float padApex = MapGenerator.PadVelocity * MapGenerator.PadVelocity / (2 * MapGenerator.BaseGravity);
    Check(padApex >= MapGenerator.PadRise + 5 && MapGenerator.BaseGravity == StatBlock.Base()[Stat.Gravity],
        "Pad launch apex covers PadRise at base gravity", $"apex {padApex:0}, rise {MapGenerator.PadRise}");
    float jumpApex = StatBlock.Base()[Stat.JumpVelocity] * StatBlock.Base()[Stat.JumpVelocity] / (2 * StatBlock.Base()[Stat.Gravity]);
    Check(jumpApex >= MapGenerator.MaxRise + 12, "Base jump clears MaxRise with margin", $"apex {jumpApex:0}");
}

// ---------- Arena features ----------
{
    var a1 = MapGenerator.Generate(4242, 4);
    var a2 = MapGenerator.Generate(4242, 4);
    Check(a1.Props.SequenceEqual(a2.Props) && a1.Hazards.SequenceEqual(a2.Hazards) && a1.Saws.SequenceEqual(a2.Saws) && a1.Movers.SequenceEqual(a2.Movers),
        "Same seed produces identical arena features");

    int maps = 2000, crates = 0, barrels = 0, glass = 0, pools = 0, saws = 0, movers = 0, bad = 0;
    var why = new List<string>();
    void Bad(int seed, string what) { bad++; if (why.Count < 5) why.Add($"seed {seed}: {what}"); }
    for (int s = 0; s < maps; s++)
    {
        int seed = s * 104729 + 11;
        var m = MapGenerator.Generate(seed, 2 + s % 3);
        foreach (var p in m.Props)
        {
            if (p.Kind == PropKind.Crate) crates++; else if (p.Kind == PropKind.Barrel) barrels++; else glass++;
            if (m.Solids.Any(r => r.Intersects(p.Rect))) Bad(seed, $"{p.Kind} inside a solid");
            if (m.Props.Any(o => o != p && o.Rect.Intersects(p.Rect))) Bad(seed, $"{p.Kind} overlaps another prop");
            if (m.Spawns.Any(sp => Math.Abs(sp.X - (p.X + p.W / 2)) < 26 + p.W / 2 && sp.Y - 40 < p.Y + p.H && p.Y < sp.Y + 6))
                Bad(seed, $"{p.Kind} on a spawn");
            if (m.Movers.Any(mv => mv.Lane.Grow(0, 20).Intersects(p.Rect))) Bad(seed, $"{p.Kind} in a mover's lane");
        }
        foreach (var h in m.Hazards)
        {
            pools++;
            if (!m.Surfaces.Any(sf => sf.Y == h.Y && h.X1 >= sf.X1 && h.X2 <= sf.X2)) Bad(seed, "pool not on a surface");
            if (m.Spawns.Any(sp => sp.Y == h.Y && sp.X > h.X1 - 26 && sp.X < h.X2 + 26)) Bad(seed, "pool at a spawn");
            if (m.Pads.Any(pd => pd.Y == h.Y && pd.X2 > h.X1 - 10 && pd.X1 < h.X2 + 10)) Bad(seed, "pool on a jump pad");
        }
        foreach (var sw in m.Saws)
        {
            saws++;
            if (!m.Surfaces.Any(sf => Math.Abs(sf.Y - 2 - sw.Y1) < 0.01f && sw.X1 >= sf.X1 && sw.X2 <= sf.X2)) Bad(seed, "saw off its surface");
            if (m.Spawns.Any(sp => sp.X > sw.X1 - 40 && sp.X < sw.X2 + 40 && Math.Abs(sp.Y - sw.Y1) < 30)) Bad(seed, "saw near a spawn");
        }
        foreach (var mv in m.Movers)
        {
            movers++;
            var room = new RectI(mv.Lane.X, mv.Y - 30, mv.Lane.W, mv.H + 30 + 20);
            if (m.Solids.Any(r => r.Intersects(room))) Bad(seed, "mover lane blocked");
            if (mv.Period <= 0 || mv.X2 <= mv.X1) Bad(seed, "mover goes nowhere");
        }
    }
    Check(bad == 0, "Arena features stay clear of solids, spawns, pads and each other", $"{bad} bad. {string.Join("; ", why)}");
    Check(crates > maps && barrels > maps / 4 && glass > maps / 2 && pools > maps / 3 && saws > maps / 4 && movers > maps / 5,
        "Every arena feature shows up often", $"per {maps} maps: {crates} crates, {barrels} barrels, {glass} glass, {pools} pools, {saws} saws, {movers} movers");

    var track = new SawTrack(100, 50, 200, 50, 9, 4, 0);
    Check(Vector2.Distance(track.At(0), new Vector2(100, 50)) < 0.01f && Vector2.Distance(track.At(2), new Vector2(200, 50)) < 0.01f
        && Vector2.Distance(track.At(4), new Vector2(100, 50)) < 0.01f, "Saws shuttle end to end and back each period");
    var mover = new Mover(40, 120, 300, 40, 8, 3, 0.25f);
    Check(Math.Abs(mover.XAt(0) - 80) < 0.01f && Enumerable.Range(0, 100).All(i => mover.XAt(i * 0.07f) is >= 40 and <= 120),
        "Movers stay inside their lane", $"{mover.XAt(0)}");

    // Reachability primitive.
    var floor = new Surface(8, 632, 344);
    Check(MapGenerator.CanTraverse(floor, new Surface(100, 160, 304)), "Can jump up 40px onto a nearby platform");
    Check(!MapGenerator.CanTraverse(floor, new Surface(100, 160, 260)), "Cannot jump up 84px");
    Check(MapGenerator.CanTraverse(new Surface(100, 160, 260), floor), "Can always drop down");

    var towerMap = new MapLayout();
    towerMap.Solids.Add(new RectI(300, 314, 32, 120));
    var leftFloor = new Surface(8, 300, 434);
    var rightFloor = new Surface(332, 792, 434);
    Check(MapGenerator.CanTraverse(leftFloor, rightFloor) && !MapGenerator.CanTraverse(towerMap, leftFloor, rightFloor),
        "A tower between two floor segments blocks walking across");
    var bridge = new Surface(240, 290, 394);
    var pathMap = new MapLayout();
    pathMap.Surfaces.AddRange(new[] { leftFloor, bridge, new Surface(300, 332, 354), rightFloor });
    pathMap.Solids.Add(new RectI(300, 354, 32, 80));
    Check(MapGenerator.NextHop(pathMap, 0, 3) == 1, "Path finding climbs the ledge to get over a tower",
        $"next hop {MapGenerator.NextHop(pathMap, 0, 3)}");

    // Size-aware graph: a shelf 32px over the floor is fine for a normal body but splits it for a Giant.
    var lowMap = new MapLayout();
    lowMap.Surfaces.Add(new Surface(8, 792, 434));
    lowMap.Solids.Add(new RectI(8, 434, 784, 16));
    lowMap.Solids.Add(new RectI(400, 394, 32, 8));
    Check(MapGenerator.NavLayout(lowMap, 14, 18).Surfaces.Count == 1, "A normal body walks under a 32px shelf");
    var giantNav = MapGenerator.NavLayout(lowMap, 25, 33);
    Check(giantNav.Surfaces.Count == 2 && MapGenerator.NextHop(giantNav, 0, 1) == -1,
        "A Giant can't walk under a low shelf", $"{giantNav.Surfaces.Count} surfaces");
    // The generator splits the floor under a bar 24px up (less than its 26px headroom); a normal body still fits.
    var barMap = new MapLayout();
    barMap.Surfaces.AddRange(new[] { new Surface(8, 300, 434), new Surface(380, 792, 434) });
    barMap.Solids.Add(new RectI(8, 434, 784, 16));
    barMap.Solids.Add(new RectI(300, 394, 80, 16));
    Check(MapGenerator.NextHop(barMap, 0, 1) == -1 && MapGenerator.NextHop(MapGenerator.NavLayout(barMap, 14, 18), 0, 1) == 1,
        "Bots know a normal body fits under a 24px bar");
    // A floor pocket between two 160px towers is a dead end, unless a jump pad in it gets you onto a tower top.
    var pocket = new MapLayout();
    pocket.Surfaces.AddRange(new[] { new Surface(648, 720, 434), new Surface(608, 648, 274) });
    pocket.Solids.AddRange(new[] { new RectI(8, 434, 784, 16), new RectI(608, 274, 40, 160), new RectI(720, 274, 24, 160) });
    pocket.Pads.Add(new JumpPad(664, 688, 434));
    Check(MapGenerator.NextHop(pocket, 0, 1) == -1 && MapGenerator.NextHop(MapGenerator.NavLayout(pocket, 14, 18), 0, 1) == 1,
        "Bots plan with jump pads (map validation doesn't)");
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
    var two = new List<RectI> { new(300, 0, 10, 100), new(100, 0, 20, 100) };
    Check(Sweep.Cast(new Vector2(50, 50), new Vector2(400, 0), 2, two) is { Index: 1 }, "Sweep reports which rect it hit first");
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

// ---------- Audio synthesis ----------
// Pass --wav=DIR to also write every sound and song as .wav files for listening.
{
    string wavDir = args.FirstOrDefault(a => a.StartsWith("--wav="))?["--wav=".Length..];
    if (wavDir != null) System.IO.Directory.CreateDirectory(wavDir);
    static (float peak, float rms, bool finite) Measure(float[] b)
    {
        float p = 0; double sq = 0; bool ok = true;
        foreach (var v in b) { if (!float.IsFinite(v)) ok = false; p = Math.Max(p, Math.Abs(v)); sq += v * v; }
        return (p, (float)Math.Sqrt(sq / Math.Max(1, b.Length)), ok);
    }

    bool sfxOk = true;
    var detail = new List<string>();
    foreach (var name in Brawler.Logic.Synth.SfxBank.Names)
    {
        var takes = Brawler.Logic.Synth.SfxBank.Render(name);
        for (int k = 0; k < takes.Count; k++)
        {
            var (p, rms, finite) = Measure(takes[k]);
            if (!finite || p > 1f || rms < 0.005f) { sfxOk = false; detail.Add($"{name}#{k} peak {p:0.00} rms {rms:0.000}"); }
            if (wavDir != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(wavDir, $"sfx_{name}_{k}.wav"), Brawler.Logic.Synth.Dsp.ToWav(takes[k], 1));
        }
    }
    Check(sfxOk, "Every sound effect renders finite, unclipped and audible", string.Join("; ", detail));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (var song in new[] { Brawler.Logic.Synth.MusicGen.Lobby(), Brawler.Logic.Synth.MusicGen.Battle() })
    {
        var buf = Brawler.Logic.Synth.MusicGen.Render(song);
        var (p, rms, finite) = Measure(buf);
        int expected = (int)MathF.Round(song.Bars * 16 * song.StepSeconds * Brawler.Logic.Synth.Dsp.Rate) * 2;
        // The seam: wrapping from the last frame to the first may be no sharper than the steepest step inside the
        // loop (the loop starts on a kick and crash, so a sharp step there is the downbeat, not a click).
        float seam = Math.Abs(buf[0] - buf[^2]) + Math.Abs(buf[1] - buf[^1]), steepest = 0;
        for (int i = 2; i < buf.Length - 1; i += 2) steepest = Math.Max(steepest, Math.Abs(buf[i] - buf[i - 2]) + Math.Abs(buf[i + 1] - buf[i - 1]));
        Check(finite && p <= 0.9f && rms > 0.05f && buf.Length == expected && seam <= steepest,
            $"Song '{song.Name}' renders a clean {song.Bars}-bar loop", $"{buf.Length / 2 / 44100f:0.0}s, peak {p:0.00}, rms {rms:0.000}, seam {seam:0.000} vs steepest {steepest:0.000}");
        if (wavDir != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(wavDir, $"music_{song.Name}.wav"), Brawler.Logic.Synth.Dsp.ToWav(buf, 2));
    }
    Console.WriteLine($"      (audio rendered in {sw.ElapsedMilliseconds} ms)");
}

// ---------- Join addresses ----------
{
    var cases = new (string Text, string Host, int Port)[]
    {
        ("1.2.3.4", "1.2.3.4", 24565), (" 1.2.3.4:5000 ", "1.2.3.4", 5000), ("game.example.com:7000", "game.example.com", 7000),
        ("[::1]:6000", "::1", 6000), ("fe80::1", "fe80::1", 24565), ("1.2.3.4:abc", "1.2.3.4", 24565), ("1.2.3.4:70000", "1.2.3.4", 24565),
    };
    var wrong = cases.Where(c => Brawler.Logic.NetAddress.Parse(c.Text, 24565) != (c.Host, c.Port)).Select(c => c.Text).ToList();
    Check(wrong.Count == 0, "Join box parses ip, ip:port, host:port and [ipv6]:port", string.Join(", ", wrong));
    bool shared = new[] { "10.0.0.1", "172.16.5.5", "172.31.0.1", "192.168.1.1", "100.64.0.1", "100.127.255.255" }.All(Brawler.Logic.NetAddress.IsShared)
        && !new[] { "8.8.8.8", "172.32.0.1", "100.128.0.1", "203.0.113.5", "", "nonsense" }.Any(Brawler.Logic.NetAddress.IsShared);
    Check(shared, "Private and carrier-grade NAT addresses are recognised");
}

// ---------- Key bindings ----------
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

// ---------- Terrain: hills, valleys and ramps ----------
{
    var t1 = MapGenerator.Generate(777, 4);
    var t2 = MapGenerator.Generate(777, 4);
    int tseed = Enumerable.Range(0, 200).First(s => MapGenerator.Generate(s, 4).Slopes.Count > 0);
    var a = MapGenerator.Generate(tseed, 4);
    var b = MapGenerator.Generate(tseed, 4);
    Check(a.Slopes.SequenceEqual(b.Slopes) && a.Terrain.SequenceEqual(b.Terrain) && a.Solids.SequenceEqual(b.Solids)
        && a.Ground.Count == b.Ground.Count && a.Ground.Zip(b.Ground).All(p => p.First.SequenceEqual(p.Second))
        && a.Surfaces.SequenceEqual(b.Surfaces) && a.Spawns.SequenceEqual(b.Spawns) && t1.Slopes.SequenceEqual(t2.Slopes),
        "Same seed produces identical terrain", $"seed {tseed}");

    int maps = 3000, withTerrain = 0, slopes = 0, plateaus = 0, bad = 0, unreached = 0, steepest = 0;
    float maxAngle = 0;
    var byStyle = new Dictionary<MapStyle, (int maps, int terrain)>();
    var why = new List<string>();
    void Bad(int seed, string what) { bad++; if (why.Count < 6) why.Add($"seed {seed}: {what}"); }
    for (int s = 0; s < maps; s++)
    {
        int seed = s * 6151 + 3;
        var m = MapGenerator.Generate(seed, 2 + s % 3);
        var st = byStyle.GetValueOrDefault(m.Style);
        bool has = m.Slopes.Count > 0 || m.Terrain.Count > 0;
        byStyle[m.Style] = (st.maps + 1, st.terrain + (has ? 1 : 0));
        if (!has) continue;
        withTerrain++;
        slopes += m.Slopes.Count;
        plateaus += m.Terrain.Count;
        foreach (var sl in m.Slopes)
        {
            maxAngle = Math.Max(maxAngle, sl.Angle);
            if (sl.Angle > TerrainGen.MaxAngle) steepest++;
            if (sl.X2 <= sl.X1 || sl.Y1 == sl.Y2 || sl.Bottom != MapGenerator.FloorY) Bad(seed, $"degenerate slope {sl}");
            // Each end meets a surface at its own height, so it's walkable onto both.
            bool lowEnd = m.Surfaces.Any(f => f.Y == sl.Y1 && f.X2 == sl.X1), highEnd = m.Surfaces.Any(f => f.Y == sl.Y2 && f.X1 == sl.X2);
            if (!lowEnd || !highEnd) Bad(seed, $"slope {sl} has no surface at an end");
        }
        // Spawns on flat ground: on a surface, not on or inside any slope.
        foreach (var sp in m.Spawns)
            if (m.Slopes.Any(sl => sl.Overlaps(sp.X - 7, sp.Y - 18, sp.X + 7, sp.Y - 0.5f)) || MapGenerator.SurfaceIndexOf(m, sp) < 0)
                Bad(seed, $"spawn {sp} not on flat ground");
        // Nothing that should sit on flat ground or float in clear air touches the terrain.
        foreach (var sl in m.Slopes)
        {
            if (m.Solids.Any(r => sl.Intersects(r))) Bad(seed, $"slope {sl} cuts into a solid");
            foreach (var p in m.Props) if (sl.Intersects(p.Rect)) Bad(seed, $"{p.Kind} on a slope");
            foreach (var h in m.Hazards) if (sl.Intersects(h.Rect)) Bad(seed, "pool on a slope");
            foreach (var pd in m.Pads) if (sl.Intersects(new RectI(pd.X1, pd.Y - 90, pd.X2 - pd.X1, 90))) Bad(seed, "pad on or under a slope");
            foreach (var mv in m.Movers) if (sl.Intersects(mv.Lane.Grow(0, 20))) Bad(seed, "mover lane through a slope");
            foreach (var sw in m.Saws) if (sl.Overlaps(sw.X1 - sw.Radius, sw.Y1 - sw.Radius, sw.X2 + sw.Radius, sw.Y1 + sw.Radius - 3)) Bad(seed, "saw in a slope");
        }
        foreach (var r in m.Terrain)
        {
            // Platforms keep clear air above terrain (towers stand away from it).
            if (m.Solids.Any(o => o != r && !m.Terrain.Contains(o) && o.Y < MapGenerator.FloorY && o.Bottom > r.Y - TerrainGen.Clearance && o.X < r.Right && o.Right > r.X))
                Bad(seed, $"platform low over terrain {r}");
            if (m.Hazards.Any(h => h.Y != r.Y && h.Rect.Intersects(r))) Bad(seed, "pool cut into terrain");
        }
        // Every surface the spawns can reach is reachable from every spawn (validation still holds).
        if (!MapGenerator.SpawnsMutuallyReachable(m)) unreached++;
    }
    Check(steepest == 0 && maxAngle <= TerrainGen.MaxAngle, "Slopes stay walkable", $"steepest {maxAngle:0.0} deg, {steepest} too steep");
    Check(bad == 0, "Terrain: slopes meet surfaces, spawns stay flat, nothing intrudes", $"{bad} bad. {string.Join("; ", why)}");
    Check(unreached == 0, "Terrain maps keep every spawn reachable", $"{unreached} unreachable");
    string shares = string.Join(", ", byStyle.OrderBy(p => p.Key).Select(p => $"{p.Key} {100f * p.Value.terrain / p.Value.maps:0}%"));
    Check(withTerrain > maps / 4 && withTerrain < maps * 3 / 4 && byStyle.Values.All(v => v.terrain > v.maps / 6),
        "A good share of maps (every style) have terrain", $"{100f * withTerrain / maps:0}% of {maps}: {shares}; {slopes} slopes, {plateaus} raised stretches");

    // Mirror arenas get mirrored terrain.
    int asym = 0;
    for (int s = 0; s < 600; s++)
    {
        var m = MapGenerator.Generate(s * 37 + 1, 2);
        if (m.Style != MapStyle.Mirror) continue;
        foreach (var sl in m.Slopes)
            if (!m.Slopes.Contains(new Slope(MapLayout.Width - sl.X2, sl.Y2, MapLayout.Width - sl.X1, sl.Y1, sl.Bottom))) asym++;
    }
    Check(asym == 0, "Mirror arenas have symmetric terrain", $"{asym} unmatched slopes");

    // The jump graph walks a ramp both ways; bots plan over it.
    var hill = new MapLayout();
    hill.Solids.Add(new RectI(8, 434, 784, 16));
    hill.Solids.Add(new RectI(240, 402, 64, 32));
    hill.Terrain.Add(new RectI(240, 402, 64, 32));
    hill.Slopes.Add(new Slope(192, 434, 240, 402, 434));
    hill.Slopes.Add(new Slope(304, 402, 352, 434, 434));
    hill.Surfaces.AddRange(new[] { new Surface(8, 192, 434), new Surface(240, 304, 402), new Surface(352, 792, 434) });
    Check(MapGenerator.SlopeLinks(hill, hill.Surfaces[0], hill.Surfaces[1]) && MapGenerator.SlopeLinks(hill, hill.Surfaces[1], hill.Surfaces[0])
        && MapGenerator.SlopeLinks(hill, hill.Surfaces[2], hill.Surfaces[1]) && !MapGenerator.SlopeLinks(hill, hill.Surfaces[0], hill.Surfaces[2]),
        "Ramps link the flat ground at their two ends");
    Check(MapGenerator.NextHop(hill, 0, 2) == 1 && MapGenerator.NextHop(MapGenerator.NavLayout(hill, 14, 18), 2, 0) == 1, "Paths go over a hill");
    Check(MapGenerator.SlopeSurface(hill, 200, 429) == 0 && MapGenerator.SlopeSurface(hill, 235, 405.5f) == 1 && MapGenerator.SlopeSurface(hill, 100, 434) == -1,
        "Standing on a ramp plans from its nearer end");
    Check(hill.Blocked(new RectI(210, 424, 4, 4)) && !hill.Blocked(new RectI(200, 400, 4, 4)), "Blocked() sees slopes exactly");

    // Bullets: hit points and bounce normals off a 33.7 degree ramp (rise 32 over run 48).
    var ramp = new Slope(100, 100, 148, 68, 100);
    var ramps = new List<Slope> { ramp };
    var none = new List<RectI>();
    var down = Sweep.Cast(new Vector2(124, 0), new Vector2(0, 200), 2, none, ramps);
    float topAt124 = ramp.YAt(124); // 84
    var nExpect = Vector2.Normalize(new Vector2(-32, -48));
    Check(down is { } dh && Vector2.Distance(dh.Normal, nExpect) < 1e-3f && Vector2.Distance(ramp.Normal, nExpect) < 1e-3f
        && Math.Abs(dh.T * 200 - (topAt124 - 2 / nExpect.Y * -1)) < 0.05f && dh.Index == -2,
        "Sweep lands on a ramp at the right point with its normal", down?.ToString() ?? "miss");
    var refl = Sweep.Reflect(new Vector2(0, 300), nExpect);
    Check(refl.X < 0 && refl.Y < 0 && Math.Abs(refl.Length() - 300) < 0.01f, "Bounce off an up-right ramp goes up and left", refl.ToString());
    var side = Sweep.Cast(new Vector2(200, 90), new Vector2(-100, 0), 1, none, ramps);
    Check(side is { } sh && sh.Normal == new Vector2(1, 0) && Math.Abs(200 - sh.T * 100 - 149) < 0.01f, "Sweep hits a ramp's tall side flat", side?.ToString() ?? "miss");
    var crest = Sweep.Cast(new Vector2(160, 40), new Vector2(-24, 56), 3, none, ramps);
    Check(crest is { } ch && Math.Abs(Vector2.Distance(new Vector2(160, 40) + new Vector2(-24, 56) * ch.T, new Vector2(148, 68)) - 3) < 0.01f,
        "Sweep rounds a ramp's corner", crest?.ToString() ?? "miss");
    Check(Sweep.Cast(new Vector2(110, 70), new Vector2(0, -60), 2, none, ramps) == null
        && Sweep.Cast(new Vector2(110, 99), new Vector2(0, 50), 2, none, ramps) == null, "Sweep misses above a ramp and ignores one it starts in");
    var both = Sweep.Cast(new Vector2(124, 0), new Vector2(0, 200), 2, new List<RectI> { new(120, 90, 10, 10) }, ramps);
    Check(both is { Index: -2 }, "Sweep picks the ramp when it's hit before a rect");
}

Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
