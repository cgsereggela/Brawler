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

    // A jump pad launch keeps the ground jump: presses wait while the launch outpaces a jump, then it's a full ledge jump.
    j = new JumpCharges(); j.Land(1);
    Check(j.Use(false, false, true, true, risingFaster: true) == JumpKind.None && j.Left == 2
          && j.Use(false, false, false, true) == JumpKind.Ledge && j.Use(false, false, false, true) == JumpKind.Air,
        "Pad launch keeps the ground jump until landing");
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
    Check(MapLayout.Width == 1000 && MapLayout.Height == 562, "Arena is 1000x562");

    // Cave, tunnel and temple layouts: each builds without falling back, and its signature part is really there
    // and in play (reachable from the spawns), not just scenery.
    {
        var made = new Dictionary<MapStyle, (int maps, int fallbacks, int bad)>();
        var why = new List<string>();
        for (int s = 0; s < 2100; s++)
        {
            int seed = s * 104729 + 11, players = 2 + s % 3;
            var want = MapGenerator.StyleFor(seed);
            if (want is not (MapStyle.Cave or MapStyle.Tunnel or MapStyle.Temple)) continue;
            var m = MapGenerator.Generate(seed, players);
            var st = made.GetValueOrDefault(want);
            st.maps++;
            if (m.UsedFallback) { st.fallbacks++; made[want] = st; continue; }
            int spawn = MapGenerator.SurfaceIndexOf(m, m.Spawns[0]);
            var reach = MapGenerator.ReachableFrom(m, spawn);
            bool Reached(Func<Surface, bool> f) => m.Surfaces.Where((x, i) => reach[i] && f(x)).Any();
            string fail = null;
            var hanging = m.Solids.Where(r => r.Y < MapGenerator.Wall && r.X > 0 && r.Right < MapLayout.Width).ToList();
            var standing = m.Solids.Where(r => r.Bottom == MapGenerator.FloorY && !m.Terrain.Contains(r)).ToList();
            switch (want)
            {
                case MapStyle.Cave:
                    // A ceiling all the way across, stalactites below it, stalagmites one hop tall.
                    if (hanging.Sum(r => r.W) != MapLayout.Width - 2 * MapGenerator.Wall) fail = "ceiling has holes";
                    else if (!m.Solids.Any(r => r.Y > 40 && r.Bottom < 260 && r.W <= 24)) fail = "no stalactites";
                    else if (standing.Any(r => r.H > MapGenerator.MaxRise)) fail = "stalagmite too tall to hop onto";
                    break;
                case MapStyle.Tunnel:
                {
                    // Long slabs (two-tile-thick solids) split the arena; the top of the highest one is reachable.
                    var slabs = m.Solids.Where(r => r.H == 16 && r.Y > MapGenerator.Wall && r.Bottom < MapGenerator.FloorY).GroupBy(r => r.Y)
                        .Where(g => g.Sum(r => r.W) > 300).Select(g => g.Key).ToList();
                    if (slabs.Count is < 1 or > 2) fail = $"{slabs.Count} slabs";
                    else if (!Reached(x => x.Y == slabs.Min())) fail = "top corridor unreachable";
                    else if (!Reached(x => x.Y == MapGenerator.FloorY)) fail = "bottom corridor unreachable";
                    else if (hanging.Count == 0) fail = "no lowered ceiling";
                    else
                    {
                        // Every corridor (floor or slab top up to the next slab's or the ceiling's bottom) is tall enough to jump in.
                        var floors = slabs.Append(MapGenerator.FloorY).OrderBy(y => y).ToList();
                        var roofs = slabs.Select(y => y + 16).Prepend(hanging.Max(r => r.Bottom)).OrderBy(y => y).ToList();
                        if (floors.Zip(roofs).Any(p => p.First - p.Second < 134)) fail = "corridor too low";
                    }
                    break;
                }
                case MapStyle.Temple:
                {
                    // Steps rising to a flat top, which is reachable.
                    int topY = standing.Count == 0 ? MapGenerator.FloorY : standing.Min(r => r.Y);
                    if (standing.Count < 5) fail = $"only {standing.Count} steps";
                    else if (!Reached(x => x.Y == topY)) fail = "temple top unreachable";
                    break;
                }
            }
            if (fail != null) { st.bad++; if (why.Count < 6) why.Add($"seed {seed} {want}: {fail}"); }
            made[want] = st;
        }
        string counts = string.Join(", ", made.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value.maps} ({p.Value.fallbacks} fallbacks)"));
        Check(made.Count == 3 && made.Values.All(v => v.maps > 150 && v.fallbacks <= v.maps / 200), "Cave, tunnel and temple maps build", counts);
        Check(made.Values.All(v => v.bad == 0), "Cave, tunnel and temple maps have their parts, in reach", string.Join("; ", why));
    }

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
    var oldNose = Appearance.Decode("2,4,3,1,1,0,10");
    Check(oldNose.Nose == 0 && oldNose.Eyes == 2 && oldNose.Mouth == 3 && !Appearance.Editable.Contains(LookPart.Nose),
        "Noses are retired: old profiles load without one and the editor leaves the part out");
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
            foreach (var pd in m.Pads) if (sl.Intersects(new RectI(pd.X1, pd.Y - MapGenerator.PadAir, pd.X2 - pd.X1, MapGenerator.PadAir))) Bad(seed, "pad on or under a slope");
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

