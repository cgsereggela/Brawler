namespace Brawler.Logic;

/// <summary>
/// Small deterministic PRNG (PCG32). Identical sequence on every peer for the same seed,
/// independent of the .NET runtime's System.Random implementation.
/// </summary>
public sealed class Rng
{
    private ulong _state;
    private const ulong Inc = 1442695040888963407UL;

    public Rng(ulong seed)
    {
        _state = 0;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        ulong old = _state;
        _state = old * 6364136223846793005UL + Inc;
        uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
    }

    /// <summary>Uniform float in [0, 1).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

    /// <summary>Uniform int in [min, max] inclusive.</summary>
    public int Range(int min, int max)
    {
        if (max <= min) return min;
        return min + (int)(NextUInt() % (uint)(max - min + 1));
    }

    public float Range(float min, float max) => min + (max - min) * NextFloat();

    public bool Chance(float p) => NextFloat() < p;
}
