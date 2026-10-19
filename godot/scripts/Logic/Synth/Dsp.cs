using System;

namespace Brawler.Logic.Synth;

/// <summary>Small offline DSP toolkit used to synthesize the game's sound effects and music. Pure C#, no engine types.</summary>
public static class Dsp
{
    public const int Rate = 44100;
    public const float Tau = MathF.PI * 2;

    public static float Hz(float midi) => 440f * MathF.Pow(2, (midi - 69) / 12f);
    public static float Cents(float c) => MathF.Pow(2, c / 1200f);
    public static float Db(float db) => MathF.Pow(10, db / 20f);
    public static float Decay(float t, float tau) => t < 0 ? 0 : MathF.Exp(-t / tau);

    /// <summary>Linear attack then exponential decay.</summary>
    public static float Perc(float t, float attack, float tau) => t < 0 ? 0 : t < attack ? t / attack : MathF.Exp(-(t - attack) / tau);

    /// <summary>ADSR for a note held <paramref name="len"/> seconds.</summary>
    public static float Adsr(float t, float len, float a, float d, float s, float r)
    {
        if (t < 0) return 0;
        float held = t < a ? t / a : s + (1 - s) * MathF.Exp(-(t - a) / d);
        if (t <= len) return held;
        float atRelease = len < a ? len / a : s + (1 - s) * MathF.Exp(-(len - a) / d);
        return atRelease * MathF.Exp(-(t - len) / r);
    }

    /// <summary>Peak-normalizes in place to <paramref name="peak"/>.</summary>
    public static void Normalize(float[] buf, float peak)
    {
        float m = 0;
        foreach (var v in buf) m = MathF.Max(m, MathF.Abs(v));
        if (m < 1e-6f) return;
        float k = peak / m;
        for (int i = 0; i < buf.Length; i++) buf[i] *= k;
    }