// ---------- Arena themes fit their features ----------
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

// ---------- Card pool (host-disabled cards) ----------
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

// ---------- Custom cards (JSON files, host sync) ----------
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
    hostSet.Add(new CardDef { Id = 1030, Name = "Odd floats", Description = "ünïcödé ✓", Category = CardCategory.Body, EffectId = null,
        Mods = new[] { new StatMod(Stat.Damage, ModOp.Mul, 1.1f), new StatMod(Stat.BulletGravity, ModOp.Mul, -0.8f), new StatMod(Stat.Spread, ModOp.Add, 0.12345679f) } });
    log.Clear();
    var back = CustomCards.Deserialize(CustomCards.Serialize(hostSet), log);
    bool Same(CardDef a, CardDef b) => a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Category == b.Category
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
    for (int i = 0; i < 400; i++) foreach (int id in CardRegistry.RollOptions(rr, 5)) rolled.Add(id);
    Check(hostSet.All(c => rolled.Contains(c.Id)), "Custom card registry: custom cards come up in rolls");
    var stats = StatBlock.Compute(new[] { CardRegistry.Get(1000) });
    Check(stats.Int(Stat.AirJumps) == 2 && Math.Abs(stats[Stat.MaxHp] - 90) < 0.01f, "Custom card registry: a custom card's mods apply", $"{stats[Stat.AirJumps]}");
    CardRegistry.SetCustom(new[] { hostSet[0] });
    Check(CardRegistry.All.Count == builtIns.Count + 1 && CardRegistry.Get(1030) == null, "Custom card registry: SetCustom replaces the whole custom set");
    CardRegistry.ResetToBuiltIns();
    Check(CardRegistry.All.SequenceEqual(builtIns) && CardRegistry.Get(1000) == null && CardRegistry.Custom.Count == 0
          && builtIns.All(c => CardRegistry.Get(c.Id) == c), "Custom card registry: reset restores exactly the built-ins");
}

// ---------- Vertical clearance between platforms ----------
{
    int c = MapGenerator.MinClearance;
    float giantH = Body.H * StatBlock.Compute(new[] { CardRegistry.Get(43) })[Stat.Size];
    Check(c > giantH + 4 && c - Body.H >= Body.H, "MinClearance fits a Giant, and a normal body gets a body height of hop",
        $"clearance {c}, body {Body.H}, Giant {giantH:0.0}");
    Check(MapGenerator.FloorY - MapGenerator.TierY(1) <= MapGenerator.MaxRise && MapGenerator.TierSpacing + MapGenerator.Tile <= MapGenerator.MaxRise,
        "Lowest tier and each tier above it (raised or not) stay within MaxRise");

    // Everything solid a body can stand on or bonk into: solids, mover lanes (their whole travel) and glass.
    int seeds = 2000, fallbacks = 0, pairs = 0, bad = 0, minGap = int.MaxValue;
    var why = new List<string>();
    foreach (int players in new[] { 2, 3, 4 })
        for (int s = 0; s < seeds; s++)
        {
            int seed = s * 6151 + players * 17;
            var m = MapGenerator.Generate(seed, players);
            if (m.UsedFallback) fallbacks++;
            var blocks = new List<RectI>(m.Solids);
            blocks.AddRange(m.Movers.Select(mv => mv.Lane));
            blocks.AddRange(m.Props.Where(p => p.Kind == PropKind.Glass).Select(p => p.Rect));
            for (int i = 0; i < blocks.Count; i++)
                for (int j = i + 1; j < blocks.Count; j++)
                {
                    RectI a = blocks[i], b = blocks[j];
                    if (a.Right <= b.X || b.Right <= a.X) continue; // no horizontal overlap
                    int gap = a.Bottom <= b.Y ? b.Y - a.Bottom : b.Bottom <= a.Y ? a.Y - b.Bottom : -1;
                    if (gap <= 0) continue; // touching (a tower on the floor, glass tiles in a row) or overlapping
                    // Only open air counts: rock filling the gap (a cave's ceiling around a stalactite's root) joins them.
                    int ox1 = Math.Max(a.X, b.X), ox2 = Math.Min(a.Right, b.Right);
                    var air = new RectI(ox1, a.Bottom <= b.Y ? a.Bottom : b.Bottom, ox2 - ox1, gap);
                    if (blocks.Any(o => o != a && o != b && o.Intersects(air))) continue;
                    pairs++;
                    minGap = Math.Min(minGap, gap);
                    if (gap < c) { bad++; if (why.Count < 5) why.Add($"seed {seed} {m.Style}: {a} / {b} gap {gap}"); }
                }
        }
    Check(bad == 0, "No two horizontally overlapping solids, movers or glass are closer than MinClearance",
        $"{pairs} stacked pairs, smallest gap {minGap}, {bad} bad. {string.Join("; ", why)}");
    Check(fallbacks <= seeds * 3 / 200, "Fallback rate stays under 0.5% with the clearance", $"{fallbacks}/{seeds * 3}");
}

