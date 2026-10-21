using System;

namespace Brawler.Logic;

/// <summary>
/// Keeps effects from dragging the frame rate down: watches real frame times and shrinks the particle budget while
/// effects make frames run long, then lets it grow back once they're quick again. <see cref="Quality"/> (0 to 1) also
/// tells the effects layer to emit fewer particles and skip the costly extras (glow halos).
/// <para>
/// "Long" is judged against the frame time measured while few particles are alive, so a machine that is simply slow
/// everywhere keeps its full budget, and only the slowdown the effects themselves add gets culled.
/// </para>
/// </summary>
public sealed class FxBudget
{
    public readonly int Max, Min;

    /// <summary>Never cull while frames are quicker than this (seconds), whatever the baseline.</summary>
    public float SlowFrame = 1f / 50f;

    private float _avg, _base, _cap, _calm;

    public FxBudget(int max, int min)
    {
        Max = max;
        Min = Math.Min(min, max);
        Reset();
    }

    /// <summary>How many particles may be alive right now.</summary>
    public int Cap => (int)_cap;

    /// <summary>1 at the full budget, falling toward 0 as it shrinks.</summary>
    public float Quality => Max == Min ? 1 : (_cap - Min) / (Max - Min);

    /// <summary>Smoothed frame time in seconds.</summary>
    public float AverageFrame => _avg;

    /// <summary>Smoothed frame time while few particles were alive: what a frame costs without effects.</summary>
    public float Baseline => _base;

    /// <summary>Frames slower than this count as slow.</summary>
    public float SlowLimit => Math.Max(SlowFrame, _base * 1.3f + 0.002f);

    /// <summary>Feeds one frame's real duration and how many particles were alive.</summary>
    public void Frame(float seconds, int live)
    {
        // A single huge frame (a hitch, loading, the window being dragged) says nothing about effects.
        seconds = Math.Clamp(seconds, 0, 0.1f);
        _avg += (seconds - _avg) * 0.15f;
        if (live < Max * 0.05f) _base += (seconds - _base) * 0.05f;

        float slow = SlowLimit;
        if (_avg > slow && live > _cap * 0.25f)
        {
            // Cut fast, harder the slower it is: about a third per 100 ms at twice the limit.
            float over = Math.Min(2f, _avg / slow - 1f);
            _cap = Math.Max(Min, _cap * (1f - Math.Min(seconds, 0.05f) * (2f + 2f * over)));
            _calm = 0;
        }
        else if (_avg < slow * 0.9f)
        {
            // Grow back slowly, and only after a short calm, so it doesn't saw up and down.
            _calm += seconds;
            if (_calm > 0.5f) _cap = Math.Min(Max, _cap + Max * 0.25f * seconds);
        }
        else _calm = 0;
    }

    /// <summary>How many of <paramref name="count"/> requested particles to actually emit at the current quality.</summary>
    public int Scale(int count)
    {
        if (count <= 0) return 0;
        float q = 0.25f + 0.75f * Quality;
        return Math.Max(1, (int)MathF.Round(count * q));
    }

    public void Reset()
    {
        _cap = Max;
        _avg = _base = 1f / 60f;
        _calm = 0;
    }
}
