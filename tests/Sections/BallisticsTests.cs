using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class BallisticsTests
{
    public static void Run()
    {
        var none = new List<RectI>();
        var noSlopes = new List<Slope>();
        bool allLand = true; string miss = "";
        foreach (var (from, to, v, g) in new[] { (new Vector2(100, 400), new Vector2(500, 400), 600f, 500f), (new Vector2(700, 300), new Vector2(200, 450), 700f, 900f),
                                                 (new Vector2(300, 500), new Vector2(420, 250), 700f, 600f), (new Vector2(50, 200), new Vector2(900, 380), 800f, 300f) })
        {
            if (!Ballistics.LobAim(from, to, v, g, none, noSlopes, out var dir)) { allLand = false; miss = $"no lob {from}->{to}"; break; }
            float t = (to.X - from.X) / (dir.X * v);
            var at = Ballistics.At(from, dir * v, g, t);
            if (Vector2.Distance(at, to) > 0.5f || MathF.Abs(dir.Length() - 1) > 1e-4f) { allLand = false; miss = $"{from}->{to} lands at {at}"; break; }
        }
        Check(allLand, "A bot's lob lands where it aimed", miss);

        // A wall halfway along: the flat arc would hit it, so the bot lobs over it.
        var from0 = new Vector2(100, 400); var to0 = new Vector2(500, 400);
        Ballistics.LobAim(from0, to0, 600, 500, none, noSlopes, out var flat);
        var wall = new List<RectI> { new(290, 300, 20, 100) };
        bool over = Ballistics.LobAim(from0, to0, 600, 500, wall, noSlopes, out var high);
        Check(over && high.Y < flat.Y - 0.1f && Ballistics.ArcClear(from0, high * 600, 500, 400 / (high.X * 600), wall, noSlopes)
              && !Ballistics.ArcClear(from0, flat * 600, 500, 400 / (flat.X * 600), wall, noSlopes),
              "A lob goes high over a wall the flat arc would hit");
        var roof = new List<RectI> { new(0, 0, 1000, 395) };
        Check(!Ballistics.LobAim(from0, to0, 600, 500, roof, noSlopes, out _), "No lob under a roof too low for either arc");
        Check(!Ballistics.LobAim(from0, new Vector2(5000, 400), 300, 500, none, noSlopes, out _), "No lob to a spot out of range");
        Check(!Ballistics.LobAim(from0, to0, 600, 0, none, noSlopes, out _), "No lob for a bullet that doesn't drop");

        var floor = new List<RectI> { new(0, 500, 400, 40), new(520, 500, 480, 40) };
        Check(Ballistics.JumpLands(new Vector2(380, 500), 300, -500, 1200, 8, floor, noSlopes), "A running jump clears a narrow gap");
        var pit = new List<RectI> { new(0, 500, 400, 40) };
        Check(!Ballistics.JumpLands(new Vector2(380, 500), 300, -500, 1200, 8, pit, noSlopes), "A running jump off the edge into a pit doesn't land");
        var ramp = new List<Slope> { new(420, 520, 700, 460, 560) };
        Check(Ballistics.JumpLands(new Vector2(380, 500), 300, -500, 1200, 8, pit, ramp), "A running jump lands on a ramp");

        float ft = Ballistics.FallTime(-120, 200, 980);
        Check(MathF.Abs(Ballistics.At(Vector2.Zero, new Vector2(0, -120), 980, ft).Y - 200) < 0.01f && Ballistics.FallTime(50, 0, 980) == 0,
              "A falling platform's fall time brings it down the whole drop");

        var snap = new AvatarSnap(5, true, true, new Vector2(123.25f, -40.5f), new Vector2(-300, 812.75f), 37.2f, 12, 0.5f, 1.3f, 0.21f, -2.4f);
        var ms = new System.IO.MemoryStream();
        var bw = new System.IO.BinaryWriter(ms);
        snap.Write(bw); bw.Flush();
        var back = AvatarSnap.Read(new System.IO.BinaryReader(new System.IO.MemoryStream(ms.ToArray())));
        Check(ms.Length == AvatarSnap.Size && back.Slot == 5 && back.Alive && back.Reloading && back.Pos == snap.Pos && back.Vel == snap.Vel
              && back.Hp == 38 && back.Ammo == 12 && MathF.Abs(back.ReloadFrac - 0.5f) < 1 / 255f && MathF.Abs(back.BlockCd - 1.3f) < 0.05f
              && MathF.Abs(back.BlockActive - 0.21f) < 0.01f && MathF.Abs(back.Aim + 2.4f) < 0.001f,
              "A packed snapshot round-trips (to its precision)", back.ToString());
        ms.SetLength(0);
        new AvatarSnap(0, false, false, default, default, 0.1f, 400, 1, 99, 99, MathF.PI).Write(bw); bw.Flush();
        var edge = AvatarSnap.Read(new System.IO.BinaryReader(new System.IO.MemoryStream(ms.ToArray())));
        Check(!edge.Alive && !edge.Reloading && edge.Hp == 1 && edge.Ammo == 255 && edge.ReloadFrac == 1 && edge.BlockCd == 255 / 20f
              && edge.BlockActive == 2.55f && MathF.Abs(edge.Aim - MathF.PI) < 0.001f,
              "A snapshot keeps a sliver of HP as 1 and clamps what doesn't fit a byte", edge.ToString());
    }
}
