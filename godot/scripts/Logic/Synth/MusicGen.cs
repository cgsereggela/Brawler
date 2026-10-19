using System;
using System.Collections.Generic;
using static Brawler.Logic.Synth.Dsp;

namespace Brawler.Logic.Synth;

public enum Inst { Kick, Snare, HatClosed, HatOpen, Crash, Tom, Bass, Pad, Arp, Lead }

/// <summary>One note on a 16th-note grid. <see cref="Midi"/> is ignored by drums except toms.</summary>
public readonly record struct NoteEv(Inst Inst, float Step, float Len, float Midi, float Vel);

public sealed class Song
{
    public string Name;
    public float Bpm;
    public int Bars;
    public readonly List<NoteEv> Events = new();
    public float StepSeconds => 60f / Bpm / 4f;
}

/// <summary>
/// Original synthwave tracks in the dark, driving style of acts like Magic Sword: minor keys, detuned-saw pads,
/// a pumping octave bass, a delayed arpeggio, a brassy lead and big reverb snares. Songs are written as note events,
/// then rendered offline to a seamless stereo loop.
/// </summary>
public static class MusicGen
{
    // ---------- the songs ----------

    /// <summary>Menu and lobby: D minor, 100 BPM. Pads and arp alone, then drums and bass, then the lead. ~58 s loop.</summary>
    public static Song Lobby()
    {
        var s = new Song { Name = "lobby", Bpm = 100, Bars = 24 };
        string[] cycle = { "Dm", "Dm", "Bb", "Bb", "F", "F", "C", "C" };
        string[] last = { "Dm", "Dm", "Bb", "Bb", "F", "F", "C", "A" };
        for (int block = 0; block < 3; block++)
        {
            var chords = block == 2 ? last : cycle;
            int b0 = block * 8;
            // One pad chord per run of equal bars; in the intro the filter opens a little with each chord.
            for (int bar = 0; bar < 8; bar++)
            {
                if (bar > 0 && chords[bar] == chords[bar - 1]) continue;
                int span = bar + 1 < 8 && chords[bar + 1] == chords[bar] ? 2 : 1;
                Pad(s, b0 + bar, span, chords[bar], block == 0 ? 0.3f + bar * 0.05f : 0.7f);
            }
            for (int bar = 0; bar < 8; bar++)
            {
                int b = b0 + bar;
                Arp(s, b, chords[bar], new[] { 0, 2, 1, 3, 2, 4, 3, 1 }, block == 0 ? 0.35f + bar * 0.03f : 0.5f);
                if (block == 0) continue;
                // Bass: steady eighths, jumping up an octave at the end of each chord.
                int root = BassRoot(chords[bar]);
                for (int e = 0; e < 8; e++)
                {
                    bool up = bar % 2 == 1 && e >= 6;
                    s.Events.Add(new NoteEv(Inst.Bass, b * 16 + e * 2, 1.7f, root + (up ? 12 : 0), e % 2 == 0 ? 0.75f : 0.6f));
                }
                // Half-time drums.
                Hit(s, Inst.Kick, b, 0, 1f);
                Hit(s, Inst.Kick, b, 10, 0.8f);
                Hit(s, Inst.Snare, b, 8, 0.9f);
                for (int h = 0; h < 16; h += 2) Hit(s, Inst.HatClosed, b, h, h % 4 == 2 ? 0.55f : 0.3f);
                Hit(s, Inst.HatOpen, b, 14, 0.35f);
                if (bar == 0) Hit(s, Inst.Crash, b, 0, block == 1 ? 0.8f : 1f);
                if (bar == 7) for (int f = 12; f < 16; f++) Hit(s, Inst.Snare, b, f, 0.35f + (f - 12) * 0.15f);
            }
        }
        Melody(s, 16 * 16, Inst.Lead, 0.8f, 0,
            "A4/8 D5/8 | F5/12 E5/4 | D5/12 C5/4 | Bb4/8 D5/8 | C5/16 | A4/8 C5/8 | E5/12 G5/4 | E5/8 C#5/8");
        return s;
    }

