using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class SuddenDeathLavaTests
{
    public static void Run()
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
}