    /// <summary>Float samples to little-endian 16-bit PCM.</summary>
    public static byte[] ToPcm16(float[] buf)
    {
        var data = new byte[buf.Length * 2];
        for (int i = 0; i < buf.Length; i++)
        {
            short v = (short)(Math.Clamp(buf[i], -1f, 1f) * 32000);
            data[i * 2] = (byte)(v & 0xff);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return data;
    }

    /// <summary>A minimal RIFF/WAV file, for auditioning sounds outside the game.</summary>
    public static byte[] ToWav(float[] buf, int channels)
    {
        var pcm = ToPcm16(buf);
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(Rate);
        w.Write(Rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }
}

/// <summary>Deterministic white noise (xorshift).</summary>
public struct Noise
{
    private uint _s;
    public Noise(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed;
    public float Next()
    {
        _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
        return (_s & 0xFFFFFF) / (float)0x800000 - 1f;
    }
    public float Unit() => (Next() + 1) * 0.5f;
}

/// <summary>Band-limited oscillator (PolyBLEP saw and pulse) plus sine and triangle.</summary>
public struct Osc
{
    public float Phase;

    private static float Blep(float t, float dt)
    {
        if (t < dt) { t /= dt; return t + t - t * t - 1; }
        if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
        return 0;
    }

    private void Advance(float dt) { Phase += dt; if (Phase >= 1) Phase -= MathF.Floor(Phase); }

    public float Saw(float hz)
    {
        float dt = hz / Dsp.Rate;
        float v = 2 * Phase - 1 - Blep(Phase, dt);
        Advance(dt);
        return v;
    }

    public float Pulse(float hz, float width = 0.5f)
    {
        float dt = hz / Dsp.Rate;
        float v = Phase < width ? 1 : -1;
        v += Blep(Phase, dt);
        float p2 = Phase - width; if (p2 < 0) p2 += 1;
        v -= Blep(p2, dt);
        Advance(dt);
        return v;
    }

    public float Sine(float hz)
    {
        float v = MathF.Sin(Phase * Dsp.Tau);
        Advance(hz / Dsp.Rate);
        return v;
    }

    public float Tri(float hz)
    {
        float v = Phase < 0.5f ? 4 * Phase - 1 : 3 - 4 * Phase;
        Advance(hz / Dsp.Rate);
        return v;
    }
}

/// <summary>Zero-delay state-variable filter (Zavalishin TPT). Call <see cref="Set"/> when the cutoff moves.</summary>
public struct Svf
{
    private float _ic1, _ic2, _a1, _a2, _a3, _k;
    public float Lp, Bp, Hp;

    public Svf(float cutoff, float q = 0.707f) : this() => Set(cutoff, q);

    public void Set(float cutoff, float q = 0.707f)
    {
        float g = MathF.Tan(MathF.PI * Math.Clamp(cutoff, 15f, Dsp.Rate * 0.46f) / Dsp.Rate);
        _k = 1 / q;
        _a1 = 1 / (1 + g * (g + _k));
        _a2 = g * _a1;
        _a3 = g * _a2;
    }

    public void Process(float x)
    {
        float v3 = x - _ic2;
        float v1 = _a1 * _ic1 + _a2 * v3;
        float v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
        _ic1 = 2 * v1 - _ic1;
        _ic2 = 2 * v2 - _ic2;
        Lp = v2; Bp = v1; Hp = x - _k * v1 - v2;
    }

    public float LowPass(float x) { Process(x); return Lp; }
    public float HighPass(float x) { Process(x); return Hp; }
    public float BandPass(float x) { Process(x); return Bp; }
}

/// <summary>Stereo Freeverb-style reverb: 8 damped combs and 4 allpasses per side.</summary>
public sealed class Reverb
{
    private static readonly int[] CombLen = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] ApLen = { 556, 441, 341, 225 };
    private const int Spread = 23;

    private readonly float[][] _combL = new float[8][], _combR = new float[8][], _apL = new float[4][], _apR = new float[4][];
    private readonly int[] _ciL = new int[8], _ciR = new int[8], _aiL = new int[4], _aiR = new int[4];
    private readonly float[] _stL = new float[8], _stR = new float[8];
    private readonly float _feedback, _damp;

    public Reverb(float room = 0.84f, float damp = 0.35f)
    {
        _feedback = room;
        _damp = damp;
        for (int i = 0; i < 8; i++) { _combL[i] = new float[CombLen[i]]; _combR[i] = new float[CombLen[i] + Spread]; }
        for (int i = 0; i < 4; i++) { _apL[i] = new float[ApLen[i]]; _apR[i] = new float[ApLen[i] + Spread]; }
    }

    public (float L, float R) Process(float input)
    {
        float x = input * 0.015f, l = 0, r = 0;
        for (int i = 0; i < 8; i++)
        {
            l += Comb(_combL[i], ref _ciL[i], ref _stL[i], x);
            r += Comb(_combR[i], ref _ciR[i], ref _stR[i], x);
        }
        for (int i = 0; i < 4; i++)
        {
            l = Allpass(_apL[i], ref _aiL[i], l);
            r = Allpass(_apR[i], ref _aiR[i], r);
        }
        return (l, r);
    }

    private float Comb(float[] buf, ref int idx, ref float store, float x)
    {
        float y = buf[idx];
        store = y * (1 - _damp) + store * _damp;
        buf[idx] = x + store * _feedback;
        if (++idx >= buf.Length) idx = 0;
        return y;
    }

    private static float Allpass(float[] buf, ref int idx, float x)
    {
        float b = buf[idx];
        float y = -x + b;
        buf[idx] = x + b * 0.5f;
        if (++idx >= buf.Length) idx = 0;
        return y;
    }
}

/// <summary>Stereo ping-pong delay with a darkening feedback path.</summary>
public sealed class PingPong
{
    private readonly float[] _l, _r;
    private int _i;
    private readonly float _fb;
    private Svf _toneL = new(3500), _toneR = new(3500);

    public PingPong(float seconds, float feedback)
    {
        int n = Math.Max(1, (int)(seconds * Dsp.Rate));
        _l = new float[n];
        _r = new float[n];
        _fb = feedback;
    }

    public (float L, float R) Process(float input)
    {
        float l = _l[_i], r = _r[_i];
        _l[_i] = _toneL.LowPass(input + r * _fb);
        _r[_i] = _toneR.LowPass(l * _fb);
        if (++_i >= _l.Length) _i = 0;
        return (l, r);
    }
}
