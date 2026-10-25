using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class ArenaAndMovementScaleTests
{
    public static void Run()
    {
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
            // "Cannot block" is 999 s of cooldown underneath, multiplied by any block cards held with it: say so instead.
            var noBlock = StatSummary.Lines(StatBlock.Compute([Named("All In"), Named("Reflector"), Named("Riposte")]));
            Check(noBlock.Any(l => l.Label == "Block" && l.Value == "can't") && StatSummary.Lines(StatBlock.Base()).Any(l => l.Label == "Block" && l.Value == "4.0s"),
                "The HUD stats say a cannot-block loadout can't block (not 1000+ seconds)", string.Join(", ", noBlock.Select(l => $"{l.Label}={l.Value}")));
        }
    }
}