// ---------- Leg animation footing (cosmetic ground / ramp / wall probes) ----------
{
    var floor = new List<RectI> { new(0, 100, 200, 20) };
    var none = new List<Slope>();
    // A 14x18 body resting on the floor: grounded, flat.
    var f = FootingProbe.Probe(50, 100, 7, 4, 7, floor, none);
    Check(f.Grounded && Math.Abs(f.Drop) < 0.01f && Math.Abs(f.Slope) < 0.01f, "Footing: standing on a flat floor", $"{f}");
    // Two pixels up from it (a frame after jumping) still counts as touching, ten pixels up does not.
    Check(FootingProbe.Probe(50, 98.5f, 7, 4, 7, floor, none).Grounded, "Footing: resting contact within Touch counts");
    Check(!FootingProbe.Probe(50, 90, 7, 4, 7, floor, none).Grounded, "Footing: airborne above the floor");
    // At a ledge with one foot over the drop, the missing foot borrows the other's height (stays flat).
    var edge = FootingProbe.Probe(203, 100, 7, 4, 7, floor, none);
    Check(edge.Grounded && Math.Abs(edge.Slope) < 0.01f && Math.Abs(edge.Drop) < 0.01f, "Footing: ledge edge stays flat", $"{edge}");

    // A ramp rising to the right (y 134 at x 100 up to 102 at x 148): the body rests on its uphill corner, the
    // ground lies below the bottom edge at the center, and the slope is negative (ascending to the right).
    var ramp = new List<Slope> { new(100, 134, 148, 102, 134) };
    float cx = 124, slopeK = -32f / 48f;
    float bottom = ramp[0].YAt(cx + 7);
    var r = FootingProbe.Probe(cx, bottom, 7, 4, 12, new List<RectI>(), ramp);
    Check(r.Grounded, "Footing: grounded on a ramp by the uphill corner");
    Check(Math.Abs(r.Slope - slopeK) < 0.02f, "Footing: ramp slope measured between the feet", $"{r.Slope} vs {slopeK}");
    Check(Math.Abs(r.Drop - 7 * -slopeK) < 0.05f, "Footing: ground drop under the center of a ramp", $"{r.Drop}");

    // Walls: right beside the box on either side, but not a floor under it or a wall a few pixels away.
    var walls = new List<RectI> { new(57, 0, 10, 200), new(20, 0, 10, 200) };
    Check(FootingProbe.WallSide(50, 100, 7, 9, walls.Take(1).ToList()) == 1, "Footing: wall on the right");
    Check(FootingProbe.WallSide(37, 100, 7, 9, walls.Skip(1).ToList()) == -1, "Footing: wall on the left");
    Check(FootingProbe.WallSide(50, 91, 7, 9, floor) == 0, "Footing: the floor is not a wall");
    Check(FootingProbe.WallSide(46, 100, 7, 9, walls.Take(1).ToList()) == 0, "Footing: no wall 4 px away");
}

