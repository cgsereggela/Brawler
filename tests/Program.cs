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
    foreach (int players in new[] { 2, 3, 4, 5, 6, 7, 8 })
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

    // Sky arenas: no floor at all, a row of islands along the bottom tier, and everyone can reach everyone.
    {
        int sky = 0, skyFallbacks = 0, skyBad = 0, kept = 0, others = 0;
        string skyWhy = "";
        for (int s = 0; s < 3000; s++)
        {
            int seed = s * 7919 + 3, players = 2 + s % 7;
            var style = MapGenerator.StyleFor(seed);
            if (style == MapStyle.Drift) continue; // checked below
            if (style != MapStyle.Sky)
            {
                // Seeds that aren't Sky keep the style they always had (their first roll).
                others++;
                if (style == (MapStyle)new Rng((ulong)(uint)seed).Range(0, 6)) kept++;
                continue;
            }
            sky++;
            var m = MapGenerator.Generate(seed, players);
            if (m.UsedFallback) { skyFallbacks++; continue; }
            string fail = null;
            if (m.Solids.Any(r => r.Bottom == MapLayout.Height && r.X > 0 && r.Right < MapLayout.Width)) fail = "has a floor";
            else if (m.Surfaces.Any(x => x.Y == MapGenerator.FloorY)) fail = "a surface at floor height";
            else if (m.Solids.Count(r => r.Y == MapGenerator.TierY(1)) < 2) fail = "fewer than two bottom islands";
            else if (!MapGenerator.SpawnsMutuallyReachable(m)) fail = "spawns can't reach each other";
            if (fail != null) { skyBad++; if (skyWhy == "") skyWhy = $"seed {seed}: {fail}"; }
        }
        Check(sky > 200 && skyFallbacks <= sky / 200, "Sky maps build", $"{sky} maps, {skyFallbacks} fallbacks");
        Check(skyBad == 0, "Sky maps have no floor, islands along the bottom, and spawns in reach of each other", skyWhy);
        Check(kept == others, "Seeds that aren't Sky or Drift keep their old style", $"{kept}/{others}");
        var empty = MapDesign.Empty(ThemeId.SunsetPeaks, 1, MapStyle.Sky);
        var built = empty.Build(4);
        Check(empty.Floor().Count == 0 && empty.Style == MapStyle.Sky && built.Surfaces.Count == 0
              && MapCode.Decode(MapCode.Encode(empty))?.Floor().Count == 0, "Design: an empty Sky arena has no floor, also after a map code");
    }

    // Drift arenas: nothing but drifting platforms over a bottomless drop, and everyone can reach everyone.
    {
        int maps = 0, fallbacks = 0, bad = 0, platforms = 0;
        string why = "";
        for (int s = 0; s < 4000; s++)
        {
            int seed = s * 7919 + 5, players = 2 + s % 7;
            if (MapGenerator.StyleFor(seed) != MapStyle.Drift) continue;
            maps++;
            var m = MapGenerator.Generate(seed, players);
            if (m.UsedFallback) { fallbacks++; continue; }
            platforms += m.Drifts.Count;
            string fail = null;
            if (m.Solids.Any(r => r.X > 0 && r.Right < MapLayout.Width && r.Y > 0)) fail = "has a solid besides the walls";
            else if (m.Surfaces.Count != m.Drifts.Count || m.Surfaces.Any(x => m.DriftIndex(x) < 0)) fail = "a surface that isn't a drifting platform";
            else if (m.Props.Count + m.Hazards.Count + m.Saws.Count + m.Movers.Count + m.Pads.Count > 0) fail = "extras on a Drift arena";
            else if (m.Drifts.Any(a => m.Drifts.Any(b => a != b && a.Range.Intersects(b.Range) && Math.Abs(a.Y - b.Y) < MapGenerator.DriftRowSpacing)))
                fail = "two platforms can be shoved into each other";
            else if (m.Drifts.Any(d => d.Range.X < MapGenerator.Wall || d.Range.Right > MapLayout.Width - MapGenerator.Wall || d.Range.Y < MapGenerator.Wall))
                fail = "a platform can be shoved into the walls";
            else if (!MapGenerator.SpawnsMutuallyReachable(m)) fail = "spawns can't reach each other";
            if (fail != null) { bad++; if (why == "") why = $"seed {seed}: {fail}"; }
        }
        Check(maps > 150 && fallbacks <= maps / 100, "Drift maps build", $"{maps} maps, {fallbacks} fallbacks, {platforms / Math.Max(1, maps - fallbacks)} platforms each");
        Check(bad == 0, "Drift maps are only drifting platforms that can't be shoved into each other or the walls, with spawns in reach", why);
        var gen = MapDesign.Generate(MapStyle.Drift, ThemeId.DeepReef, 1);
        var back = MapCode.Decode(MapCode.Encode(gen));
        Check(gen.Style == MapStyle.Drift && gen.Blocks.Count == 0 && gen.Drifts.Count > 5 && back != null && back.Style == MapStyle.Drift
              && back.Drifts.SequenceEqual(gen.Drifts) && back.Problems().Count == 0, "Design: a generated Drift arena survives a map code with no problems",
              string.Join(" | ", back?.Problems() ?? new List<string>()));
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

    int maps = 2000, crates = 0, barrels = 0, glass = 0, pools = 0, saws = 0, movers = 0, drifts = 0, hangers = 0, bad = 0;
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
        foreach (var dr in m.Drifts)
        {
            drifts++;
            if (m.Solids.Any(r => r.Intersects(dr.Range))) Bad(seed, "drift can be shoved into a solid");
            if (m.Props.Any(p => p.Rect.Intersects(dr.Range)) || m.Movers.Any(mv => mv.Lane.Intersects(dr.Range))) Bad(seed, "drift range crowded");
            // (On a Drift arena the platforms are the arena, so spawns stand on them.)
            if (m.Style != MapStyle.Drift && m.Spawns.Any(sp => dr.Range.Grow(0, 40).Contains(sp.X, sp.Y))) Bad(seed, "drift over a spawn");
            if (!m.Surfaces.Contains(dr.Top)) Bad(seed, "drift top not a surface");
        }
        foreach (var h in m.Hangers)
        {
            hangers++;
            var anchors = HangerMath.Anchors(m, h);
            for (int k = 0; k < anchors.Length; k++)
            {
                var chain = new RectI(h.ChainXs[k] - 2, anchors[k], 4, h.Y - anchors[k]);
                if (anchors[k] < MapGenerator.Wall || m.Solids.Any(r => r.Intersects(chain))) Bad(seed, "hanger chain through a solid");
                if (m.Props.Any(p => p.Rect.Intersects(chain)) || m.Drifts.Any(dr => dr.Range.Intersects(chain))) Bad(seed, "hanger chain through an extra");
                int cx = h.ChainXs[k], at = anchors[k];
                if (!m.Solids.Any(r => r.Bottom == at && r.X <= cx - HangerMath.AnchorMargin && r.Right >= cx + HangerMath.AnchorMargin))
                    Bad(seed, "hanger chain bolted too near the edge of what it hangs from");
            }
            if (m.Solids.Any(r => r.Intersects(h.Rect.Grow(16, MapGenerator.MinClearance)))) Bad(seed, "hanger boxed in");
            if (!HangerMath.LandsFlat(m, h)) Bad(seed, "hanger lands on a slope");
            if (HangerMath.LandY(m, h) is { } ly)
            {
                var landed = new RectI(h.X, ly, h.W, h.H);
                if (m.Spawns.Any(sp => sp.Y == ly + h.H && sp.X > h.X - 26 && sp.X < h.X + h.W + 26) || m.Pads.Any(p => p.Y == ly + h.H && p.X2 > h.X && p.X1 < h.X + h.W)
                    || m.Hazards.Any(z => z.Rect.Intersects(landed.Grow(4, 0))))
                    Bad(seed, "hanger lands on a spawn, pad or pool");
            }
        }
    }
    Check(bad == 0, "Arena features stay clear of solids, spawns, pads and each other", $"{bad} bad. {string.Join("; ", why)}");
    Check(crates > maps && barrels > maps / 4 && glass > maps / 2 && pools > maps / 3 && saws > maps / 4 && movers > maps / 5 && drifts > maps / 5 && hangers > maps / 4,
        "Every arena feature shows up often", $"per {maps} maps: {crates} crates, {barrels} barrels, {glass} glass, {pools} pools, {saws} saws, {movers} movers, {drifts} drifts, {hangers} hangers");

    var track = new SawTrack(100, 50, 200, 50, 9, 4, 0);
    Check(Vector2.Distance(track.At(0), new Vector2(100, 50)) < 0.01f && Vector2.Distance(track.At(2), new Vector2(200, 50)) < 0.01f
        && Vector2.Distance(track.At(4), new Vector2(100, 50)) < 0.01f, "Saws shuttle end to end and back each period");
    var mover = new Mover(40, 120, 300, 40, 8, 3, 0.25f);
    Check(Math.Abs(mover.XAt(0) - 80) < 0.01f && Enumerable.Range(0, 100).All(i => mover.XAt(i * 0.07f) is >= 40 and <= 120),
        "Movers stay inside their lane", $"{mover.XAt(0)}");
    {
        // A drifting platform shoved hard stays within its reach and eases back home.
        var drift = new Drift(300, 200, 56, 8);
        var (off, vel) = (Vector2.Zero, new Vector2(900, -400));
        bool inside = true;
        for (int i = 0; i < 60 * 6; i++)
        {
            off = DriftMotion.Step(drift, off, ref vel, Vector2.Zero, 1f / 60);
            inside &= Math.Abs(off.X) <= Drift.ReachX && Math.Abs(off.Y) <= Drift.ReachY;
        }
        Check(inside && off.Length() < 0.5f, "Drifting platforms stay within reach and settle back home", $"{off}");
        var wall = new Drift(MapGenerator.Wall + 4, 200, 56, 8);
        var (wo, wv) = (Vector2.Zero, new Vector2(-900, 0));
        float least = 0;
        for (int i = 0; i < 60; i++) { wo = DriftMotion.Step(wall, wo, ref wv, Vector2.Zero, 1f / 60); least = Math.Min(least, wo.X); }
        Check(wall.X + least >= MapGenerator.Wall, "Drifting platforms can't be shoved into the arena wall", $"{least}");
    }
    {
        // Hanging platforms: chains bolted to the block above (or the ceiling), landing on the floor or out through a pit.
        var hd = MapDesign.Empty(ThemeId.SunsetPeaks, 1);
        hd.Blocks.Add(new RectI(100, 120, 200, 16));
        hd.Pits.Add((600, 700));
        var hm = hd.Build(2);
        var under = new Hanger(120, 240, 40, 8);
        var wide = new Hanger(260, 240, 120, 8);
        var overPit = new Hanger(610, 240, 64, 8);
        Check(HangerMath.Anchors(hm, under).SequenceEqual(new[] { 136 }) && under.ChainXs.Length == 1, "A short hanger hangs on one chain from the block above it",
            string.Join(",", HangerMath.Anchors(hm, under)));
        Check(wide.ChainXs.SequenceEqual(new[] { 268, 372 }) && HangerMath.Anchors(hm, wide).SequenceEqual(new[] { 136, MapGenerator.Wall }),
            "A long hanger hangs on two chains, each from whatever is above it", string.Join(",", HangerMath.Anchors(hm, wide)));
        var onEdge = new Hanger(276, 240, 40, 8); // its chain 4 px in from the block's end: the bracket would stick out
        Check(HangerMath.AnchorsHidden(hm, under) && HangerMath.AnchorsHidden(hm, wide) && !HangerMath.AnchorsHidden(hm, onEdge),
            "A chain bolted near the end of a block shows, one under the middle or the ceiling doesn't");
        var edgeDesign = hd.Copy();
        edgeDesign.Hangers.Add(onEdge);
        Check(edgeDesign.Problems().Exists(p => p.Contains("off the end of a block")) && !hd.Problems().Exists(p => p.Contains("off the end")),
            "The map editor warns about a chain hanging off the end of a block", string.Join(" / ", edgeDesign.Problems()));
        Check(HangerMath.LandY(hm, under) == MapGenerator.FloorY - 8 && HangerMath.LandsFlat(hm, under), "A cut hanger lands flat on the floor");
        Check(HangerMath.LandY(hm, overPit) == null && HangerMath.LandsFlat(hm, overPit), "A hanger over a pit falls out of the arena");
        float tLand = MathF.Sqrt(2 * (MapGenerator.FloorY - 8 - 240) / Hanger.Gravity);
        Check(Hanger.Drop(0) == 0 && Hanger.Drop(-1) == 0 && Math.Abs(Hanger.Drop(tLand) - (MapGenerator.FloorY - 8 - 240)) < 0.01f && tLand < 0.8f,
            "A hanger drops faster and faster and reaches the floor in under a second", $"{tLand:0.00}s");
        // One chain of two cut: it swings on the other, the swing pulling that chain along, and settles hanging under it.
        var swing = new HangerSwing(wide, 268, MapGenerator.Wall);
        var wideCenter = new Vector2(320, 244);
        swing.At(0, out var c0, out float r0, out _, out _);
        float Energy(float t) { swing.At(t, out var c, out _, out var v, out float w); return 0.5f * (v.LengthSquared() + (120 * 120 + 64) / 12f * w * w) - Hanger.Gravity * c.Y; }
        float e0 = Energy(0), worst = float.MinValue, chainOut = 0;
        for (float t = 0; t < HangerSwing.Seconds; t += 0.05f)
        {
            worst = Math.Max(worst, Energy(t) - e0);
            chainOut = Math.Max(chainOut, Math.Abs(swing.ChainEndAt(t).X - 268));
        }
        swing.At(0.4f, out var cEarly, out float rEarly, out _, out _);
        swing.At(HangerSwing.Seconds, out var cEnd, out float rEnd, out var vEnd, out float wEnd);
        Check(Vector2.Distance(c0, wideCenter) < 0.01f && r0 == 0, "Swing: it starts where it hung, level", $"{c0} {r0}");
        Check(cEarly.X < 319 && cEarly.Y > 250 && rEarly > 0.2f, "Swing: the cut end drops and the platform swings in under its last chain",
            $"{cEarly} {rEarly:0.00}");
        Check(chainOut > 5, "Swing: the platform's swing is passed into the chain, which swings too", $"{chainOut:0.0}px");
        Check(worst < 50, "Swing: it never gains energy", $"+{worst:0.0}");
        Check(Math.Abs(cEnd.X - 268) < 3 && vEnd.Length() < 20 && Math.Abs(wEnd) < 0.05f, "Swing: it settles hanging still, straight under its chain",
            $"{cEnd} v{vEnd.Length():0.0} w{wEnd:0.00} r{rEnd:0.00}");
        var again = new HangerSwing(wide, 268, MapGenerator.Wall);
        again.At(2.345f, out var sw1, out float ar1, out _, out _);
        swing.At(2.345f, out var sw2, out float ar2, out _, out _);
        Check(sw1 == sw2 && ar1 == ar2, "Swing: the same cut swings the same on every peer");

        // The last chain goes: from rest it lands where it always did; mid-swing it carries the swing's speed and lands level.
        var still = new HangerFall(hm, wide, wideCenter, Vector2.Zero, 0, 0);
        Check(still.LandTop == HangerMath.LandY(hm, wide) && Math.Abs(still.LandX - 320) < 0.01f, "Fall: from rest it lands straight under where it hung",
            $"{still.LandTop} {still.LandX}");
        Check(new HangerFall(hm, overPit, new Vector2(642, 244), Vector2.Zero, 0, 0).LandTop == null, "Fall: over a pit it falls out of the arena");
        swing.At(0.6f, out var cFrom, out float rFrom, out var vFrom, out float wFrom);
        var flung = new HangerFall(hm, wide, cFrom, vFrom, rFrom, wFrom);
        bool landed = flung.At(flung.LandT + 0.01f, out var cLand, out float rLand);
        flung.At(0, out var cStart, out float rStart);
        Check(landed && flung.LandTop == MapGenerator.FloorY - 8 && rLand == 0 && Math.Abs(cLand.X - (cFrom.X + vFrom.X * flung.LandT)) < 1
            && Vector2.Distance(cStart, cFrom) < 0.01f && rStart == rFrom,
            "Fall: cut mid-swing, it carries on from there with the swing's speed and lands level", $"{cFrom} -> {cLand} t{flung.LandT:0.00}");

        hd.Hangers.Add(new Hanger(150, 116, 40, 8));
        Check(hd.Problems().Any(p => p.Contains("hanging platform")), "Design: a hanger inside a block is a problem", string.Join(" | ", hd.Problems()));
    }

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
    var koth = Make(3);
    koth.Settings.GameMode = "koth";
    Check(StatsReport.Build(new[] { koth }, new StatsFilter()).Matches == 0 && StatsReport.Build(new[] { Make(3) }, new StatsFilter()).Matches == 1,
        "Other game modes stay out of the card win rates");
    var oldLine = StatsJson.ToLine(Make(4)).Replace("\"gameMode\":\"elimination\",", "").Replace(",\"gameMode\":\"elimination\"", "");
    Check(!oldLine.Contains("gameMode") && StatsJson.ParseLines(new[] { oldLine }, out _)[0].Settings.GameMode == "elimination",
        "Logs from before game modes read as elimination");
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
    foreach (var song in Brawler.Logic.Synth.MusicGen.All())
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

