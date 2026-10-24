using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class AppearanceTests
{
    public static void Run()
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
}