// ---------- Gun rig: the arm holds the gun clear of the face; muzzle is shared by art and bullets ----------
{
    // Add any new gun look here: the hand path in GunRig must keep every look clear of the face.
    var looks = new[]
    {
        ("default", GunLook.Default),
    };
    // Every aim the facing rules allow in sprite space: up to MaxBackAimX behind, from straight up round to straight down.
    float back = MathF.Asin(GunRig.MaxBackAimX);
    foreach (var (name, look) in looks)
    {
        var boxes = GunRig.Footprint(look);
        int covered = 0;
        var bad = new SortedSet<int>();
        float minMargin = float.MaxValue;
        string worst = "";
        for (float deg = -90 - back * 180 / MathF.PI; deg <= 90 + back * 180 / MathF.PI + 1e-3f; deg += 0.5f)
            foreach (float kick in new[] { 0f, 0.5f, 1f })
            {
                float rad = deg * MathF.PI / 180;
                var (hand, angle) = GunRig.Pose(new Vector2(MathF.Cos(rad), MathF.Sin(rad)), kick);
                foreach (var (x, y, w, h) in boxes)
                    for (float u = x; u <= x + w + 1e-3f; u += Math.Min(0.25f, w))
                        for (float v = y; v <= y + h + 1e-3f; v += Math.Min(0.25f, h))
                        {
                            var p = GunRig.ToSprite(new Vector2(u, v), hand, angle);
                            foreach (var (fx, fy, fw, fh) in GunRig.Face)
                            {
                                // Signed distance outside the face box (negative = inside).
                                float dx = Math.Max(fx - p.X, p.X - (fx + fw)), dy = Math.Max(fy - p.Y, p.Y - (fy + fh));
                                float margin = dx > 0 && dy > 0 ? MathF.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy);
                                if (margin < minMargin) { minMargin = margin; worst = $"{deg:0.#} deg kick {kick} gun ({u:0.##},{v:0.##}) at {p} in face box {fx},{fy}"; }
                                if (margin < 0) { covered++; bad.Add((int)MathF.Floor(deg / 5) * 5); }
                            }
                        }
            }
        Check(covered == 0, $"Gun ({name}) never covers the eyes, nose or mouth at any allowed aim or recoil",
            $"closest {minMargin:0.00} px at {worst}{(covered > 0 ? $"; {covered} points inside, aims (5-degree bands): {string.Join(",", bad)}" : "")}");
    }

    // The two-bone arm reaches the grip at every allowed aim and recoil without stretching, bends more as the hand
    // comes in, and the elbow moves smoothly (no flips) as the aim sweeps round.
    {
        float worstUpper = 0, worstFore = 0, maxJump = 0, minReachSlack = float.MaxValue;
        Vector2? prev = null;
        for (float deg = -90 - back * 180 / MathF.PI; deg <= 90 + back * 180 / MathF.PI + 1e-3f; deg += 0.5f)
        {
            float rad = deg * MathF.PI / 180;
            var aim = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
            foreach (float kick in new[] { 0f, 1f })
            {
                var (grip, elbow) = GunRig.Arm(aim, kick);
                worstUpper = MathF.Max(worstUpper, MathF.Abs(Vector2.Distance(GunRig.Shoulder, elbow) - GunRig.UpperArm));
                worstFore = MathF.Max(worstFore, MathF.Abs(Vector2.Distance(elbow, grip) - GunRig.Forearm));
                minReachSlack = MathF.Min(minReachSlack, GunRig.UpperArm + GunRig.Forearm - Vector2.Distance(GunRig.Shoulder, grip));
            }
            var e0 = GunRig.Arm(aim).Elbow;
            if (prev is { } pe) maxJump = MathF.Max(maxJump, Vector2.Distance(pe, e0));
            prev = e0;
        }
        Check(worstUpper < 1e-3f && worstFore < 1e-3f && minReachSlack > 0, "Arm reaches the grip at every aim without stretching",
            $"upper err {worstUpper:0.0000}, fore err {worstFore:0.0000}, reach slack {minReachSlack:0.00}");
        Check(maxJump < 0.6f, "Elbow moves smoothly as the aim sweeps round", $"largest step {maxJump:0.00} per 0.5 deg");
        var (lg, le) = GunRig.Arm(new Vector2(1, 0));
        var (kg, ke) = GunRig.Arm(new Vector2(1, 0), 1);
        float Sag(Vector2 gr, Vector2 el) { var d = Vector2.Normalize(gr - GunRig.Shoulder); var r = el - GunRig.Shoulder; return MathF.Abs(r.X * d.Y - r.Y * d.X); }
        Check(le.Y > GunRig.Shoulder.Y + (lg.Y - GunRig.Shoulder.Y) * ((le.X - GunRig.Shoulder.X) / (lg.X - GunRig.Shoulder.X)) && Sag(kg, ke) > Sag(lg, le),
            "Level arm's elbow hangs below the line and folds further under recoil", $"sag {Sag(lg, le):0.00} -> {Sag(kg, ke):0.00}");
    }

    // The muzzle mirrors with facing, scales with size and sits past the body at arm's length.
    var g = GunLook.Default;
    var right = GunRig.Muzzle(new Vector2(1, 0), 1, 1, g);
    var left = GunRig.Muzzle(new Vector2(-1, 0), -1, 1, g);
    var big = GunRig.Muzzle(new Vector2(1, 0), 1, 2, g);
    Check(MathF.Abs(right.X + left.X) < 1e-4f && MathF.Abs(right.Y - left.Y) < 1e-4f, "Muzzle mirrors with facing", $"{right} / {left}");
    Check(Vector2.Distance(big, right * 2) < 1e-4f, "Muzzle scales with size", $"{big} vs {right * 2}");
    Check(right.X > Body.W / 2 + 8, "Level muzzle is held out at arm's length past the body", $"{right}");
    var up = GunRig.Muzzle(new Vector2(0, -1), 1, 1, g);
    Check(up.Y < -Body.H / 2 - 10 && MathF.Abs(up.X) < 3, "Upward muzzle is held straight up over the head", $"{up}");
    // Aiming straight up, the hand and the whole gun are raised over the top of the head (sprite y -9.5), recoil included.
    float lowest = float.MinValue;
    foreach (float kick in new[] { 0f, 0.5f, 1f })
    {
        var (hand, angle) = GunRig.Pose(new Vector2(0, -1), kick);
        foreach (var (x, y, w, h) in GunRig.Footprint(g))
            foreach (var corner in new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x, y + h), new Vector2(x + w, y + h) })
                lowest = Math.Max(lowest, GunRig.ToSprite(corner, hand, angle).Y);
    }
    Check(lowest < -9.5f, "Aiming straight up, the hand and gun sit above the head", $"lowest point y {lowest:0.0}");

    // The muzzle (bullet origin) follows the aim smoothly: no jumps as the aim sweeps, so bullets leave the barrel.
    float maxStep = 0;
    for (int facing = -1; facing <= 1; facing += 2)
        for (float deg = -100; deg < 100; deg += 1)
        {
            Vector2 A(float d) => new(facing * MathF.Cos(d * MathF.PI / 180), MathF.Sin(d * MathF.PI / 180));
            maxStep = Math.Max(maxStep, Vector2.Distance(GunRig.Muzzle(A(deg), facing, 1, g), GunRig.Muzzle(A(deg + 1), facing, 1, g)));
        }
    Check(maxStep < 0.6f, "Muzzle moves smoothly as the aim turns (1 degree steps)", $"largest step {maxStep:0.000} px");

    // Same inputs, same muzzle: the host's bullet origin is a pure function every peer can reproduce.
    Check(GunRig.Muzzle(new Vector2(0.6f, -0.8f), 1, 1.3f, g) == GunRig.Muzzle(new Vector2(0.6f, -0.8f), 1, 1.3f, g),
        "Muzzle is deterministic");
}