// ---------- Streamer-mode invite codes ----------
{
    var addrs = new (string Ip, int Port)[] { ("192.168.1.20", 24565), ("0.0.0.0", 1), ("255.255.255.255", 65535), ("100.101.102.103", 7777), ("8.8.4.4", 24566) };
    var bad = addrs.Where(a => !Brawler.Logic.InviteCode.TryDecode(Brawler.Logic.InviteCode.Encode(a.Ip, a.Port), out var ip, out var p) || (ip, p) != a)
        .Select(a => a.Ip).ToList();
    Check(bad.Count == 0, "Invite codes round-trip IPv4 and port", string.Join(", ", bad));
    string code = Brawler.Logic.InviteCode.Encode("203.0.113.5", 24565);
    Check(code.Length == 14 && !code.Contains("203") && !code.Contains("113"), "Invite codes look like XXXX-XXXX-XXXX and hide the address", code);
    bool loose = Brawler.Logic.InviteCode.TryDecode(" " + code.ToLowerInvariant().Replace("-", " ").Replace('0', 'o') + " ", out var lip, out var lport) && lip == "203.0.113.5" && lport == 24565;
    Check(loose, "Invite codes ignore case, spacing and O/0 mix-ups", code);
    int typos = 0, caught = 0;
    for (int i = 0; i < code.Length; i++)
    {
        if (code[i] == '-') continue;
        char swap = code[i] == 'A' ? 'B' : 'A';
        typos++;
        if (!Brawler.Logic.InviteCode.TryDecode(code[..i] + swap + code[(i + 1)..], out _, out _)) caught++;
    }
    Check(caught >= typos - 1, "Invite code checksum catches single-character typos", $"{caught}/{typos}");
    bool rejects = !new[] { "", "1.2.3.4", "1.2.3.4:5000", "ABCD-EFGH", "localhost", "ABCD-EFGH-JKMN-PQRS" }.Any(t => Brawler.Logic.InviteCode.TryDecode(t, out _, out _))
        && Brawler.Logic.InviteCode.Encode("::1", 24565) == "" && Brawler.Logic.InviteCode.Encode("1.2.3.4", 0) == "";
    Check(rejects, "Addresses and junk are not mistaken for invite codes");
}

