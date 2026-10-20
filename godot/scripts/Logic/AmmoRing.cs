using System;

namespace Brawler.Logic;

/// <summary>
/// Layout of the ammo ring around the crosshair: one arc per bullet in the magazine, clockwise from the top, with a
/// small gap between them. Past <see cref="MaxPips"/> bullets the pips would be slivers, so the ring becomes one
/// continuous gauge with a tick every <see cref="TickEvery"/> bullets (and the crosshair adds the count as a number).
/// Engine-independent so the layout can be unit tested.
/// </summary>
public static class AmmoRing
{
    public const int MaxPips = 24, TickEvery = 5;
    /// <summary>Magazines at least this big also show the ammo count as a number.</summary>
    public const int NumberFrom = 10;
    /// <summary>Angle of the first pip's start: straight up (screen y points down).</summary>
    public const float Top = -MathF.PI / 2;

    public static bool Continuous(int mag) => mag > MaxPips;

    /// <summary>Gap between pips, in radians, for a ring of the given radius (about 2.6 units of arc, never more than a third of a pip).</summary>
    public static float Gap(int mag, float radius)
    {
        if (mag <= 1 || Continuous(mag)) return 0;
        return MathF.Min(2.6f / MathF.Max(1, radius), 2 * MathF.PI / mag / 3);
    }

    /// <summary>Start and end angle of pip <paramref name="i"/> : pip 0 starts at the top, and pips 0..ammo-1 are the full ones, so firing empties the ring counterclockwise.</summary>
    public static (float From, float To) Pip(int i, int mag, float radius)
    {
        float step = 2 * MathF.PI / Math.Max(1, mag), gap = Gap(mag, radius);
        float from = Top + i * step + gap / 2;
        return (from, from + step - gap);
    }

    /// <summary>The gauge arc for a continuous ring: from the top, clockwise, as far as the ammo left.</summary>
    public static (float From, float To) Gauge(int ammo, int mag) =>
        (Top, Top + 2 * MathF.PI * Math.Clamp(ammo, 0, Math.Max(1, mag)) / Math.Max(1, mag));
}
