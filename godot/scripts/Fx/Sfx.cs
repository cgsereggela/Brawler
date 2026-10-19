using System;
using System.Collections.Generic;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// Procedurally synthesized retro sound effects (no audio assets needed) played through a small voice pool.
/// </summary>
public partial class Sfx : Node
{
    private const int Rate = 22050;
    private readonly Dictionary<string, AudioStreamWav> _sounds = new();
    private readonly List<AudioStreamPlayer> _voices = new();
    private readonly Dictionary<string, ulong> _lastPlayed = new();
    private readonly Random _rng = new();
    private int _next;

    public override void _Ready()
    {
        _sounds["shoot"] = Make(0.09f, t => Square(Sweep(880, 220, t, 0.09f), t) * Env(t, 0.09f) * 0.5f);
        _sounds["hit"] = Make(0.12f, t => (Noise() * 0.6f + Square(Sweep(300, 90, t, 0.12f), t) * 0.4f) * Env(t, 0.12f));
        _sounds["death"] = Make(0.45f, t => (Noise() * 0.7f + Square(Sweep(220, 40, t, 0.45f), t) * 0.5f) * Env(t, 0.45f));
        _sounds["block"] = Make(0.18f, t => (Sine(1320, t) * 0.6f + Sine(1980, t) * 0.3f) * Env(t, 0.18f));
        _sounds["blocked"] = Make(0.12f, t => Square(Sweep(1600, 2400, t, 0.12f), t) * Env(t, 0.12f) * 0.4f);
        _sounds["bounce"] = Make(0.05f, t => Square(Sweep(1200, 1600, t, 0.05f), t) * Env(t, 0.05f) * 0.3f);
        _sounds["explode"] = Make(0.4f, t => Noise() * Env(t, 0.4f) * (1f - t / 0.4f) * 0.9f);
        _sounds["jump"] = Make(0.08f, t => Square(Sweep(300, 600, t, 0.08f), t) * Env(t, 0.08f) * 0.25f);
        _sounds["blink"] = Make(0.15f, t => Sine(Sweep(400, 2000, t, 0.15f), t) * Env(t, 0.15f) * 0.5f);
        _sounds["tick"] = Make(0.08f, t => Square(660, t) * Env(t, 0.08f) * 0.35f);
        _sounds["go"] = Make(0.25f, t => Square(t < 0.1f ? 880 : 1320, t) * Env(t, 0.25f) * 0.35f);
        _sounds["pick"] = Make(0.3f, t => Square(t < 0.1f ? 523 : t < 0.2f ? 659 : 784, t) * Env(t, 0.3f) * 0.3f);
        _sounds["win"] = Make(0.6f, t => Square(t < 0.15f ? 523 : t < 0.3f ? 659 : t < 0.45f ? 784 : 1046, t) * Env(t, 0.6f) * 0.3f);

        for (int i = 0; i < 12; i++)
        {
            var p = new AudioStreamPlayer { VolumeDb = -8 };
            AddChild(p);
            _voices.Add(p);
        }
    }

    /// <summary>Plays a sound with slight pitch variation. Rapid repeats of the same sound are throttled.</summary>
    public void Play(string name, float volume = 1f)
    {
        if (!_sounds.TryGetValue(name, out var stream)) return;
        ulong now = Time.GetTicksMsec();
        if (_lastPlayed.TryGetValue(name, out var last) && now - last < 30) return;
        _lastPlayed[name] = now;

        var v = _voices[_next];
        _next = (_next + 1) % _voices.Count;
        v.Stream = stream;
        v.PitchScale = 0.92f + (float)_rng.NextDouble() * 0.16f;
        v.VolumeDb = -8 + Mathf.LinearToDb(Mathf.Max(0.01f, volume));
        v.Play();
    }

    // ---------- synthesis ----------

    private float _phase;

    private AudioStreamWav Make(float seconds, Func<float, float> gen)
    {
        int n = (int)(seconds * Rate);
        var data = new byte[n * 2];
        _phase = 0;
        for (int i = 0; i < n; i++)
        {
            float s = Mathf.Clamp(gen(i / (float)Rate), -1, 1);
            short v = (short)(s * 28000);
            data[i * 2] = (byte)(v & 0xff);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = data };
    }

    private float Square(float freq, float t)
    {
        _phase += freq / Rate;
        return (_phase % 1f) < 0.5f ? 1f : -1f;
    }

    private float Sine(float freq, float t)
    {
        _phase += freq / Rate;
        return Mathf.Sin(_phase * Mathf.Tau);
    }

    private float Noise() => (float)_rng.NextDouble() * 2f - 1f;

    private static float Sweep(float from, float to, float t, float dur) => Mathf.Lerp(from, to, Mathf.Clamp(t / dur, 0, 1));

    private static float Env(float t, float dur)
    {
        float attack = Mathf.Min(1, t / 0.005f);
        float decay = 1f - Mathf.Clamp(t / dur, 0, 1);
        return attack * decay * decay;
    }
}