// ---------- Emotes ----------
{
    var g = new EmoteLimiter(burst: 3, refill: 1.5f);
    int burst = Enumerable.Range(0, 10).Count(_ => g.TryUse());
    Check(burst == 3, "Emote limiter allows a burst of 3, then refuses", $"{burst}");
    g.Tick(1.4f);
    Check(!g.TryUse(), "No new emote before a refill period passes");
    g.Tick(0.2f);
    Check(g.TryUse() && !g.TryUse(), "One emote back per refill period");
    g.Tick(100);
    Check(Math.Abs(g.Tokens - 3) < 1e-4, "Refill stops at the burst size", $"{g.Tokens}");
    // Spamming every frame for 30 s gets through at most the burst plus one per refill period.
    var spam = new EmoteLimiter();
    int sent = 0;
    for (int i = 0; i < 30 * 60; i++) { spam.Tick(1 / 60f); if (spam.TryUse()) sent++; }
    Check(sent <= 3 + 30 / 1.5f + 1 && sent >= 20, "Spamming is held to about one emote per 1.5 s", $"{sent}");
    Check(Emotes.All.All(e => e.Text.Length is > 0 and <= 12 && e.GhostText.Length is > 0 and <= 12), "Emote texts are short enough for a bubble");
    Check(Emotes.Text(1, ghost: true) == "BOO!" && Emotes.Text(1, ghost: false) == "HAHA!" && Emotes.Text(99, false) == "", "Ghosts say their own line; bad ids say nothing");
    Check(Enumerable.Range(0, Emotes.All.Length).All(i => BindTable.Actions.Contains(Emotes.Action(i))) && BindTable.Actions.Contains("ping"),
        "Every emote and the ping have a bindable action");
    Check(Emotes.Text(4, ghost: false) == "UNLUCKY" && Emotes.Action(4) == "emote_5", "UNLUCKY is the fifth emote, on emote_5");
}

// ---------- Sudden-death lava ----------
{
    Check(float.IsPositiveInfinity(RisingLava.Level(100, 0)), "Lava set to Never never rises", "");
    Check(float.IsPositiveInfinity(RisingLava.Level(44.9f, 45)), "Lava waits for its start time", "");
    float prev = RisingLava.Level(45.01f, 45);
    bool rising = prev > MapLayout.Height;
    for (float t = 45.5f; t < 90; t += 0.5f)
    {
        float y = RisingLava.Level(t, 45);
        if (y > prev) rising = false;
        prev = y;
    }
    Check(rising, "Lava starts below the arena and only ever climbs", "");
    float floorT = RisingLava.TimeToReach(MapGenerator.FloorY), topT = RisingLava.TimeToReach(MapGenerator.Wall);
    Check(floorT > 1.5f && floorT < 5, "Lava reaches the floor a few seconds after it starts", $"{floorT:0.0}s");
    Check(topT > 15 && topT < 35, "Lava swallows the whole arena within half a minute or so", $"{topT:0.0}s");
    Check(MathF.Abs(RisingLava.Level(45 + floorT, 45) - MapGenerator.FloorY) < 0.5f, "TimeToReach agrees with Level", "");
    Check(RisingLava.Snap(44) == 45 && RisingLava.Snap(0) == 0 && RisingLava.Snap(200) == 90, "Odd lava settings snap to a lobby choice", "");
    Check(Array.IndexOf(RisingLava.Choices, RisingLava.DefaultAfter) >= 0, "The default lava time is one of the lobby choices", "");
}

// ---------- Effects budget (dynamic particle culling) ----------
{
    const float Fast = 1 / 60f, Slow = 1 / 25f;
    static FxBudget Run(FxBudget b, float frame, int live, float seconds)
    {
        for (float t = 0; t < seconds; t += frame) b.Frame(frame, live);
        return b;
    }
    var b = Run(new FxBudget(2200, 300), Fast, 1500, 3);
    Check(b.Cap == 2200 && b.Quality == 1, "Quick frames keep the full particle budget", $"{b.Cap}");
    Run(b, Slow, 1500, 1.5f);
    Check(b.Cap < 700, "Effects slowing frames to 25 fps cut the budget within a second and a half", $"{b.Cap}");
    Run(b, Slow, 2000, 10);
    Check(b.Cap == 300 && b.Quality == 0, "The budget never drops below its floor", $"{b.Cap}");
    Run(b, Fast, 200, 0.4f);
    Check(b.Cap == 300, "It waits for a short calm before growing back", $"{b.Cap}");
    Run(b, Fast, 200, 8);
    Check(b.Cap == 2200, "Quick frames grow it back to the full budget", $"{b.Cap}");

    // A machine that runs at 30 fps with nothing on screen keeps its budget at 30 fps with effects...
    var slowPc = Run(new FxBudget(2200, 300), 1 / 30f, 20, 5);
    Run(slowPc, 1 / 30f, 1800, 5);
    Check(slowPc.Cap == 2200, "A machine that is slow everywhere keeps its budget when effects add nothing", $"{slowPc.Cap}, baseline {slowPc.Baseline * 1000:0.0} ms");
    // ...but still culls when effects drag it down further.
    Run(slowPc, 1 / 15f, 1800, 2);
    Check(slowPc.Cap < 1000, "...and still culls when effects slow it further", $"{slowPc.Cap}");

    var quiet = Run(new FxBudget(2200, 300), Fast, 0, 1);
    Run(quiet, Slow, 40, 3);
    Check(quiet.Cap == 2200, "Slow frames with hardly any particles don't shrink the budget", $"{quiet.Cap}");
    var hitch = Run(new FxBudget(2200, 300), Fast, 1500, 1);
    hitch.Frame(2f, 1500);
    Check(hitch.Cap > 1800, "One long hitch doesn't wipe out the budget", $"{hitch.Cap}");

    var full = new FxBudget(2200, 300);
    Check(full.Scale(20) == 20 && full.Scale(0) == 0, "At full quality every requested particle is emitted");
    Run(full, Slow, 2000, 10);
    Check(full.Scale(20) == 5 && full.Scale(1) == 1, "At the floor a quarter are emitted, and never none", $"{full.Scale(20)}");
}

// ---------- Card combos ----------
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
    Check(withTerrain > maps / 4 && withTerrain < maps * 3 / 4 && byStyle.Where(p => TerrainGen.ChanceFor(p.Key) > 0).All(p => p.Value.terrain > p.Value.maps / 6),
        "A good share of maps (every style with a floor) have terrain", $"{100f * withTerrain / maps:0}% of {maps}: {shares}; {slopes} slopes, {plateaus} raised stretches");

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

