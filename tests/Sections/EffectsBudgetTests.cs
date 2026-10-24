using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class EffectsBudgetTests
{
    public static void Run()
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
}
