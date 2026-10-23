using System;
using System.Linq;
using Brawler.Logic;
using Godot;

namespace Brawler.Core;

/// <summary>
/// The seed behind everything random that steers a match: the host's rolls (arenas, card offers, splits), each bot's
/// decisions, autopilot card picks and Godot's global random numbers. --seed=N sets it; otherwise it comes from the
/// clock. The checks print it with their result, so a failing run can be replayed exactly with --seed=N (bots and
/// picks only replay under --fixed-fps, where every frame is the same length).
/// </summary>
public static class RunSeed
{
    public static ulong Value { get; private set; }
    public static bool Given { get; private set; }
    private static Rng _subs;

    /// <summary>Before anything rolls (the GameManager's host rolls included).</summary>
    public static void Init()
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--seed="));
        Given = arg != null && ulong.TryParse(arg["--seed=".Length..], out ulong v) && Set(v);
        // Short enough to retype from a log line.
        if (!Given) Set((ulong)(DateTime.Now.Ticks % 1_000_000_000));
    }

    private static bool Set(ulong v)
    {
        Value = v;
        _subs = new Rng(v ^ 0x5EED_5EEDUL);
        GD.Seed(v);
        return true;
    }

    /// <summary>A seed of its own for the next thing that asks (a bot's brain, the autopilot's picks), in order.</summary>
    public static ulong Next() => (ulong)_subs.NextUInt() << 32 | _subs.NextUInt();
}