// ---------- Crosshair ammo pips ----------
{
    var bad = new List<string>();
    for (int mag = 1; mag <= 40; mag++)
    {
        if (AmmoPips.Continuous(mag) != (mag > AmmoPips.MaxPips)) bad.Add($"mag {mag}: wrong mode");
        if (AmmoPips.Continuous(mag)) continue;
        float w = AmmoPips.Width(mag), h = AmmoPips.Rows(mag) * (AmmoPips.PipH + AmmoPips.GapY) - AmmoPips.GapY;
        for (int i = 0; i < mag; i++)
        {
            var (x, y) = AmmoPips.Pip(i);
            // Inside the block, and never overlapping an earlier pip.
            if (x < AmmoPips.X0 - 1e-4f || y < AmmoPips.Y0 - 1e-4f || x + AmmoPips.PipW > AmmoPips.X0 + w + 1e-4f || y + AmmoPips.PipH > AmmoPips.Y0 + h + 1e-4f)
                bad.Add($"mag {mag} pip {i} outside the block");
            for (int j = 0; j < i; j++)
            {
                var (px, py) = AmmoPips.Pip(j);
                if (Math.Abs(px - x) < AmmoPips.PipW && Math.Abs(py - y) < AmmoPips.PipH) bad.Add($"mag {mag} pips {j},{i} overlap");
            }
        }
    }
    Check(bad.Count == 0, "Ammo pips: one per bullet up to MaxPips, in rows, no overlap", string.Join("; ", bad.Take(5)));
    // The block (plate included) sits in the lower-right quadrant, off the right and bottom ticks (strokes reach ~1.7 off their axis).
    Check(AmmoPips.X0 - 1.6f > 1.8f && AmmoPips.Y0 - 1.6f > 1.8f, "Ammo pips sit at the lower right, off the cross's ticks");
    var maxRows = AmmoPips.Rows(AmmoPips.MaxPips);
    Check(maxRows <= 3 && AmmoPips.Width(AmmoPips.MaxPips) < 26, "Ammo pip block stays small", $"{maxRows} rows, {AmmoPips.Width(AmmoPips.MaxPips):0.0} wide");
    Check(Math.Abs(AmmoPips.Gauge(23, 40) - 23f / 40) < 1e-5f && AmmoPips.Gauge(0, 40) == 0 && AmmoPips.Gauge(99, 40) == 1,
        "Ammo gauge (big magazines) is proportional, clamped to the mag");
    var maxMag = StatBlock.Compute(Enumerable.Repeat(CardRegistry.Get(31), 6).ToList())[Stat.Ammo];
    Check(maxMag <= 40 && AmmoPips.Continuous((int)maxMag), "Stacked Minigun magazines fall back to the gauge", $"{maxMag}");
}

