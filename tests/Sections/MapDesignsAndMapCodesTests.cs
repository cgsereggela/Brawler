using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class MapDesignsAndMapCodesTests
{
    public static void Run()
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
}