    /// <summary>In a match: E minor, 126 BPM. Drive, heroic lead, half-time build, lead doubled in octaves. ~61 s loop.</summary>
    public static Song Battle()
    {
        var s = new Song { Name = "battle", Bpm = 126, Bars = 32 };
        string[] main = { "Em", "C", "G", "D", "Em", "C", "Am", "B" };
        string[] build = { "C", "D", "Em", "Em", "C", "D", "B", "B" };
        for (int block = 0; block < 4; block++)
        {
            bool half = block == 2;
            var chords = half ? build : main;
            int b0 = block * 8;
            for (int bar = 0; bar < 8; bar++)
            {
                int b = b0 + bar;
                string ch = chords[bar];
                Pad(s, b, 1, ch, half ? 0.8f : 0.6f);
                if (block != 1) Arp(s, b, ch, block == 3 ? new[] { 0, 1, 2, 4, 3, 2, 1, 2 } : new[] { 0, 1, 2, 3, 4, 3, 2, 1 }, block == 3 ? 0.4f : 0.55f);
                int root = BassRoot(ch);
                if (half)
                {
                    for (int e = 0; e < 8; e++) s.Events.Add(new NoteEv(Inst.Bass, b * 16 + e * 2, 1.8f, root, 0.7f));
                    for (int h = 0; h < 16; h += 4) Hit(s, Inst.HatClosed, b, h + 2, 0.45f);
                    if (bar < 6)
                    {
                        Hit(s, Inst.Kick, b, 0, 1f);
                        Hit(s, Inst.Kick, b, 10, 0.8f);
                        Hit(s, Inst.Snare, b, 8, 1f);
                    }
                    else
                    {
                        // The last two bars build: kicks and snares on every beat, then a snare roll.
                        for (int q = 0; q < 16; q += 4) Hit(s, Inst.Kick, b, q, 0.9f);
                        for (int q = 0; q < (bar == 7 ? 8 : 16); q += 4) Hit(s, Inst.Snare, b, q, 0.4f + (bar - 6) * 0.15f + q * 0.01f);
                        if (bar == 7) for (int f = 8; f < 16; f++) Hit(s, Inst.Snare, b, f, 0.4f + (f - 8) * 0.08f);
                    }
                }
                else
                {
                    // Galloping octave bass in sixteenths.
                    for (int q = 0; q < 16; q++)
                        s.Events.Add(new NoteEv(Inst.Bass, b * 16 + q, 0.8f, root + (q % 4 == 2 ? 12 : 0), q % 4 == 0 ? 0.9f : 0.6f));
                    for (int q = 0; q < 16; q += 4) Hit(s, Inst.Kick, b, q, 1f);
                    Hit(s, Inst.Snare, b, 4, 1f);
                    Hit(s, Inst.Snare, b, 12, 1f);
                    for (int h = 0; h < 16; h++) if (h % 4 != 2) Hit(s, Inst.HatClosed, b, h, h % 2 == 0 ? 0.4f : 0.22f);
                    for (int h = 2; h < 16; h += 4) Hit(s, Inst.HatOpen, b, h, 0.4f);
                    if (bar == 7)
                    {
                        Hit(s, Inst.Tom, b, 8, 0.9f, 52);
                        Hit(s, Inst.Tom, b, 10, 0.9f, 47);
                        for (int f = 12; f < 16; f++) Hit(s, Inst.Snare, b, f, 0.5f + (f - 12) * 0.15f);
                    }
                }
                if (bar == 0) Hit(s, Inst.Crash, b, 0, 1f);
            }
        }
        const string theme = "B4/4 E5/4 G5/4 F#5/4 | E5/8 G5/4 E5/4 | D5/8 B4/4 D5/4 | F#5/6 E5/2 D5/4 A4/4 | ";
        Melody(s, 8 * 16, Inst.Lead, 0.85f, 0, theme + "B4/4 E5/4 G5/4 B5/4 | C6/8 B5/4 G5/4 | A5/6 G5/2 E5/4 C5/4 | D#5/8 F#5/4 B5/4");
        Melody(s, 16 * 16, Inst.Lead, 0.55f, 0, "G5/16 | A5/16 | B5/24 G5/8 | E5/16 | F#5/16 | D#5/32");
        const string finale = theme + "B4/4 E5/4 G5/4 B5/4 | C6/6 B5/2 A5/4 G5/4 | A5/4 B5/4 C6/4 E6/4 | D#6/12 r/4";
        Melody(s, 24 * 16, Inst.Lead, 0.9f, 0, finale);
        Melody(s, 24 * 16, Inst.Lead, 0.5f, -12, finale);
        return s;
    }