// ---------- Thick floor band (room for the HUD card strip) ----------
{
    int floorY = MapGenerator.FloorY, depth = MapGenerator.FloorDepth;
    Check(floorY == MapLayout.Height - depth && depth >= 18 + 2 * 6, "Floor band is FloorDepth thick and fits the 18 px card strip with margins",
        $"depth {depth}, top {floorY}");
    Check(MapGenerator.TierY(9) >= MapGenerator.Wall + MapGenerator.MinClearance, "The highest tier a layout asks for still fits under the ceiling",
        $"tier 9 at {MapGenerator.TierY(9)}");

    // Nothing playable ends up inside the band: it is all floor (or pit) from FloorY down.
    int bad = 0;
    var why = new List<string>();
    void Bad(int seed, string what) { bad++; if (why.Count < 5) why.Add($"seed {seed}: {what}"); }
    for (int s = 0; s < 3000; s++)
    {
        int seed = s * 7919 + 3;
        var m = MapGenerator.Generate(seed, 2 + s % 3);
        foreach (var r in m.Solids)
            if (r.Y == floorY && r.Bottom != MapLayout.Height) Bad(seed, $"floor block {r} does not reach the bottom");
            else if (r.Y > floorY) Bad(seed, $"solid {r} inside the floor band");
        foreach (var su in m.Surfaces) if (su.Y > floorY) Bad(seed, $"surface {su}");
        foreach (var sp in m.Spawns) if (sp.Y > floorY) Bad(seed, $"spawn {sp}");
        foreach (var p in m.Pads) if (p.Y > floorY) Bad(seed, $"pad {p}");
        foreach (var p in m.Props) if (p.Y > floorY) Bad(seed, $"prop {p}");
        foreach (var h in m.Hazards) if (h.Y > floorY) Bad(seed, $"pool {h}");
        foreach (var sw in m.Saws) if (Math.Max(sw.Y1, sw.Y2) > floorY) Bad(seed, $"saw {sw}");
        foreach (var mv in m.Movers) if (mv.Lane.Bottom > floorY) Bad(seed, $"mover {mv}");
    }
    Check(bad == 0, "No surface, spawn, pad, prop, pool, saw or mover sits inside the floor band (3,000 maps)", string.Join("; ", why));
}

