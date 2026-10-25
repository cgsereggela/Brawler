using System;

namespace Brawler.Logic;

/// <summary>
/// The kill cam's clock: which stretch of the battle it replays around the final kill, and how fast. Times are battle
/// seconds relative to the kill (negative = before it). The replay runs near full speed for the build-up, eases into
/// slow motion just before the kill and holds it through the hit, then picks up again for the aftermath.
/// </summary>
public static class KillCamTiming
{
    /// <summary>Seconds replayed before the kill, and after it.</summary>
    public const float Pre = 3.6f, Post = 0.5f;
    /// <summary>Battle seconds the replay covers (it plays them back in <see cref="RealSeconds"/>).</summary>
    public const float Length = Pre + Post;
    public const float Fast = 1f, Slow = 0.3f, Exit = 0.7f;
    private const float SlowFrom = -0.5f, SlowAt = -0.15f, SlowUntil = 0.2f;

    /// <summary>Playback speed (battle seconds per real second) at <paramref name="t"/> seconds from the kill.</summary>
    public static float Speed(float t)
    {
        if (t <= SlowFrom) return Fast;
        if (t < SlowAt) return Lerp(Fast, Slow, Smooth((t - SlowFrom) / (SlowAt - SlowFrom)));
        if (t <= SlowUntil) return Slow;
        return Lerp(Slow, Exit, Smooth(Math.Min(1, (t - SlowUntil) / (Post - SlowUntil))));
    }

    /// <summary>How long the whole replay takes to watch, in real seconds.</summary>
    public static float RealSeconds()
    {
        const int steps = 2000;
        float h = Length / steps, sum = 0;
        for (int i = 0; i < steps; i++) sum += h / Speed(-Pre + (i + 0.5f) * h);
        return sum;
    }

    private static float Smooth(float x) => x * x * (3 - 2 * x);
    private static float Lerp(float a, float b, float k) => a + (b - a) * k;
}
