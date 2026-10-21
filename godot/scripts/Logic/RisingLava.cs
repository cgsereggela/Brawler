using System;

namespace Brawler.Logic;

/// <summary>
/// Sudden death: once a battle has run <c>after</c> seconds, lava wells up from the bottom of the arena and keeps
/// climbing, slowly at first and then faster, until it has swallowed everything. Campers have to come up and fight.
/// The level is a pure function of the battle clock, so every peer agrees without syncing it.
/// </summary>
public static class RisingLava
{
    /// <summary>Lobby setting choices, in seconds of fighting (0 = never).</summary>
    public static readonly int[] Choices = { 0, 20, 30, 45, 60, 90 };
    public const int DefaultAfter = 45;

    /// <summary>Where it starts: just under the bottom edge, so the first second or two only shows its glow.</summary>
    public const float StartY = MapLayout.Height + 12;
    /// <summary>Rise speed when it starts (px/s), and how fast that speed grows (px/s²).</summary>
    public const float Speed = 14, Accel = 0.9f;
    /// <summary>Burn per second with your feet in it, and the hop it gives you (px/s) to help you out.</summary>
    public const float Dps = 60, Pop = 300;
    /// <summary>Seconds of warning before it starts.</summary>
    public const float Warning = 5;

    /// <summary>Seconds since it started rising (negative before), from seconds of fighting.</summary>
    public static float Rising(float fightTime, int after) => after <= 0 ? float.NegativeInfinity : fightTime - after;

    /// <summary>The lava's surface (y, down is +) after <paramref name="fightTime"/> seconds of fighting; +∞ while it hasn't started.</summary>
    public static float Level(float fightTime, int after)
    {
        float t = Rising(fightTime, after);
        if (t <= 0) return float.PositiveInfinity;
        return MathF.Max(-20, StartY - (Speed * t + 0.5f * Accel * t * t));
    }

    /// <summary>Seconds of rising until the surface reaches <paramref name="y"/>.</summary>
    public static float TimeToReach(float y)
    {
        float d = StartY - y;
        if (d <= 0) return 0;
        // Solve Speed*t + Accel/2*t² = d.
        return (-Speed + MathF.Sqrt(Speed * Speed + 2 * Accel * d)) / Accel;
    }

    /// <summary>Nearest choice to a stored or received value, so odd values fall back to something the lobby can show.</summary>
    public static int Snap(int after)
    {
        int best = Choices[0];
        foreach (int c in Choices)
            if (Math.Abs(c - after) < Math.Abs(best - after)) best = c;
        return best;
    }
}