    // ---------- writing helpers ----------

    private static readonly Dictionary<char, int> Pc = new() { ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5, ['G'] = 7, ['A'] = 9, ['B'] = 11 };

    /// <summary>"Em" → (pitch class 4, minor). Accepts sharps and flats.</summary>
    private static (int pc, bool minor) Chord(string sym)
    {
        int pc = Pc[sym[0]], i = 1;
        if (i < sym.Length && sym[i] == '#') { pc++; i++; }
        else if (i < sym.Length && sym[i] == 'b') { pc--; i++; }
        return ((pc + 12) % 12, i < sym.Length && sym[i] == 'm');
    }

    private static int BassRoot(string sym) => 36 + Chord(sym).pc; // C2..B2

    /// <summary>Chord tones from <paramref name="baseMidi"/> up: root, third, fifth, octave, tenth.</summary>
    private static int[] Tones(string sym, int baseMidi)
    {
        var (pc, minor) = Chord(sym);
        int r = baseMidi + pc, third = minor ? 3 : 4;
        return new[] { r, r + third, r + 7, r + 12, r + 12 + third };
    }

    private static void Pad(Song s, int bar, int bars, string sym, float vel)
    {
        var t = Tones(sym, 48);
        foreach (int n in new[] { t[0], t[1], t[2], t[3] })
            s.Events.Add(new NoteEv(Inst.Pad, bar * 16, bars * 16, n, vel));
    }

    private static void Arp(Song s, int bar, string sym, int[] pattern, float vel)
    {
        var t = Tones(sym, 60);
        for (int q = 0; q < 16; q++)
            s.Events.Add(new NoteEv(Inst.Arp, bar * 16 + q, 0.7f, t[pattern[q % pattern.Length]], vel * (q % 4 == 0 ? 1f : 0.75f)));
    }

    private static void Hit(Song s, Inst inst, int bar, int step, float vel, float midi = 0) =>
        s.Events.Add(new NoteEv(inst, bar * 16 + step, 1, midi, vel));

