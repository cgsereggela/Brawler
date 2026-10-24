using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class GameModesTests
{
    public static void Run()
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
}