// ---------- Rare cards and cursed deals ----------
{
    var rares = CardRegistry.All.Where(c => c.IsRare).ToList();
    var curses = CardRegistry.All.Where(c => c.IsCurse).ToList();
    Check(rares.Count >= 10 && curses.Count >= 12, "At least 10 rare cards and 12 curses", $"{rares.Count} rare, {curses.Count} curses");
    Check(rares.All(c => !c.IsCurse), "No card is both rare and a curse");
    // A curse is a pure downside: compared with base stats it must make something worse and nothing better.
    var baseStats = StatBlock.Base();
    var higherIsWorse = new HashSet<Stat> { Stat.FireCooldown, Stat.ReloadTime, Stat.BlockCooldown, Stat.Size, Stat.KnockbackTaken };
    bool allWorse = curses.All(c =>
    {
        var s = StatBlock.Compute(new[] { c });
        var moved = Enum.GetValues<Stat>().Where(st => Math.Abs(s[st] - baseStats[st]) > 1e-4f).ToList();
        return moved.Count > 0 && moved.All(st => higherIsWorse.Contains(st) ? s[st] > baseStats[st] : s[st] < baseStats[st]);
    });
    Check(allWorse, "Every curse only makes stats worse");
    var jaw = StatBlock.Compute(new[] { CardRegistry.Get(99) });
    Check(Math.Abs(jaw[Stat.Armor] + 0.2f) < 1e-4f, "Glass Jaw's negative armor survives the clamp", $"{jaw[Stat.Armor]}");

    var rng = new Rng(11);
    bool plainOnly = true, neverCursed = true, atMostOne = true;
    int picks = 3000, withRare = 0;
    for (int i = 0; i < picks; i++)
    {
        var plain = CardRegistry.RollOptions(rng, 5);
        plainOnly &= plain.All(id => !CardRegistry.Get(id).IsRare && !CardRegistry.Get(id).IsCurse);
        var opts = CardRegistry.RollOptions(rng, 5, null, 0.1f);
        neverCursed &= opts.All(id => !CardRegistry.Get(id).IsCurse) && opts.Distinct().Count() == opts.Count && opts.Count == 5;
        int r = opts.Count(id => CardRegistry.Get(id).IsRare);
        atMostOne &= r <= 1;
        if (r > 0) withRare++;
    }
    Check(plainOnly, "Rares off: offers are only common cards");
    Check(neverCursed, "Curses are never offered on their own, and rare offers stay distinct and full");
    Check(atMostOne, "A pick offers at most one rare card while commons are left");
    double rate = (double)withRare / picks;
    Check(rate > 0.08 && rate < 0.12, "A 10% rare chance puts a rare card in about 10% of picks", $"{rate:P1}");
    var allRare = CardRegistry.RollOptions(new Rng(2), 5, null, 1f);
    Check(allRare.Count == 5 && allRare.Count(id => CardRegistry.Get(id).IsRare) == 1, "A 100% rare chance puts one rare in every pick");
    var commonsOff = new HashSet<int>(CardPool.Allowed(null));
    var onlyRares = CardRegistry.RollOptions(new Rng(2), 5, commonsOff, 0.1f);
    Check(onlyRares.Count == 5 && onlyRares.All(id => CardRegistry.Get(id).IsRare), "With every common card disabled, rares fill the offer");

    bool dealsOk = true;
    var seen = new HashSet<int>();
    var r2 = new Rng(4);
    var noArsenal = new HashSet<int> { 82, 92 };
    for (int i = 0; i < 2000; i++)
    {
        var opts = CardRegistry.RollOptions(r2, 5, noArsenal, 0.35f);
        var deal = CardRegistry.RollDeal(r2, noArsenal, opts);
        dealsOk &= deal is { } d && CardRegistry.Get(d.Rare).IsRare && CardRegistry.Get(d.Curse).IsCurse
                   && !opts.Contains(d.Rare) && !noArsenal.Contains(d.Rare) && !noArsenal.Contains(d.Curse);
        if (deal is { } dd) { seen.Add(dd.Rare); seen.Add(dd.Curse); }
    }
    Check(dealsOk, "A deal is a rare card not already offered plus a curse, both enabled");
    Check(seen.Count == rares.Count + curses.Count - 2, "Every enabled rare and curse comes up in deals", $"{seen.Count}");
    var noCurses = new HashSet<int>(curses.Select(c => c.Id));
    Check(CardRegistry.RollDeal(new Rng(1), noCurses, new List<int>()) == null, "No enabled curses: no deal");
    var noRares = new HashSet<int>(rares.Select(c => c.Id));
    Check(CardRegistry.RollDeal(new Rng(1), noRares, new List<int>()) == null, "No enabled rares: no deal");
    var dealRng = new Rng(9);
    int deals = 0;
    for (int i = 0; i < 3000; i++)
        if (CardRegistry.RollDeal(dealRng, null, new List<int>(), 0.1f) != null) deals++;
    Check(deals > 240 && deals < 360, "A 10% rare chance offers a cursed deal in about 10% of picks", $"{deals / 30.0:F1}%");
    Check(CardRegistry.RollDeal(new Rng(1), null, new List<int>(), 0) == null, "Rares off: no deals either");
    Check(CardPool.CommonCount(noRares) == CardPool.CommonCount(null) && CardPool.CommonCount(null) >= CardPool.MinEnabled,
        "Rares don't count toward the common minimum");

    // Stats report: a taken deal counts as a pick for the rare and the curse, an extra option for everything offered.
    var m = new MatchRecord { Complete = true, Winner = 0 };
    m.Players.Add(new PlayerRec { Slot = 0, Name = "A" });
    m.Picks.Add(new PickRec { Slot = 0, Offered = new List<int> { 1, 2, 3 }, Deal = new List<int> { 83, 94 }, Picked = 83, TookDeal = true });
    var rep = StatsReport.Build(new[] { m }, new StatsFilter());
    var c83 = rep.Cards.First(c => c.Card.Id == 83);
    var c94 = rep.Cards.First(c => c.Card.Id == 94);
    var c1 = rep.Cards.First(c => c.Card.Id == 1);
    Check(c83.Picked == 1 && c94.Picked == 1 && c83.Offered == 1 && Math.Abs(c1.ExpectedPicks - 0.25) < 1e-9,
        "Stats report: a taken deal counts its rare and curse", $"{c83.Picked}/{c94.Picked}/{c1.ExpectedPicks}");
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
    foreach (int players in new[] { 2, 3, 4, 5, 6, 7, 8 })
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
    var blockCards = CardRegistry.All.Where(c => c.Category == CardCategory.Block && !c.IsRare).ToList();
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

// ---------- End-of-match awards ----------
{
    var book = new AwardBook();
    book.Add(0, AwardStat.SelfKills, 3);
    book.Add(1, AwardStat.SelfKills, 1);
    book.Add(0, AwardStat.Kills, 5); // tied with slot 1: nobody is the Menace
    book.Add(1, AwardStat.Kills, 5);
    book.Max(2, AwardStat.Airtime, 2f);
    book.Max(2, AwardStat.Airtime, 1f); // keeps the longest
    var awards = Awards.Pick(book, new[] { 0, 1, 2 }, new Rng(1));
    Check(awards.Any(a => a.Id == 0 && a.Slot == 0 && a.Blurb == "3 self-inflicted exits"), "Awards: a clear leader gets the title", string.Join(", ", awards.Select(a => a.Title)));
    Check(!awards.Any(a => a.Id == 10), "Awards: a tie gives nobody the title");
    Check(!awards.Any(a => a.Id == 1) && Math.Abs(book.Get(2, AwardStat.Airtime) - 2f) < 0.001f, "Awards: below the minimum, no title (and Max keeps the record)");
    Check(Awards.Pick(book, new[] { 0 }, new Rng(1)).Count == 0, "Awards: none with fewer than two players");

    // Everyone qualifies for something: four awards, one each before anyone gets a second.
    var full = new AwardBook();
    for (int s = 0; s < 4; s++) full.Add(s, AwardStat.Shots, 10);
    full.Add(0, AwardStat.SelfKills, 4);
    full.Add(0, AwardStat.Falls, 4);
    full.Add(1, AwardStat.HitsBlocked, 9);
    full.Add(2, AwardStat.Healed, 200);
    full.Add(3, AwardStat.Kills, 7);
    full.Add(3, AwardStat.DamageTaken, 900);
    var four = Awards.Pick(full, new[] { 0, 1, 2, 3 }, new Rng(5));
    Check(four.Count == Awards.MaxShown && four.Select(a => a.Slot).Distinct().Count() == 4, "Awards: four players, one award each",
        string.Join(", ", four.Select(a => $"{a.Slot}:{a.Title}")));
    var again = Awards.Pick(full, new[] { 0, 1, 2, 3 }, new Rng(5));
    Check(four.SequenceEqual(again), "Awards: same seed, same awards");
    var two = Awards.Pick(full, new[] { 0, 3 }, new Rng(5));
    Check(two.Count == 4 && two.Count(a => a.Slot == 0) == 2, "Awards: two players can get two each", string.Join(", ", two.Select(a => $"{a.Slot}:{a.Title}")));
}

// ---------- Kill cam timing ----------
{
    Check(KillCamTiming.Speed(-KillCamTiming.Pre) == KillCamTiming.Fast, "Kill cam: the build-up plays at full speed");
    Check(KillCamTiming.Speed(0) == KillCamTiming.Slow, "Kill cam: slowest at the kill");
    Check(Math.Abs(KillCamTiming.Speed(KillCamTiming.Post) - KillCamTiming.Exit) < 1e-4, "Kill cam: picks up again by the end");
    bool smooth = true, positive = true;
    for (float t = -KillCamTiming.Pre; t < KillCamTiming.Post; t += 0.005f)
    {
        float a = KillCamTiming.Speed(t), b = KillCamTiming.Speed(t + 0.005f);
        positive &= a > 0;
        smooth &= Math.Abs(a - b) < 0.05f;
    }
    Check(positive && smooth, "Kill cam: speed is positive and has no jumps");
    float real = KillCamTiming.RealSeconds();
    Check(real > 3f && real < 4.5f, "Kill cam: takes 3-4.5 s to watch", $"{real:0.00}s");
}

// ---------- Team modes ----------
{
    Check(Teams.ModeName(0) == "Free-for-all" && Teams.ModeName(3) == "3v3", "Teams: mode names");
    Check(Teams.NextSize(0) == 2 && Teams.NextSize(2) == 3 && Teams.NextSize(4) == 0, "Teams: the mode button steps 2v2, 3v3, 4v4, then back to free-for-all");
    Check(Teams.Deal(5).SequenceEqual(new[] { 0, 1, 0, 1, 0 }), "Teams: dealing alternates");
    Check(Teams.Emptier(new int[0], 2) == 0 && Teams.Emptier(new[] { 0 }, 2) == 1 && Teams.Emptier(new[] { 0, 1 }, 2) == 0,
        "Teams: newcomers join the emptier team, ties to Red");
    Check(Teams.Emptier(new[] { 0, 1, 0, 1 }, 2) == -1, "Teams: no room once both are full");
    Check(Teams.Swap(new[] { 1 }, 0, 2) == 1 && Teams.Swap(new[] { 1, 1 }, 0, 2) == 0, "Teams: a swap needs room on the other side");
    Check(Teams.Problem(new[] { 0, 1 }, 0) == null && Teams.Problem(new[] { 0, 0 }, 0) == null, "Teams: free-for-all has no team rules");
    Check(Teams.Problem(new[] { 0, 0 }, 2) != null, "Teams: both teams need someone");
    Check(Teams.Problem(new[] { 0, 0, 0, 1 }, 2) != null, "Teams: a team can't go over the size");
    Check(Teams.Problem(new[] { 0, 0, 1 }, 2) == null, "Teams: uneven teams can still play");
    Check(!Teams.Settled(new[] { 0, 1, 1 }, out _), "Teams: both teams standing, the battle goes on");
    Check(Teams.Settled(new[] { 1, 1 }, out int w1) && w1 == 1, "Teams: one team left wins");
    Check(Teams.Settled(new int[0], out int w2) && w2 == -1, "Teams: nobody left is a draw");
}

// ---------- Game modes ----------
{
    Check(GameModes.Next(GameMode.Elimination) == GameMode.KingOfTheHill && GameModes.Next(GameMode.Headhunter) == GameMode.GunGame && GameModes.Next(GameMode.GunGame) == GameMode.Survival && GameModes.Next(GameMode.Survival) == GameMode.Elimination,
        "Modes: the lobby button steps through every mode and wraps");
    Check(GameModes.All.All(m => GameModes.Parse(GameModes.Id(m)) == m) && GameModes.Parse("nope") == null, "Modes: ids parse back");
    Check(GameModes.From(99) == GameMode.Elimination && GameModes.From(2) == GameMode.CaptureTheFlag, "Modes: unknown wire values fall back to elimination");
    Check(GameModes.Problem(GameMode.CaptureTheFlag, 0) != null && GameModes.Problem(GameMode.CaptureTheFlag, 2) == null
          && GameModes.Problem(GameMode.KingOfTheHill, 0) == null, "Modes: only Capture the Flag needs teams");
    Check(GameModes.LavaAfter(GameMode.KingOfTheHill, 45) == 90 && GameModes.LavaAfter(GameMode.Headhunter, 45) == 45
          && GameModes.LavaAfter(GameMode.CaptureTheFlag, 0) == 0, "Modes: sudden death comes twice as late in the respawn modes");
    Check(GameModes.RespawnsNow(GameMode.KingOfTheHill, 30, 45) && !GameModes.RespawnsNow(GameMode.KingOfTheHill, 95, 45)
          && GameModes.RespawnsNow(GameMode.KingOfTheHill, 500, 0) && !GameModes.RespawnsNow(GameMode.Elimination, 1, 45),
        "Modes: respawns stop at sudden death, and never happen in elimination");

    // Gun Game: a fixed ladder of real cards; a kill swaps yours for the next one.
    Check(GameModes.GunLadder.All(id => CardRegistry.All.Any(c => c.Id == id)) && GameModes.GunLadder.Distinct().Count() == GameModes.GunLadder.Length,
          "Gun Game: the ladder is distinct real cards");
    Check(GameModes.GunCard(-3) == GameModes.GunLadder[0] && GameModes.GunCard(99) == GameModes.GunLadder[^1], "Gun Game: levels clamp to the ladder");
    Check(GameModes.Problem(GameMode.GunGame, 2) != null && GameModes.Problem(GameMode.GunGame, 0) == null, "Gun Game: free-for-all only");
    Check(GameModes.LavaAfter(GameMode.GunGame, 45) == 0 && GameModes.Respawns(GameMode.GunGame), "Gun Game: respawns, and no sudden death");

    // Survival: waves grow from one bot per human, one more every second wave, never past the lobby's bots.
    Check(GameModes.WaveSize(1, 1, 7) == 1 && GameModes.WaveSize(3, 1, 7) == 2 && GameModes.WaveSize(1, 2, 6) == 2 && GameModes.WaveSize(10, 1, 7) == 5,
          "Survival: waves start at one bot per human and grow every second wave");
    Check(Enumerable.Range(1, GameModes.SurvivalWaves).All(w => GameModes.WaveSize(w, 3, 2) <= 2) && GameModes.WaveSize(1, 3, 2) == 2,
          "Survival: a wave never needs more bots than the lobby has");
    Check(Enumerable.Range(1, GameModes.SurvivalWaves).All(w => GameModes.WaveSize(w, 1, 2) + GameModes.WaveRespawns(w, 1, 2) == 1 + (w - 1) / 2)
          && GameModes.WaveRespawns(10, 1, 2) == 3 && GameModes.WaveRespawns(10, 1, 7) == 0 && GameModes.WaveRespawns(3, 3, 2) == 2 && GameModes.WaveRespawns(10, 1, 0) == 0,
          "Survival: once the lobby runs out of bots, the wave's extra bots come as respawns instead");
    Check(GameModes.WaveSkill(1) == BotSkill.Easy && GameModes.WaveSkill(5) == BotSkill.Normal && GameModes.WaveSkill(GameModes.SurvivalWaves) == BotSkill.Hard
          && GameModes.WaveCards(1) == 0 && GameModes.WaveCards(2) == 0 && GameModes.WaveCards(3) == 1 && GameModes.WaveCards(GameModes.SurvivalWaves) < GameModes.SurvivalWaves,
          "Survival: bots get tougher, and gain cards slower than the humans' one a wave");
    Check(GameModes.Problem(GameMode.Survival, 2) != null && GameModes.Problem(GameMode.Survival, 0) == null, "Survival: picks its own teams (free-for-all lobby)");

    // The hill: the side alone in it scores; contested or empty, nobody does.
    var hill = new Hill(100, 200, 300);
    var half = new Vector2(6, 10);
    Check(hill.Inside(new Vector2(150, 290), half) && !hill.Inside(new Vector2(150, 320), half) && !hill.Inside(new Vector2(250, 290), half)
          && !hill.Inside(new Vector2(150, 200), half), "Hill: standing on it counts; below, beside or high above doesn't");
    Check(hill.Step(1, new[] { 2, 2 }) == -1 && hill.Holder == 2 && Math.Abs(hill.Held[2] - 1) < 1e-4, "Hill: a side alone holds it");
    hill.Step(1, new[] { 2, 3 });
    Check(hill.Contested && hill.Holder == -1 && Math.Abs(hill.Held[2] - 1) < 1e-4 && hill.Held[3] == 0, "Hill: contested, nobody scores");
    hill.Step(1, new int[0]);
    Check(!hill.Contested && hill.Holder == -1 && Math.Abs(hill.Held[2] - 1) < 1e-4, "Hill: held time doesn't drain when it's empty");
    int hillWin = -1;
    for (int i = 0; i < 20 && hillWin < 0; i++) hillWin = hill.Step(0.5f, new[] { 2 });
    Check(hillWin == 2 && hill.Held[2] <= GameModes.HoldTime, "Hill: held long enough wins");

    bool hillsOk = true;
    for (int seed = 1; seed <= 60; seed++)
    {
        var map = MapGenerator.Generate(seed, 4);
        var h = GameModes.PickHill(map);
        if (h.X2 - h.X1 < Hill.MinWidth - 1 || h.X2 - h.X1 > Hill.MaxWidth || h.X1 < 0 || h.X2 > MapLayout.Width
            || !map.Surfaces.Any(s => s.Y == h.Y && s.X1 <= h.X1 && s.X2 >= h.X2)) hillsOk = false;
    }
    Check(hillsOk, "Hill: always picked on a real surface, sized within limits (60 maps)");

    // Flags.
    var flags = new Flags(new Vector2(50, 500), new Vector2(950, 500));
    var red = new Runner(0, 0, new Vector2(950, 500), 10);   // Red player at Blue's base
    var blue = new Runner(1, 1, new Vector2(500, 300), 10);
    var ev = flags.Step(0.1f, new[] { red, blue });
    Check(ev.Count == 1 && ev[0] == (FlagEvent.Taken, 1, 0) && flags.CarriedBy(0) == 1, "Flags: an enemy touching a flag takes it");
    ev = flags.Step(0.1f, new[] { red with { Pos = new Vector2(500, 500) }, blue with { Pos = new Vector2(950, 500) } });
    Check(ev.Count == 0 && flags.Team[1].Pos == new Vector2(500, 500), "Flags: a carried flag moves with its carrier, and its empty base gives nothing");
    Check(flags.Drop(0, new Vector2(400, 520)) == 1 && flags.Team[1].Dropped && flags.Drop(0, null) == -1, "Flags: the carrier going down drops it");
    ev = flags.Step(0.1f, new[] { red with { Pos = new Vector2(400, 520) } });
    Check(ev.Count == 1 && ev[0] == (FlagEvent.Taken, 1, 0), "Flags: an enemy can take a dropped flag back up");
    flags.Drop(0, new Vector2(400, 520));
    ev = flags.Step(0.1f, new[] { blue with { Pos = new Vector2(405, 520) } });
    Check(ev.Count == 1 && ev[0] == (FlagEvent.Returned, 1, 1) && flags.Team[1].AtHome, "Flags: a defender touching a dropped flag returns it");
    flags.Step(0.1f, new[] { red with { Pos = new Vector2(950, 500) } });
    flags.Drop(0, new Vector2(700, 520));
    for (int i = 0; i < 130; i++) ev = flags.Step(0.1f, new Runner[0]);
    Check(flags.Team[1].AtHome, "Flags: a dropped flag goes home by itself in time");
    flags.Drop(0, null);
    flags.Step(0.1f, new[] { red with { Pos = new Vector2(950, 500) } });
    flags.Step(0.1f, new[] { blue with { Pos = new Vector2(50, 500) } }); // Blue takes Red's flag too
    ev = flags.Step(0.1f, new[] { red with { Pos = new Vector2(52, 500) } });
    Check(ev.Count == 0, "Flags: no capture while your own flag is away from home");
    flags.Drop(1, null);
    ev = flags.Step(0.1f, new[] { red with { Pos = new Vector2(52, 500) } });
    Check(ev.Any(e => e == (FlagEvent.Captured, 1, 0)) && flags.Team[1].AtHome, "Flags: the enemy flag at your base, yours home: captured");
    var lost = new Flags(new Vector2(50, 500), new Vector2(950, 500));
    lost.Step(0.1f, new[] { red });
    Check(lost.Drop(0, null) == 1 && lost.Team[1].AtHome, "Flags: lost in a pit, it goes straight home");

    // The head: the leader, ties broken the same way on every peer.
    var hh = new List<(int, int, int, int)> { (0, -1, 1, 0), (1, -1, 2, 0), (2, -1, 2, 1), (3, -1, 0, 1) };
    Check(GameModes.PickHead(hh, 5) == 2, "Head: the leader (round wins, then battle wins) is marked");
    var tied = new List<(int, int, int, int)> { (0, -1, 0, 0), (1, -1, 0, 0), (2, -1, 0, 0) };
    var heads = Enumerable.Range(0, 40).Select(s => GameModes.PickHead(tied, s)).ToList();
    Check(heads.All(h => h is >= 0 and <= 2) && heads.Distinct().Count() > 1 && heads.SequenceEqual(Enumerable.Range(0, 40).Select(s => GameModes.PickHead(tied, s))),
        "Head: ties go by the seed: deterministic, and not always the same player");
    var teamsHh = new List<(int, int, int, int)> { (0, 0, 1, 0), (1, 1, 2, 0), (2, 0, 1, 0), (3, 1, 2, 0) };
    Check(Enumerable.Range(0, 20).All(s => GameModes.PickHead(teamsHh, s) is 1 or 3), "Head: in team modes it's a player on the leading team");
}

// ---------- Controllers and couch co-op ----------
{
    Check(Gamepad.Stick(0.15f, 0.1f, 0.22f) == Vector2.Zero, "Stick: inside the dead zone reads as centered");
    var full = Gamepad.Stick(1, 0, 0.22f);
    Check(Math.Abs(full.X - 1) < 1e-4f && full.Y == 0, "Stick: full deflection still reaches 1", $"{full}");
    var half = Gamepad.Stick(0, -0.61f, 0.22f);
    Check(half.X == 0 && half.Y < 0 && Math.Abs(half.Y + 0.5f) < 0.01f, "Stick: rescaled past the dead zone, direction kept", $"{half}");
    Check(Gamepad.Pressed(0b0101, 0b0110) == 0b0010, "Pad: only buttons that went down count as presses");
    Check(Gamepad.TriggerHeld(0.45f, false) && Gamepad.TriggerHeld(0.35f, true) && !Gamepad.TriggerHeld(0.35f, false),
        "Pad: triggers have a little hysteresis around the press point");

    var aim = new PadAim();
    Check(aim.Dir == Vector2.UnitX, "Aim: starts facing right");
    aim.Update(new PadState { RX = 0, RY = -1 });
    Check(aim.Aiming && Math.Abs(aim.Dir.Y + 1) < 1e-4f, "Aim: the right stick points the gun");
    aim.Update(new PadState());
    Check(!aim.Aiming && Math.Abs(aim.Dir.Y + 1) < 1e-4f, "Aim: let go, it keeps pointing where it was");
    aim.Update(new PadState { LX = -1 });
    Check(aim.Dir.X < -0.99f, "Aim: with the right stick resting, moving turns you that way");
    aim.Update(new PadState { LX = -0.4f, RX = 0.9f });
    Check(aim.Dir.X > 0.99f, "Aim: the right stick wins over the left");
    var me = Vector2.Zero;
    var ahead = new[] { new Vector2(200, 30) }; // about 8.5° below the aim line
    var bent = AimAssist.Bend(Vector2.UnitX, me, ahead, 0.5f);
    Check(bent.Y > 0 && bent.Y < 30 / 200f && Math.Abs(bent.Length() - 1) < 1e-4f, "Aim assist: bends part of the way toward an enemy near the line", $"{bent}");
    Check(AimAssist.Bend(Vector2.UnitX, me, ahead, 0) == Vector2.UnitX, "Aim assist: 0% leaves the aim alone");
    Check(AimAssist.Bend(Vector2.UnitX, me, new[] { new Vector2(0, 200) }, 1) == Vector2.UnitX, "Aim assist: an enemy well off the line doesn't pull");
    Check(AimAssist.Bend(Vector2.UnitX, me, new[] { new Vector2(900, 10) }, 1) == Vector2.UnitX, "Aim assist: an enemy out of range doesn't pull");
    var two = AimAssist.Bend(Vector2.UnitX, me, new[] { new Vector2(200, -60), new Vector2(200, 10) }, 1);
    Check(two.Y > 0, "Aim assist: the enemy nearest the line wins", $"{two}");
    Check(AimAssist.Bend(-Vector2.UnitX, me, new[] { new Vector2(-200, 5), new Vector2(-200, -5) }, 1).X < -0.99f, "Aim assist: works across the ±180° seam");

    foreach (var kind in Enum.GetValues<ColorVision.Kind>())
        Check(ColorVision.ClosestPair(ColorVision.Safe, kind) > 25, $"Colors: the safe set stays apart for {kind} vision", $"{ColorVision.ClosestPair(ColorVision.Safe, kind):0.0} ΔE");
    Check(ColorVision.ClosestPair(ColorVision.Usual, ColorVision.Kind.Deutan) < 5, "Colors: the usual blue and purple do merge for a deuteranope (why the safe set exists)");
    Check(ColorVision.Distance(ColorVision.Safe[0], ColorVision.Safe[1], ColorVision.Kind.Deutan) > 80, "Colors: the safe team colors (red and blue) are far apart");

    var held = new PadState { Buttons = 1u << PadLayout.LeftShoulder, RT = 0.9f, LT = 0.1f };
    Check(held.JumpHeld && held.ShootHeld && !held.BlockHeld && held.Active, "Pad: LB jumps, RT shoots, a resting LT doesn't block");

    var couch = new CouchRoster();
    Check(couch.Add(3) == 1 && couch.Add(0) == 2, "Couch: guests number from 1 in join order");
    Check(couch.Add(3) == -1, "Couch: a controller can't join twice");
    Check(couch.Claimed(0) && !couch.Claimed(1) && couch.DeviceOf(2) == 0, "Couch: knows whose controller is whose");
    couch.Remove(1);
    Check(couch.Add(5) == 1, "Couch: a freed number is reused");
    Check(couch.Add(6) == 3 && couch.Add(7) == -1 && couch.Count == CouchRoster.MaxGuests, "Couch: at most three guests per machine");
}

// ---------- Map designs and map codes ----------
{
    static string Shape(MapLayout m) => string.Join(";",
        string.Join(",", m.Solids), string.Join(",", m.Surfaces), string.Join(",", m.Slopes), string.Join(",", m.Ground.Select(g => string.Join("/", g))),
        string.Join(",", m.Spawns), string.Join(",", m.Pads), string.Join(",", m.Props), string.Join(",", m.Hazards),
        string.Join(",", m.Movers.Select(v => (v.X1, v.X2, v.Y))), string.Join(",", m.Saws.Select(s => (MathF.Round(s.X1), s.Y1, MathF.Round(s.X2), s.Y2))),
        string.Join(",", m.Drifts), string.Join(",", m.Hangers));
    static string Rounded(MapLayout m)
    {
        // Saws are stored to the pixel and phases to a sixteenth: compare the rest exactly.
        var copy = new MapLayout { Seed = m.Seed, Style = m.Style };
        copy.Solids.AddRange(m.Solids); copy.Surfaces.AddRange(m.Surfaces); copy.Slopes.AddRange(m.Slopes); copy.Ground.AddRange(m.Ground);
        copy.Spawns.AddRange(m.Spawns); copy.Pads.AddRange(m.Pads); copy.Props.AddRange(m.Props); copy.Hazards.AddRange(m.Hazards);
        copy.Movers.AddRange(m.Movers); copy.Saws.AddRange(m.Saws); copy.Drifts.AddRange(m.Drifts); copy.Hangers.AddRange(m.Hangers);
        return Shape(copy);
    }

    int same = 0, total = 0, longest = 0, sum = 0;
    string firstBad = "";
    for (int s = 0; s < 120; s++)
    {
        int seed = s * 104729 + 17;
        var gen = MapGenerator.Generate(seed, 4);
        var code = MapCode.Encode(MapDesign.From(gen));
        var back = MapCode.Decode(code);
        total++;
        longest = Math.Max(longest, code.Length);
        sum += code.Length;
        if (back != null && Rounded(back.Build(4)) == Rounded(gen)) same++;
        else if (firstBad == "") firstBad = $"seed {seed} {gen.Style}";
    }
    Check(same == total, "Map code: a generated arena survives From, Encode, Decode and Build unchanged", $"{same}/{total} {firstBad}");
    Check(sum / total <= 260, "Map code: a generated arena's code stays short", $"average {sum / total}, longest {longest} chars");

    var hilly = MapGenerator.Generate(Enumerable.Range(0, 400).First(s => MapGenerator.Generate(s, 4).Ground.Count > 0), 4);
    var feats = TerrainGen.Features(hilly);
    var rebuilt = new MapLayout();
    var hillFloor = MapDesign.From(hilly).Floor();
    TerrainGen.Apply(rebuilt, hillFloor, feats.Select(f => new List<(int x, int h)>(f)).ToList());
    Check(feats.Count > 0 && string.Join("|", rebuilt.Ground.Select(g => string.Join("/", g))) == string.Join("|", hilly.Ground.Select(g => string.Join("/", g)))
        && rebuilt.Slopes.SequenceEqual(hilly.Slopes), "Terrain: features read back from the ground outlines rebuild the same ground", $"{feats.Count} features");

    var empty = MapDesign.Empty(ThemeId.FrostBite, 1);
    empty.Name = "Ice rink";
    var e2 = MapCode.Decode(MapCode.Encode(empty));
    Check(e2 != null && e2.Name == "Ice rink" && e2.Theme == ThemeId.FrostBite, "Map code: name and theme come back", MapCode.Encode(empty));
    Check(MapCode.Encode(empty).Length < 40, "Map code: an empty arena is a few dozen characters", $"{MapCode.Encode(empty).Length}");
    var flat = empty.Build(6);
    Check(flat.Spawns.Count == 6 && flat.Spawns.Distinct().Count() == 6 && MapGenerator.SpawnsMutuallyReachable(flat), "Design: missing spawns are picked on reachable ground", string.Join(" ", flat.Spawns));
    Check(empty.Build(6).Spawns.SequenceEqual(flat.Spawns), "Design: picked spawns are the same every time (every peer agrees)");

    var d = MapDesign.Empty(ThemeId.NeonNight, 5);
    d.Pits.Add((400, 480));
    d.Blocks.Add(new RectI(200, MapGenerator.TierY(1), 120, 8));
    d.Hills.Add(TerrainGen.Hill(560, 32, 48));
    d.Hills.Add(TerrainGen.Hill(440, 32, 16)); // runs over the pit
    d.Spawns.Add(new SpawnPoint(100, MapGenerator.FloorY));
    d.Spawns.Add(new SpawnPoint(300, 200));     // in mid-air
    d.Pads.Add(new JumpPad(800, 800 + MapGenerator.PadWidth, MapGenerator.FloorY));
    d.Props.Add(new PropSpawn(PropKind.Glass, 400, MapGenerator.FloorY, 8, 8));
    d.Props.Add(new PropSpawn(PropKind.Glass, 408, MapGenerator.FloorY, 8, 8));
    d.Props.Add(new PropSpawn(PropKind.Crate, 250, MapGenerator.TierY(1) - 14, 14, 14));
    d.Hazards.Add(new Hazard(HazardKind.Poison, 700, 732, MapGenerator.FloorY));
    d.Saws.Add(new SawTrack(150, MapGenerator.FloorY - 2, 260, MapGenerator.FloorY - 2, ArenaFeatures.SawRadius, MapDesign.SawPeriod(150, 0, 260, 0), 0.25f));
    d.Movers.Add(new Mover(380, 460, 440, ArenaFeatures.MoverW, ArenaFeatures.MoverH, MapDesign.MoverPeriod(380, 460), 0.5f));
    d.Drifts.Add(new Drift(560, MapGenerator.TierY(1) - 40, 48, 8));
    d.Hangers.Add(new Hanger(640, 200, 64, 8));
    var dc = MapCode.Decode(MapCode.Encode(d));
    Check(dc != null && Rounded(dc.Build(4)) == Rounded(d.Build(4)) && dc.Props.Count == 3 && Math.Abs(dc.Saws[0].Phase - 0.25f) < 1e-4f
        && dc.Drifts.SequenceEqual(d.Drifts) && dc.Hangers.SequenceEqual(d.Hangers) && dc.Build(4).Surfaces.Any(s => s.Y == MapGenerator.TierY(1) - 40 && s.X1 == 560),
        "Map code: a hand-made arena survives the trip", MapCode.Encode(d));
    var built = d.Build(2);
    Check(built.Slopes.Count == 2 && built.Ground.Count == 1, "Design: a hill that runs over a pit is left out", $"{built.Slopes.Count} slopes");
    Check(!built.Spawns.Contains(new SpawnPoint(300, 200)) && built.Spawns.Count == 2, "Design: a spawn in mid-air is skipped and replaced", string.Join(" ", built.Spawns));
    var problems = d.Problems();
    Check(problems.Any(p => p.Contains("hill")) && problems.Any(p => p.Contains("Spawn 2")) && !problems.Any(p => p.Contains("pad")), "Design: problems name the hill and the floating spawn, not the grounded pad", string.Join(" | ", problems));
    Check(MapDesign.SurfaceBelow(built, 250, 100) is { } below && below.Y == MapGenerator.TierY(1) && MapDesign.SurfaceBelow(built, 440, 100) == null,
        "Design: dropping onto the surface below finds the platform, and nothing over the pit");

    var gen2 = MapDesign.Generate(MapStyle.Temple, ThemeId.MagmaCore, 1);
    Check(gen2.Style == MapStyle.Temple && gen2.Theme == ThemeId.MagmaCore && gen2.Blocks.Count > 0, "Design: generate a starting arena in a chosen style and theme", $"seed {gen2.Seed}");
    var t = gen2.Copy();
    t.SetTheme(ThemeId.CandyClouds);
    Check(t.Theme == ThemeId.CandyClouds && t.Blocks.SequenceEqual(gen2.Blocks), "Design: changing the theme keeps the layout");

    var good = MapCode.Encode(gen2);
    var broken = good[..10] + (good[10] == 'A' ? 'B' : 'A') + good[11..];
    Check(MapCode.Decode(broken) == null, "Map code: a changed character is caught");
    Check(MapCode.Decode(good[..(good.Length / 2)]) == null && MapCode.Decode("hello world!") == null && MapCode.Decode("") == null, "Map code: cut-off codes and junk are rejected");
    {
        MapCode.Decode("hello world!", out var junk);
        MapCode.Decode(good[..(good.Length / 2)], out var cut);
        var flipped = good[..10] + (good[10] == 'A' ? 'B' : 'A') + good[11..];
        MapCode.Decode(flipped, out var changed);
        var newer = MapCode.Pack(MapCode.Decode(good));
        newer[0] = (byte)(newer[0] & 0x0F | (MapCode.Version + 1) << 4);
        newer[^1] = MapCode.Checksum(newer, newer.Length - 1);
        MapCode.Decode(MapCode.Text(newer), out var future);
        MapCode.Decode(good, out var fine);
        Check(junk == MapCodeError.NotACode && cut == MapCodeError.Damaged && changed == MapCodeError.Damaged && future == MapCodeError.Newer && fine == MapCodeError.None,
            $"Map code: a bad code says why (junk {junk}, cut off {cut}, changed {changed}, newer {future})");
    }
    Check(MapCode.Decode(" " + good[..20] + "\n" + good[20..] + " ") != null, "Map code: spaces and line breaks in a paste are ignored");
    // The largest map a code can carry (every list full, coordinates spread out): what a lobby with 12 of them sends.
    var full = new MapDesign { Name = new string('W', MapDesign.MaxName), Seed = int.MaxValue, Style = MapStyle.Tiers };
    var fr = new Random(9);
    for (int i = 0; i < MapDesign.MaxPits; i++) full.Pits.Add((40 + i * 75, 70 + i * 75));
    for (int i = 0; i < MapDesign.MaxBlocks; i++) full.Blocks.Add(new RectI(8 + fr.Next(900), 8 + fr.Next(500), 8 + fr.Next(300), 8 + fr.Next(200)));
    for (int i = 0; i < MapDesign.MaxHills; i++) full.Hills.Add(Enumerable.Range(0, 15).Select(k => (8 + i * 120 + k * 8, fr.Next(60))).ToList());
    for (int i = 0; i < MapDesign.MaxSpawns; i++) full.Spawns.Add(new SpawnPoint(8 + fr.Next(980), 8 + fr.Next(520)));
    for (int i = 0; i < MapDesign.MaxPads; i++) full.Pads.Add(new JumpPad(8 + fr.Next(900), 990, 8 + fr.Next(520)));
    for (int i = 0; i < MapDesign.MaxProps; i++) full.Props.Add(new PropSpawn((PropKind)fr.Next(Enum.GetValues<PropKind>().Length), 8 + fr.Next(900), 8 + fr.Next(500), 8 + fr.Next(60), 8 + fr.Next(60)));
    for (int i = 0; i < MapDesign.MaxHazards; i++) full.Hazards.Add(new Hazard((HazardKind)fr.Next(Enum.GetValues<HazardKind>().Length), 8 + fr.Next(400), 500 + fr.Next(490), 8 + fr.Next(520)));
    for (int i = 0; i < MapDesign.MaxSaws; i++) full.Saws.Add(new SawTrack(8 + fr.Next(980), 8 + fr.Next(520), 8 + fr.Next(980), 8 + fr.Next(520), 6 + fr.Next(20), 1 + fr.Next(9), fr.NextSingle()));
    for (int i = 0; i < MapDesign.MaxMovers; i++) full.Movers.Add(new Mover(8 + fr.Next(400), 500 + fr.Next(400), 8 + fr.Next(520), 40, 8, 1 + fr.Next(9), fr.NextSingle()));
    for (int i = 0; i < MapDesign.MaxDrifts; i++) full.Drifts.Add(new Drift(8 * (1 + fr.Next(120)), 8 * (1 + fr.Next(60)), 8 * (2 + fr.Next(20)), 8 * (1 + fr.Next(4))));
    for (int i = 0; i < MapDesign.MaxHangers; i++) full.Hangers.Add(new Hanger(8 * (1 + fr.Next(120)), 8 * (1 + fr.Next(60)), 8 * (2 + fr.Next(20)), 8 * (1 + fr.Next(4))));
    Check(MapCode.Decode(MapCode.Encode(full)) is { } fullBack && fullBack.Drifts.SequenceEqual(full.Drifts) && fullBack.Hangers.SequenceEqual(full.Hangers),
        "Map code: full lists of drifting and hanging platforms survive the trip");
    int fullBytes = MapCode.Pack(full).Length;
    Console.WriteLine($"      (largest map: {fullBytes} bytes packed, {MapCode.Encode(full).Length}-character code)");
    Check(fullBytes <= 1500, $"Map code: the largest map packs into {fullBytes} bytes (at most 1500, so 12 fit a lobby sync)");
    Check(MapCode.Trim(new string('x', 40)).Length == MapDesign.MaxName && MapCode.Trim("  hi  ") == "hi", "Map code: names are trimmed to fit");
}

// ---------- Bot skill ----------
{
    var (easy, normal, hard) = (BotTuning.Of(BotSkill.Easy), BotTuning.Of(BotSkill.Normal), BotTuning.Of(BotSkill.Hard));
    Check(normal == new BotTuning(1f, 0.8f, 520, 0f, 0.55f, 140, 240), "Bot skill: Normal plays as bots always did (card stats come from it)");
    Check(easy.AimError > normal.AimError && normal.AimError > hard.AimError && easy.Reaction > normal.Reaction && easy.BlockChance < normal.BlockChance
          && normal.BlockChance < hard.BlockChance && easy.Range < normal.Range && normal.Range < hard.Range && easy.Lead < hard.Lead,
          "Bot skill: Easy is worse than Normal at everything, Hard better");
    Check(BotTuning.Next(BotSkill.Easy) == BotSkill.Normal && BotTuning.Next(BotSkill.Normal) == BotSkill.Hard && BotTuning.Next(BotSkill.Hard) == BotSkill.Easy,
          "Bot skill: the lobby button cycles Easy, Normal, Hard");
}

// ---------- Draft ----------
{
    Check(string.Join(",", Draft.Order(new[] { 4, 1, 7 }, 2)) == "4,1,7,7,1,4", "Draft: turns snake back (4,1,7 then 7,1,4)", string.Join(",", Draft.Order(new[] { 4, 1, 7 }, 2)));
    bool fits = true;
    for (int n = 2; n <= 8; n++) fits &= Draft.PoolSize(n) <= 10 && Draft.PoolSize(n) > n * Draft.PicksEach(n);
    Check(fits, "Draft: the pool holds everyone's picks with cards to spare, and never more than 10 (two rows on screen)");
}

// ---------- Cosmetic unlocks ----------
{
    var c = new Career { Matches = 12, Wins = 3, Kills = 140, BestWave = 4, GunGameWins = 1 };
    var back = Career.Decode(c.Encode());
    Check(back.Matches == 12 && back.Wins == 3 && back.Kills == 140 && back.BestWave == 4 && back.GunGameWins == 1, "Career: round-trips through its profile text");
    var odd = Career.Decode("kills=7;future=3;wins=-2;matches=x");
    Check(odd.Kills == 7 && odd.Wins == 0 && odd.Matches == 0 && Career.Decode(null).Matches == 0, "Career: unknown keys, negatives and junk are ignored");

    Check(Unlocks.All.All(u => u.Option > 0 && u.Option < Appearance.Count(u.Part)) && Unlocks.All.Select(u => (u.Part, u.Option)).Distinct().Count() == Unlocks.All.Length,
          "Unlocks: each is a real option, never a part's first (always free) one, and listed once");
    Check(Enumerable.Range(1, Appearance.Count(LookPart.Trail) - 1).All(t => Unlocks.For(LookPart.Trail, t) != null), "Unlocks: every trail but None is earned");
    Check(Unlocks.All.All(u => !u.Met(new Career())), "Unlocks: a new player has none");

    var look = Appearance.Default();
    look[LookPart.Hat] = 12;   // Laurel: win 10 matches
    look[LookPart.Trail] = 1;  // Sparks: play 3 matches
    Check(!Unlocks.Allowed(c, look) && Unlocks.Earned(c, look).Hat == 0 && Unlocks.Earned(c, look).Trail == 1 && Unlocks.Allowed(c, Unlocks.Earned(c, look)),
          "Unlocks: an unearned option comes off the look, earned ones stay");
    var gained = Unlocks.New(new Career { Matches = 2, Kills = 49 }, new Career { Matches = 3, Kills = 51 });
    Check(gained.Count == 2 && gained.Any(u => u.Name == "Sparks") && gained.Any(u => u.Name == "Pirate"), "Unlocks: crossing milestones lists what they unlock",
          string.Join(",", gained.Select(u => u.Name)));

    var rng = new Rng(9);
    bool free = true;
    for (int i = 0; i < 300; i++) free &= Unlocks.Allowed(new Career(), Appearance.Random(rng));
    Check(free, "Unlocks: random looks (bots, Randomize) only use free options");
    Check(Appearance.FromBytes(new byte[] { 1, 0, 2, 3, 4, 5, 6 }).Trail == 0, "Look: lobby data from before trails reads as no trail");
}

// ---------- Daily challenge ----------
{
    var today = Daily.For(20261023);
    var again = Daily.For(new DateTime(2026, 10, 23, 23, 59, 0, DateTimeKind.Utc));
    Check(today.Seed == again.Seed && today.ArenaSeed == again.ArenaSeed && today.Mode == again.Mode && today.StartingCards.SequenceEqual(again.StartingCards),
          "Daily: the same day gives the same challenge");
    var days = Enumerable.Range(0, 30).Select(i => Daily.For(new DateTime(2026, 11, 1).AddDays(i))).ToList();
    Check(days.Select(d => d.ArenaSeed).Distinct().Count() == 30 && days.Select(d => d.Mode).Distinct().Count() > 1, "Daily: days differ in arena and mode");
    Check(days.All(d => d.StartingCards.Count == Daily.StartingCardCount && d.StartingCards.Distinct().Count() == Daily.StartingCardCount
                        && d.StartingCards.All(id => CardRegistry.Get(id) is { IsRare: false, IsCurse: false } && !CardRegistry.IsCustom(id))),
          "Daily: three different built-in common starting cards");
    Check(days.All(d => Daily.Modes.Contains(d.Mode) && GameModes.Problem(d.Mode, 0) == null), "Daily: the mode works in free-for-all");
    Check(today.DateText == "2026-10-23", "Daily: date text");
    var arenaCode = MapCode.Encode(today.Arena());
    Check(arenaCode == MapCode.Encode(Daily.For(20261023).Arena()) && MapCode.Decode(arenaCode) is { } dayArena && dayArena.Name == "Daily 2026-10-23"
          && dayArena.Build(Daily.Bots + 1).Spawns.Count >= Daily.Bots + 1,
          "Daily: the day's arena is the same every time, survives a map code and has a spawn for everyone");

    var lost = new DailyResult(false, 4, 20);
    var won = new DailyResult(true, 5, 2);
    Check(won.CompareTo(lost) > 0 && new DailyResult(false, 4, 21).CompareTo(lost) > 0 && new DailyResult(false, 3, 99).CompareTo(lost) < 0,
          "Daily: a win beats any loss, then rounds, then kills");
    string s = DailyBest.Merge(null, 20261023, lost);
    Check(DailyBest.Decode(s, 20261023) == lost && DailyBest.Decode(s, 20261024) == null, "Daily: the best is kept for its own day only");
    Check(DailyBest.Merge(s, 20261023, new DailyResult(false, 1, 0)) == s && DailyBest.Decode(DailyBest.Merge(s, 20261023, won), 20261023) == won,
          "Daily: a worse run keeps the best, a better one replaces it");
    Check(DailyBest.Decode(DailyBest.Merge(s, 20261024, new DailyResult(false, 0, 0)), 20261024) == new DailyResult(false, 0, 0), "Daily: a new day starts over");
}

Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