// ---------- Arena and movement scale (25% bigger arena, faster run, higher jump) ----------
{
    var bs = StatBlock.Base();
    float speed = bs[Stat.MoveSpeed], jv = bs[Stat.JumpVelocity], g = bs[Stat.Gravity];
    float apex = jv * jv / (2 * g);
    Check(Math.Abs(MapLayout.Width / 800f - 1.25f) < 0.01f && Math.Abs(MapLayout.Height / 450f - 1.25f) < 0.01f,
        "Arena is 25% bigger than 800x450 each way", $"{MapLayout.Width}x{MapLayout.Height}");
    Check(Math.Abs(speed / 115f - 1.25f) < 0.01f && Math.Abs(apex / (292f * 292f / (2 * 650f)) - 1.25f) < 0.01f && g == 650,
        "Run speed and jump height are 25% over the original 115 px/s and 292 px/s jump (same gravity)", $"speed {speed}, apex {apex:0.0}");
    // Step a held, full-speed jump at 60 Hz the way the avatar does (gravity, then move) and measure how far it
    // carries across before coming back down to MaxRise up, and to the launch height. A body 14 px wide can leave one
    // edge and touch the other with its own width to spare, so a gap needs 14 px less than its width in flight.
    float x = 0, y = 0, vy = -jv, dt = 1 / 60f, acrossAtRise = -1, acrossLevel = -1;
    bool rose = false;
    for (int i = 0; i < 600 && acrossLevel < 0; i++)
    {
        vy += g * dt;
        x += speed * dt;
        y += vy * dt;
        if (-y > MapGenerator.MaxRise) rose = true;
        if (rose && vy > 0 && acrossAtRise < 0 && -y <= MapGenerator.MaxRise) acrossAtRise = x;
        if (vy > 0 && y >= 0) acrossLevel = x;
    }
    Check(acrossAtRise >= MapGenerator.MaxGapRising - Body.W + 20, "A full jump clears MaxGapRising onto a ledge MaxRise up",
        $"{acrossAtRise:0} px across at {MapGenerator.MaxRise} px up, gap {MapGenerator.MaxGapRising}");
    Check(acrossLevel >= MapGenerator.MaxGapFalling - Body.W, "A full jump clears MaxGapFalling on the level",
        $"{acrossLevel:0} px across, gap {MapGenerator.MaxGapFalling}");
    Check(TerrainGen.MaxAngle > 34 && TerrainGen.MaxAngle <= 42 && TerrainGen.MaxHeight > 40,
        "Terrain is steeper and taller than before, and well under the 45 degree floor limit", $"{TerrainGen.MaxAngle} deg, {TerrainGen.MaxHeight} px");
    Check(MapGenerator.PadRise >= MapGenerator.MaxRise * 3 && MapGenerator.PadAir < MapGenerator.PadRise,
        "Jump pads still launch well past a jump", $"pad rise {MapGenerator.PadRise}, jump rise {MapGenerator.MaxRise}");
}

{
    // HUD hover card: every stat has a readable line, base stats show only the core set, cards light up what they change.
    var missing = Enum.GetValues<Stat>().Where(st => !StatSummary.Covers(st)).ToList();
    Check(missing.Count == 0, "Every stat has a HUD summary line", string.Join(",", missing));
    var baseLines = StatSummary.Lines(StatBlock.Base());
    Check(baseLines.Count == 9 && baseLines.All(l => !l.Changed), "Base stats show the 9 core lines, none changed", $"{baseLines.Count}");
    var bouncy = CardRegistry.All.First(c => c.Name == "Bouncy");
    var bl = StatSummary.Lines(StatBlock.Compute([bouncy, bouncy]));
    Check(bl.Any(l => l.Label == "Bounces" && l.Value == "4" && l.Changed), "Two Bouncy cards show 4 bounces", string.Join(", ", bl.Select(l => $"{l.Label}={l.Value}")));
    var stacks = StatSummary.Stacks([bouncy.Id, 2, bouncy.Id]);
    Check(stacks.Count == 2 && stacks[0].Card.Id == bouncy.Id && stacks[0].Count == 2, "Owned cards stack by id in pick order");
}

{
    // Block pack: each card does what it says, and the trade-away cards really turn blocking off.
    CardDef Named(string n) => CardRegistry.All.First(c => c.Name == n);
    var blockCards = CardRegistry.All.Where(c => c.Category == CardCategory.Block).ToList();
    Check(blockCards.Select(c => c.Id).OrderBy(i => i).SequenceEqual(Enumerable.Range(70, 12)), "Block cards use ids 70-81",
        string.Join(",", blockCards.Select(c => c.Id)));
    var refl = StatBlock.Compute([Named("Reflector")]);
    Check(refl[Stat.Reflect] == 1 && MathF.Abs(refl[Stat.BlockCooldown] - 4.8f) < 1e-4f, "Reflector reflects for +20% block cooldown", $"{refl[Stat.BlockCooldown]}");
    foreach (var n in new[] { "Iron Will", "All In" })
        Check(StatBlock.Compute([Named(n)])[Stat.BlockCooldown] >= 900, $"{n} turns blocking off");
    var guards = StatBlock.Compute(Enumerable.Repeat(Named("Quick Guard"), 5));
    Check(guards[Stat.BlockCooldown] >= 0.6f, "Stacked Quick Guards keep the block cooldown floor", $"{guards[Stat.BlockCooldown]}");
    var mom = StatBlock.Compute(Enumerable.Repeat(Named("Momentum"), 4));
    Check(mom[Stat.BlockRefund] <= 1f, "Momentum refunds at most the whole cooldown", $"{mom[Stat.BlockRefund]}");
    var wave = StatBlock.Compute(Enumerable.Repeat(Named("Shockwave"), 4));
    Check(wave[Stat.Shockwave] <= 160f, "Shockwave radius is capped", $"{wave[Stat.Shockwave]}");
    var lines = StatSummary.Lines(StatBlock.Compute([Named("Riposte"), Named("Absorb")]));
    Check(lines.Any(l => l.Label == "Counter" && l.Value == "60%") && lines.Any(l => l.Label == "Block heal" && l.Value == "75%"),
        "Block cards show in the HUD stats", string.Join(", ", lines.Select(l => $"{l.Label}={l.Value}")));
}

