using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class AudioSynthesisTests
{
    // Pass --wav=DIR to also write every sound and song as .wav files for listening.
    public static void Run()
    {
        string wavDir = Args.FirstOrDefault(a => a.StartsWith("--wav="))?["--wav=".Length..];
        if (wavDir != null) System.IO.Directory.CreateDirectory(wavDir);
        static (float peak, float rms, bool finite) Measure(float[] b)
        {
            float p = 0; double sq = 0; bool ok = true;
            foreach (var v in b) { if (!float.IsFinite(v)) ok = false; p = Math.Max(p, Math.Abs(v)); sq += v * v; }
            return (p, (float)Math.Sqrt(sq / Math.Max(1, b.Length)), ok);
        }

        bool sfxOk = true;
        var detail = new List<string>();
        foreach (var name in Brawler.Logic.Synth.SfxBank.Names)
        {
            var takes = Brawler.Logic.Synth.SfxBank.Render(name);
            for (int k = 0; k < takes.Count; k++)
            {
                var (p, rms, finite) = Measure(takes[k]);
                if (!finite || p > 1f || rms < 0.005f) { sfxOk = false; detail.Add($"{name}#{k} peak {p:0.00} rms {rms:0.000}"); }
                if (wavDir != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(wavDir, $"sfx_{name}_{k}.wav"), Brawler.Logic.Synth.Dsp.ToWav(takes[k], 1));
            }
        }
        Check(sfxOk, "Every sound effect renders finite, unclipped and audible", string.Join("; ", detail));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var song in Brawler.Logic.Synth.MusicGen.All())
        {
            var buf = Brawler.Logic.Synth.MusicGen.Render(song);
            var (p, rms, finite) = Measure(buf);
            int expected = (int)MathF.Round(song.Bars * 16 * song.StepSeconds * Brawler.Logic.Synth.Dsp.Rate) * 2;
            // The seam: wrapping from the last frame to the first may be no sharper than the steepest step inside the
            // loop (the loop starts on a kick and crash, so a sharp step there is the downbeat, not a click).
            float seam = Math.Abs(buf[0] - buf[^2]) + Math.Abs(buf[1] - buf[^1]), steepest = 0;
            for (int i = 2; i < buf.Length - 1; i += 2) steepest = Math.Max(steepest, Math.Abs(buf[i] - buf[i - 2]) + Math.Abs(buf[i + 1] - buf[i - 1]));
            Check(finite && p <= 0.9f && rms > 0.05f && buf.Length == expected && seam <= steepest,
                $"Song '{song.Name}' renders a clean {song.Bars}-bar loop", $"{buf.Length / 2 / 44100f:0.0}s, peak {p:0.00}, rms {rms:0.000}, seam {seam:0.000} vs steepest {steepest:0.000}");
            if (wavDir != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(wavDir, $"music_{song.Name}.wav"), Brawler.Logic.Synth.Dsp.ToWav(buf, 2));
        }
        Console.WriteLine($"      (audio rendered in {sw.ElapsedMilliseconds} ms)");
    }
}
