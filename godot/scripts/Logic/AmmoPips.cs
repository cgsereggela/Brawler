using System;

namespace Brawler.Logic;

/// <summary>
/// Layout of the ammo pips beside the crosshair: a small block of upright pips at its lower right (mirroring the
/// reload dial at the upper right), one per bullet, in rows of <see cref="PerRow"/> filled left to right, top to
/// bottom, so firing empties them from the end. Past <see cref="MaxPips"/> bullets the block becomes one short gauge
/// bar plus the count as a number. Coordinates are relative to the crosshair center (screen y points down).
/// Engine-independent so the layout can be unit tested.
/// </summary>
public static class AmmoPips
{
    public const int MaxPips = 24, PerRow = 8;
    public const float PipW = 1.8f, PipH = 3.4f, GapX = 1.1f, GapY = 1.3f;
    /// <summary>Top-left corner of the block, clear of the cross's ticks even while they spread after a shot.</summary>
    public const float X0 = 9f, Y0 = 7f;

    public static bool Continuous(int mag) => mag > MaxPips;

    public static int Rows(int mag) => Continuous(mag) ? 1 : (Math.Max(1, mag) + PerRow - 1) / PerRow;

    /// <summary>Width of the block: a full row of pips (or the gauge, which is as wide).</summary>
    public static float Width(int mag)
    {
        int cols = Continuous(mag) ? PerRow : Math.Min(Math.Max(1, mag), PerRow);
        return cols * PipW + (cols - 1) * GapX;
    }

    /// <summary>Top-left corner of pip <paramref name="i"/>; pips 0..ammo-1 are the full ones.</summary>
    public static (float X, float Y) Pip(int i) => (X0 + i % PerRow * (PipW + GapX), Y0 + i / PerRow * (PipH + GapY));

    /// <summary>How full the gauge of a big magazine is, 0..1.</summary>
    public static float Gauge(int ammo, int mag) => Math.Clamp(ammo, 0, Math.Max(1, mag)) / (float)Math.Max(1, mag);
}
