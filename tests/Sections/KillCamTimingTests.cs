using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class KillCamTimingTests
{
    public static void Run()
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
        Check(real > 5f && real < 6.5f, "Kill cam: takes 5-6.5 s to watch", $"{real:0.00}s");
    }
}
