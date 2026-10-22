using System;
using System.Collections.Generic;

namespace Brawler.Logic;

/// <summary>
/// The player colors, the usual set and a colorblind-safe one, and a check of how far apart a set of colors stays for
/// someone with protanopia, deuteranopia or tritanopia. The simulation uses Machado, Oliveira and Fernandes (2009) at
/// full severity in linear RGB; distances are CIE76 ΔE in Lab. Pure, so the tests cover the safe set.
/// </summary>
public static class ColorVision
{
    /// <summary>Red, Blue, Green, Yellow, Purple, Orange, Cyan, Pink.</summary>
    public static readonly string[] Usual = { "#ff4d5a", "#3d9bff", "#4ee06e", "#ffd23d", "#b46cff", "#ff9233", "#3de6e0", "#ff7ad9" };

    /// <summary>
    /// The same eight, each still close to its name, picked by a search that kept the closest pair as far apart as it
    /// could under every kind of color blindness. The usual blue and purple look the same to a deuteranope.
    /// </summary>
    public static readonly string[] Safe = { "#e24213", "#6278c7", "#3eb867", "#ffff00", "#9a53ff", "#ffc550", "#53ffff", "#d86f9e" };

    public enum Kind { Normal, Protan, Deutan, Tritan }

    private static readonly float[][,] Matrices =
    {
        new float[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } },
        new float[,] { { 0.152286f, 1.052583f, -0.204868f }, { 0.114503f, 0.786281f, 0.099216f }, { -0.003882f, -0.048116f, 1.051998f } },
        new float[,] { { 0.367322f, 0.860646f, -0.227968f }, { 0.280085f, 0.672501f, 0.047413f }, { -0.011820f, 0.042940f, 0.968881f } },
        new float[,] { { 1.255528f, -0.076749f, -0.178779f }, { -0.078411f, 0.930809f, 0.147602f }, { 0.004733f, 0.691367f, 0.303900f } },
    };

    /// <summary>A "#rrggbb" color as seen with <paramref name="kind"/>, in CIE Lab.</summary>
    public static (float L, float A, float B) Seen(string hex, Kind kind)
    {
        var m = Matrices[(int)kind];
        float[] v = new float[3];
        for (int i = 0; i < 3; i++) v[i] = Linear(Convert.ToInt32(hex.Substring(1 + 2 * i, 2), 16) / 255f);
        float[] o = new float[3];
        for (int r = 0; r < 3; r++) o[r] = Math.Clamp(m[r, 0] * v[0] + m[r, 1] * v[1] + m[r, 2] * v[2], 0, 1);
        float x = (0.4124f * o[0] + 0.3576f * o[1] + 0.1805f * o[2]) / 0.9505f;
        float y = 0.2126f * o[0] + 0.7152f * o[1] + 0.0722f * o[2];
        float z = (0.0193f * o[0] + 0.1192f * o[1] + 0.9505f * o[2]) / 1.089f;
        float fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>The smallest ΔE between any two of <paramref name="colors"/> as seen with <paramref name="kind"/>.</summary>
    public static float ClosestPair(IReadOnlyList<string> colors, Kind kind)
    {
        float min = float.MaxValue;
        for (int i = 0; i < colors.Count; i++)
        for (int j = i + 1; j < colors.Count; j++)
            min = Math.Min(min, Distance(colors[i], colors[j], kind));
        return min;
    }

    public static float Distance(string a, string b, Kind kind)
    {
        var p = Seen(a, kind);
        var q = Seen(b, kind);
        return MathF.Sqrt((p.L - q.L) * (p.L - q.L) + (p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B));
    }

    private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    private static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
}