    /// <summary>Parses "E5/4 F#5/2 r/4 | ..." (durations in sixteenths, "r" rests, "|" is decoration).</summary>
    private static void Melody(Song s, int startStep, Inst inst, float vel, int transpose, string text)
    {
        int step = startStep;
        foreach (var tok in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok == "|") continue;
            var parts = tok.Split('/');
            int len = int.Parse(parts[1]);
            if (parts[0] != "r")
            {
                string n = parts[0];
                int pc = Pc[n[0]], i = 1;
                if (n[i] == '#') { pc++; i++; }
                else if (n[i] == 'b') { pc--; i++; }
                int midi = (int.Parse(n[i..]) + 1) * 12 + pc + transpose;
                s.Events.Add(new NoteEv(inst, step, len - 0.3f, midi, vel));
            }
            step += len;
        }
    }

    // ---------- rendering ----------

    private const float TailSeconds = 4f;

    /// <summary>
    /// Renders the song to interleaved stereo. The returned buffer is exactly one loop long: reverb and delay tails
    /// that run past the end are folded back onto the start, so it repeats without a seam.
    /// </summary>
    public static float[] Render(Song song, bool master = true)
    {
        float step = song.StepSeconds;
        int loop = (int)MathF.Round(song.Bars * 16 * step * Rate);
        int total = loop + (int)(TailSeconds * Rate);
        var mix = new Mix(total);

        // Sidechain: everything melodic ducks under the kick, the signature synthwave pump.
        foreach (var e in song.Events)
        {
            if (e.Inst != Inst.Kick) continue;
            int k0 = (int)(e.Step * step * Rate);
            for (int i = k0, end = Math.Min(total, k0 + (int)(0.4f * Rate)); i < end; i++)
                mix.Duck[i] = MathF.Max(mix.Duck[i], Perc((i - k0) / (float)Rate, 0.005f, 0.11f) * e.Vel);
        }

        uint seed = 1;
        foreach (var e in song.Events)
        {
            int start = (int)(e.Step * step * Rate);
            float len = e.Len * step;
            var n = new Noise(seed++ * 2654435761u);
            switch (e.Inst)
            {
                case Inst.Kick: Kick(mix, start, e.Vel, ref n); break;
                case Inst.Snare: Snare(mix, start, e.Vel, ref n); break;
                case Inst.HatClosed: Hat(mix, start, e.Vel, 0.03f, ref n); break;
                case Inst.HatOpen: Hat(mix, start, e.Vel, 0.18f, ref n); break;
                case Inst.Crash: Crash(mix, start, e.Vel, ref n); break;
                case Inst.Tom: Tom(mix, start, e.Vel, e.Midi, ref n); break;
                case Inst.Bass: Bass(mix, start, len, e.Midi, e.Vel); break;
                case Inst.Pad: PadVoice(mix, start, len, e.Midi, e.Vel, ref n); break;
                case Inst.Arp: ArpVoice(mix, start, len, e.Midi, e.Vel, (int)e.Step % 2 == 0 ? -0.35f : 0.35f); break;
                case Inst.Lead: LeadVoice(mix, start, len, e.Midi, e.Vel); break;
            }
        }

        // Effects returns.
        var delay = new PingPong(step * 3, 0.38f);
        var verb = new Reverb(0.86f, 0.3f);
        var hp = new Svf(220, 0.6f);
        for (int i = 0; i < total; i++)
        {
            var (dl, dr) = delay.Process(mix.Dly[i]);
            var (rl, rr) = verb.Process(hp.HighPass(mix.Rev[i] + (dl + dr) * 0.25f));
            mix.L[i] += dl * 0.55f + rl * 0.9f;
            mix.R[i] += dr * 0.55f + rr * 0.9f;
        }

        // Fold the tail onto the start, then master: gentle saturation and normalize.
        var o = new float[loop * 2];
        for (int i = 0; i < loop; i++)
        {
            float l = mix.L[i], r = mix.R[i];
            if (i + loop < total) { l += mix.L[i + loop]; r += mix.R[i + loop]; }
            o[i * 2] = l;
            o[i * 2 + 1] = r;
        }
        if (!master) return o;
        Normalize(o, 1.25f);
        for (int i = 0; i < o.Length; i++) o[i] = MathF.Tanh(o[i]);
        Normalize(o, 0.89f);
        return o;
    }

    private sealed class Mix
    {
        public readonly float[] L, R, Rev, Dly, Duck;
        public Mix(int n) { L = new float[n]; R = new float[n]; Rev = new float[n]; Dly = new float[n]; Duck = new float[n]; }

        public void Out(int i, float v, float pan, float rev, float dly)
        {
            if ((uint)i >= (uint)L.Length) return;
            L[i] += v * (pan <= 0 ? 1 : 1 - pan);
            R[i] += v * (pan >= 0 ? 1 : 1 + pan);
            Rev[i] += v * rev;
            Dly[i] += v * dly;
        }

        public float Ducked(int i, float depth) => (uint)i < (uint)Duck.Length ? 1 - depth * Duck[i] : 1;
    }

    private static int Frames(float seconds) => (int)(seconds * Rate);

    // ---------- drums ----------

    private static void Kick(Mix m, int s0, float vel, ref Noise n)
    {
        var osc = new Osc();
        var click = new Svf(3500, 0.7f);
        for (int i = 0; i < Frames(0.5f); i++)
        {
            float t = i / (float)Rate;
            float v = osc.Sine(46 + 130 * Decay(t, 0.026f)) * Perc(t, 0.001f, 0.2f);
            v += click.HighPass(n.Next()) * Decay(t, 0.0025f) * 0.5f;
            m.Out(s0 + i, MathF.Tanh(1.8f * v) * vel * 0.8f, 0, 0.02f, 0);
        }
    }

    /// <summary>The 80s snare: a short tone, bright noise, and a huge reverb send.</summary>
    private static void Snare(Mix m, int s0, float vel, ref Noise n)
    {
        Osc a = new(), b = new();
        var bp = new Svf(1900, 0.8f);
        var hp = new Svf(700, 0.6f);
        for (int i = 0; i < Frames(0.45f); i++)
        {
            float t = i / (float)Rate, w = n.Next();
            float v = a.Sine(185 * (1 + 0.35f * Decay(t, 0.01f))) * Decay(t, 0.05f) * 0.55f;
            v += b.Sine(330) * Decay(t, 0.035f) * 0.25f;
            v += (bp.BandPass(w) * 0.9f + hp.HighPass(w) * 0.5f) * Perc(t, 0.001f, 0.13f);
            m.Out(s0 + i, MathF.Tanh(1.5f * v) * vel * 0.85f, 0, 0.6f, 0);
        }
    }

    private static void Hat(Mix m, int s0, float vel, float tau, ref Noise n)
    {
        var hp = new Svf(7800, 0.8f);
        Osc[] metal = new Osc[3];
        for (int i = 0; i < Frames(tau * 6); i++)
        {
            float t = i / (float)Rate;
            float ring = metal[0].Pulse(5432) + metal[1].Pulse(7019) + metal[2].Pulse(8810);
            float v = hp.HighPass(n.Next() + ring * 0.25f) * Perc(t, 0.0005f, tau);
            m.Out(s0 + i, v * vel * 0.34f, 0.3f, 0.06f, 0);
        }
    }

    private static void Crash(Mix m, int s0, float vel, ref Noise n)
    {
        var hp = new Svf(4200, 0.7f);
        Osc[] metal = new Osc[4];
        for (int i = 0; i < Frames(2.2f); i++)
        {
            float t = i / (float)Rate;
            float ring = metal[0].Pulse(3311) + metal[1].Pulse(4703) + metal[2].Pulse(6029) + metal[3].Pulse(7487);
            float v = hp.HighPass(n.Next() + ring * 0.3f) * Perc(t, 0.002f, 0.55f);
            m.Out(s0 + i, v * vel * 0.3f, -0.25f, 0.3f, 0);
        }
    }

    private static void Tom(Mix m, int s0, float vel, float midi, ref Noise n)
    {
        var osc = new Osc();
        float f = Hz(midi);
        for (int i = 0; i < Frames(0.6f); i++)
        {
            float t = i / (float)Rate;
            float v = osc.Sine(f * (1 + 0.5f * Decay(t, 0.03f))) * Perc(t, 0.001f, 0.22f) + n.Next() * Decay(t, 0.01f) * 0.2f;
            m.Out(s0 + i, MathF.Tanh(1.4f * v) * vel * 0.75f, (midi - 50) * 0.08f, 0.45f, 0);
        }
    }

    // ---------- synths ----------

    private static void Bass(Mix m, int s0, float len, float midi, float vel)
    {
        float f = Hz(midi);
        Osc a = new(), b = new(), sub = new();
        var lp = new Svf(800, 1.3f);
        for (int i = 0; i < Frames(len + 0.06f); i++)
        {
            float t = i / (float)Rate;
            if ((i & 15) == 0) lp.Set(160 + 2400 * vel * Decay(t, 0.07f), 1.3f);
            float v = lp.LowPass(a.Saw(f * Cents(-6)) + b.Saw(f * Cents(6)) * 0.8f) + sub.Sine(f * 0.5f) * 0.6f;
            v *= Adsr(t, len, 0.003f, 0.2f, 0.75f, 0.025f);
            m.Out(s0 + i, v * vel * 0.42f * m.Ducked(s0 + i, 0.55f), 0, 0, 0);
        }
    }

    /// <summary>Three detuned saws spread across the stereo field, slowly breathing through a low-pass.</summary>
    private static void PadVoice(Mix m, int s0, float len, float midi, float vel, ref Noise n)
    {
        float f = Hz(midi);
        Osc a = new() { Phase = n.Unit() }, b = new() { Phase = n.Unit() }, c = new() { Phase = n.Unit() };
        var lpL = new Svf(1000, 0.9f);
        var lpR = new Svf(1000, 0.9f);
        for (int i = 0; i < Frames(len + 1.2f); i++)
        {
            int at = s0 + i;
            float t = i / (float)Rate;
            if ((i & 31) == 0)
            {
                float cut = 450 + 1700 * vel + 350 * MathF.Sin(at / (float)Rate * 0.9f);
                lpL.Set(cut, 0.9f);
                lpR.Set(cut * 1.05f, 0.9f);
            }
            float sa = a.Saw(f * Cents(-12)), sb = b.Saw(f), sc = c.Saw(f * Cents(12));
            float env = Adsr(t, len, 0.3f, 1.2f, 0.8f, 0.7f) * 0.085f * m.Ducked(at, 0.45f);
            float l = lpL.LowPass(sa + sb * 0.7f) * env, r = lpR.LowPass(sc + sb * 0.7f) * env;
            if ((uint)at >= (uint)m.L.Length) break;
            m.L[at] += l;
            m.R[at] += r;
            m.Rev[at] += (l + r) * 0.3f;
        }
    }

    private static void ArpVoice(Mix m, int s0, float len, float midi, float vel, float pan)
    {
        float f = Hz(midi);
        Osc a = new(), b = new();
        var lp = new Svf(2000, 1.6f);
        for (int i = 0; i < Frames(len + 0.08f); i++)
        {
            float t = i / (float)Rate;
            if ((i & 15) == 0) lp.Set(350 + 4200 * vel * Decay(t, 0.06f), 1.6f);
            float v = lp.LowPass(a.Pulse(f, 0.32f) + b.Saw(f * Cents(5)) * 0.5f) * Adsr(t, len, 0.002f, 0.09f, 0.35f, 0.04f);
            m.Out(s0 + i, v * vel * 0.26f * m.Ducked(s0 + i, 0.35f), pan, 0.12f, 0.4f);
        }
    }

    /// <summary>Brassy lead: detuned saws plus a sub-octave pulse, a little scoop into each note and delayed vibrato.</summary>
    private static void LeadVoice(Mix m, int s0, float len, float midi, float vel)
    {
        float f = Hz(midi);
        Osc a = new(), b = new(), c = new();
        var lp = new Svf(2000, 1.1f);
        for (int i = 0; i < Frames(len + 0.35f); i++)
        {
            float t = i / (float)Rate;
            float vib = 1 + 0.005f * MathF.Min(1, MathF.Max(0, t - 0.2f) / 0.3f) * MathF.Sin(t * Tau * 5.6f);
            float hz = f * Cents(-35 * Decay(t, 0.025f)) * vib;
            if ((i & 15) == 0) lp.Set((700 + 2600 * vel) * (0.55f + 0.45f * Decay(t, 0.3f)) + 600 * Decay(t, 0.04f), 1.1f);
            float v = lp.LowPass(a.Saw(hz * Cents(-8)) + b.Saw(hz * Cents(8)) + c.Pulse(hz * 0.5f, 0.4f) * 0.35f);
            v *= Adsr(t, len, 0.012f, 0.35f, 0.75f, 0.12f);
            m.Out(s0 + i, v * vel * 0.24f, 0, 0.32f, 0.3f);
        }
    }
}