// ---------- README capture helpers ----------
{
    var names = FunNames.Pick(new Rng(3), 4);
    var words = names.SelectMany(n => n.Split(' ')).ToList();
    Check(names.Count == 4 && names.All(n => n.Length <= FunNames.MaxLength) && words.Distinct().Count() == words.Count,
        "Generated names fit the name box and don't repeat words", string.Join(", ", names));

    // Round trip: random pixels from 40 exact colours (so the palette is lossless), enough of them to fill the LZW table.
    int w = 200, h = 100;
    var rng = new Rng(11);
    var colours = Enumerable.Range(0, 40).Select(_ => new[] { (byte)(rng.Range(0, 31) * 8), (byte)(rng.Range(0, 31) * 8), (byte)(rng.Range(0, 31) * 8) }).ToList();
    var frames = new List<byte[]>();
    var cur = new byte[w * h * 3];
    for (int f = 0; f < 4; f++)
    {
        for (int i = 0; i < w * h; i++)
            if (f == 0 || (i % w > 50 && i % w < 120 && i / w > 20 && i / w < 60 && rng.Chance(0.5f)))
                Array.Copy(colours[rng.Range(0, colours.Count - 1)], 0, cur, i * 3, 3);
        frames.Add((byte[])cur.Clone());
    }
    if (frames[1].SequenceEqual(frames[0])) throw new Exception("test frames didn't change");
    frames.Add((byte[])cur.Clone()); // an unchanged frame
    var gif = Gif.Encode(frames, w, h, 5);
    var decoded = DecodeGif(gif);
    Check(decoded.Count == frames.Count && decoded.Zip(frames).All(p => p.First.SequenceEqual(p.Second)),
        "GIF encoder round-trips frames exactly", $"{gif.Length} bytes, {decoded.Count} frames");
}

// Composited RGB frames of a GIF with a global palette (enough of the format to check our encoder).
static List<byte[]> DecodeGif(byte[] g)
{
    int p = 6, w = g[p] | g[p + 1] << 8, h = g[p + 2] | g[p + 3] << 8;
    p = 13;
    var pal = g[p..(p + 768)];
    p += 768;
    var screen = new byte[w * h * 3];
    var outFrames = new List<byte[]>();
    int trans = -1;
    while (g[p] != 0x3B)
    {
        if (g[p] == 0x21)
        {
            if (g[p + 1] == 0xF9) trans = (g[p + 3] & 1) != 0 ? g[p + 6] : -1;
            p += 2;
            while (g[p] != 0) p += g[p] + 1;
            p++;
            continue;
        }
        int x0 = g[p + 1] | g[p + 2] << 8, y0 = g[p + 3] | g[p + 4] << 8, rw = g[p + 5] | g[p + 6] << 8, rh = g[p + 7] | g[p + 8] << 8;
        p += 10;
        int min = g[p++];
        var data = new List<byte>();
        while (g[p] != 0) { data.AddRange(g[(p + 1)..(p + 1 + g[p])]); p += g[p] + 1; }
        p++;
        // LZW decode
        int clear = 1 << min, end = clear + 1, size = min + 1, next = end + 1, bitPos = 0;
        var table = new List<byte[]>();
        void Reset() { table.Clear(); for (int i = 0; i < clear; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); size = min + 1; next = end + 1; }
        Reset();
        var px = new List<byte>();
        byte[] prev = null;
        while (true)
        {
            int code = 0;
            for (int b = 0; b < size; b++, bitPos++) code |= ((data[bitPos >> 3] >> (bitPos & 7)) & 1) << b;
            if (code == clear) { Reset(); prev = null; continue; }
            if (code == end) break;
            byte[] entry = code < table.Count ? table[code] : [.. prev, prev[0]];
            px.AddRange(entry);
            if (prev != null && next < 4096)
            {
                table.Add([.. prev, entry[0]]);
                next++;
                if (next >= 1 << size && size < 12) size++;
            }
            prev = entry;
        }
        for (int i = 0; i < rw * rh; i++)
        {
            if (px[i] == trans) continue;
            int s = ((y0 + i / rw) * w + x0 + i % rw) * 3;
            Array.Copy(pal, px[i] * 3, screen, s, 3);
        }
        outFrames.Add((byte[])screen.Clone());
    }
    return outFrames;
}

Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
