using System;
using System.Collections.Generic;
using static Brawler.Logic.Synth.Dsp;

namespace Brawler.Logic.Synth;

/// <summary>
/// Every sound effect in the game, synthesized offline as mono 44.1 kHz buffers. Gunshots are layered the way real
/// recordings are: a broadband crack, a filtered noise body, a low thump and a dark room tail, then saturated. Each
/// sound gets several takes so rapid repeats don't sound identical.
/// </summary>
public static class SfxBank
{
    public static readonly string[] Names =
    {
        "shoot", "shoot_light", "shoot_heavy", "shoot_shotgun", "hit", "death", "explode", "block", "blocked",
        "bounce", "jump", "blink", "pad", "tick", "go", "pick", "win", "crate", "glass", "saw", "sizzle",
    };

    /// <summary>Renders every take of one sound.</summary>
    public static List<float[]> Render(string name)
    {
        int takes = name.StartsWith("shoot") || name is "hit" or "bounce" or "blocked" ? 4 : name is "tick" or "go" or "pick" or "win" ? 1 : 2;
        var list = new List<float[]>();
        for (int k = 0; k < takes; k++)
        {
            uint seed = 17;
            foreach (char ch in name) seed = seed * 31 + ch;
            seed += (uint)k * 7919;
            var buf = Make(name, seed);
            Normalize(buf, 0.92f * Level(name));
            list.Add(buf);
        }
        return list;
    }

    /// <summary>Relative loudness of each sound after normalizing, so weapons sit on top and UI blips stay polite.</summary>
    private static float Level(string name) => name switch
    {
        "shoot" or "shoot_shotgun" => 0.9f,
        "shoot_light" => 0.75f,
        "shoot_heavy" => 1f,
        "hit" => 0.85f,
        "death" or "explode" => 1f,
        "block" => 0.55f,
        "blocked" => 0.7f,
        "bounce" => 0.4f,
        "jump" => 0.35f,
        "blink" => 0.55f,
        "pad" => 0.55f,
        "tick" => 0.5f,
        "go" or "win" => 0.6f,
        "pick" => 0.45f,
        "crate" => 0.75f,
        "glass" => 0.6f,
        "saw" => 0.8f,
        "sizzle" => 0.4f,
        _ => 0.8f,
    };

    private static float[] Make(string name, uint seed) => name switch
    {
        "shoot" => Gun(seed, new GunVoice { Boom = 105, BoomTau = 0.035f, BodyTau = 0.028f, Bright = 7500, Crack = 1.1f, TailTau = 0.11f, TailGain = 0.32f, Length = 0.5f, Mech = 0.18f, MechAt = 0.06f }),
        "shoot_light" => Gun(seed, new GunVoice { Boom = 135, BoomTau = 0.02f, BodyTau = 0.016f, Bright = 9000, Crack = 1f, TailTau = 0.06f, TailGain = 0.2f, Length = 0.28f }),
        "shoot_heavy" => Gun(seed, new GunVoice { Boom = 62, BoomTau = 0.09f, BoomGain = 1.15f, BodyTau = 0.06f, Bright = 5200, Crack = 1.35f, TailTau = 0.38f, TailGain = 0.5f, Length = 1.2f, Mech = 0.28f, MechAt = 0.3f, MechTwice = true }),
        "shoot_shotgun" => Gun(seed, new GunVoice { Boom = 80, BoomTau = 0.06f, BoomGain = 1.05f, BodyTau = 0.05f, Bright = 5800, Crack = 1.05f, TailTau = 0.24f, TailGain = 0.48f, Length = 0.95f, Mech = 0.3f, MechAt = 0.38f, MechTwice = true }),
        "hit" => Hit(seed),
        "death" => Death(seed),
        "explode" => Explode(seed),
        "block" => Block(seed),
        "blocked" => Ricochet(seed, 2100, 0.45f, 1f),
        "bounce" => Ricochet(seed, 2700, 0.22f, 0.45f),
        "jump" => Jump(seed),
        "blink" => Blink(seed),
        "pad" => Pad(seed),
        "tick" => Pluck(new[] { 69f }, 0, 0.4f),
        "pick" => Pluck(new[] { 76f, 83f }, 0.07f, 0.5f),
        "go" => Stab(new[] { 40f, 47f, 52f, 59f }, 0, 0.9f),
        "win" => Stab(new[] { 52f, 56f, 59f, 64f, 68f }, 0.075f, 1.5f),
        "crate" => Crate(seed),
        "glass" => Glass(seed),
        "saw" => Saw(seed),
        "sizzle" => Sizzle(seed),
        _ => new float[1],
    };

    private static float[] Buf(float seconds) => new float[(int)(seconds * Rate)];

    // ---------- weapons ----------

    private struct GunVoice
    {
        public float Boom, BoomTau, BodyTau, Bright, Crack, TailTau, TailGain, Length, Mech, MechAt;
        public float BoomGain;
        public bool MechTwice;
    }

    private static float[] Gun(uint seed, GunVoice v)
    {
        var n = new Noise(seed);
        var o = Buf(v.Length);
        var crackHp = new Svf(3000, 0.6f);
        var body = new Svf(v.Bright, 0.8f);
        var tail = new Svf(1300, 0.55f);
        var mech = new Svf(3300 + n.Unit() * 600, 7f);
        var thump = new Osc();
        float boomGain = v.BoomGain > 0 ? v.BoomGain : 0.95f;
        float jitter = 0.94f + n.Unit() * 0.12f;
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate, w = n.Next();
            float crack = crackHp.HighPass(w) * Decay(t, 0.0022f) * v.Crack * 1.6f;
            if ((i & 15) == 0) body.Set(500 + v.Bright * jitter * Decay(t, 0.045f), 0.8f);
            float b = body.LowPass(w) * Perc(t, 0.0006f, v.BodyTau);
            float th = thump.Sine(v.Boom * jitter * (1 + 1.8f * Decay(t, 0.01f))) * Perc(t, 0.0008f, v.BoomTau) * boomGain;
            float tl = tail.LowPass(w) * Perc(t, 0.012f, v.TailTau) * v.TailGain;
            float m = mech.BandPass(w);
            float mc = 0;
            if (v.Mech > 0 && t > v.MechAt) mc += m * Decay(t - v.MechAt, 0.005f) * v.Mech * 3;
            if (v.MechTwice && t > v.MechAt + 0.09f) mc += m * Decay(t - v.MechAt - 0.09f, 0.006f) * v.Mech * 3.5f;
            o[i] = MathF.Tanh(1.9f * (crack + b + th + tl)) + mc;
        }
        Echo(o, 0.075f, 0.18f, 1600);
        return o;
    }

    /// <summary>Adds one dark reflection, like a nearby wall.</summary>
    private static void Echo(float[] o, float delay, float gain, float tone)
    {
        int d = (int)(delay * Rate);
        var lp = new Svf(tone, 0.6f);
        var src = (float[])o.Clone();
        for (int i = d; i < o.Length; i++) o[i] += lp.LowPass(src[i - d]) * gain;
    }

    // ---------- impacts ----------

    private static float[] Hit(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.28f);
        var slap = new Svf(1900, 0.7f);
        var mid = new Svf(320 + n.Unit() * 80, 2.2f);
        var snap = new Svf(4000, 0.6f);
        var thud = new Osc();
        float f = 88 + n.Unit() * 16;
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate, w = n.Next();
            float s = thud.Sine(f * (1 + 0.9f * Decay(t, 0.012f))) * Perc(t, 0.001f, 0.05f);
            s += slap.LowPass(w) * Perc(t, 0.0008f, 0.022f) * 0.9f;
            s += mid.BandPass(w) * Perc(t, 0.001f, 0.035f) * 1.6f;
            s += snap.HighPass(w) * Decay(t, 0.003f) * 0.7f;
            o[i] = MathF.Tanh(1.7f * s);
        }
        return o;
    }

    private static float[] Death(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(1.1f);
        var whoomp = new Svf(2500, 0.7f);
        var crackle = new Svf(2800, 1.5f);
        var thud = new Osc();
        var sub = new Osc();
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate, w = n.Next();
            if ((i & 15) == 0) whoomp.Set(160 + 3200 * Decay(t, 0.09f), 0.8f);
            float s = thud.Sine(95 * (1 + 1.2f * Decay(t, 0.015f))) * Perc(t, 0.001f, 0.07f);
            s += sub.Sine(48 * (1 + 0.6f * Decay(t, 0.05f))) * Perc(t, 0.004f, 0.22f) * 0.8f;
            s += whoomp.LowPass(w) * Perc(t, 0.002f, 0.2f) * 1.1f;
            float spark = n.Unit() < 0.006f * Decay(t, 0.25f) ? n.Next() * 6 : 0;
            s += crackle.BandPass(spark) * 0.8f;
            o[i] = MathF.Tanh(1.6f * s);
        }
        Echo(o, 0.11f, 0.22f, 900);
        return o;
    }

    private static float[] Explode(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(1.8f);
        var body = new Svf(4000, 0.7f);
        var crackle = new Svf(2200, 1.2f);
        var sub = new Osc();
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate, w = n.Next();
            if ((i & 15) == 0) body.Set(120 + 4200 * Decay(t, 0.07f), 0.7f);
            float s = body.LowPass(w) * Perc(t, 0.002f, 0.38f) * 1.4f;
            s += sub.Sine(52 * (1 + 0.8f * Decay(t, 0.04f))) * Perc(t, 0.003f, 0.32f);
            float spark = n.Unit() < 0.01f * Decay(t, 0.45f) ? n.Next() * 5 : 0;
            s += crackle.BandPass(spark) * 0.7f;
            o[i] = MathF.Tanh(1.8f * s);
        }
        Echo(o, 0.13f, 0.25f, 700);
        return o;
    }

    // ---------- defense ----------

    /// <summary>Shield up: an electric hum swept through a resonant filter.</summary>
    private static float[] Block(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.4f);
        var bp = new Svf(400, 3f);
        var air = new Svf(3000, 1f);
        Osc a = new(), b = new();
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate;
            if ((i & 15) == 0) bp.Set(350 + 2600 * (1 - Decay(t, 0.06f)), 3f);
            float hum = a.Saw(110) + b.Saw(110 * 1.498f) * 0.7f;
            float s = bp.BandPass(hum) * Perc(t, 0.012f, 0.11f) * 1.3f;
            s += air.BandPass(n.Next()) * Perc(t, 0.02f, 0.06f) * 0.5f;
            o[i] = MathF.Tanh(1.4f * s);
        }
        return o;
    }

    /// <summary>A bullet glancing off metal: inharmonic clang plus the falling whine of a ricochet.</summary>
    private static float[] Ricochet(uint seed, float f0, float seconds, float whine)
    {
        var n = new Noise(seed);
        var o = Buf(seconds);
        float[] ratio = { 1f, 2.32f, 4.25f, 6.63f }, tau = { 0.11f, 0.07f, 0.045f, 0.03f }, amp = { 0.55f, 0.4f, 0.3f, 0.2f };
        var p = new Osc[4];
        var snap = new Svf(5000, 0.6f);
        Osc wh = new();
        float f = f0 * (0.92f + n.Unit() * 0.16f);
        float top = 3400 + n.Unit() * 900, bottom = top * (0.45f + n.Unit() * 0.1f);
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate;
            float s = snap.HighPass(n.Next()) * Decay(t, 0.003f) * 1.2f;
            for (int k = 0; k < 4; k++) s += p[k].Sine(f * ratio[k]) * Decay(t, tau[k] * seconds / 0.45f) * amp[k];
            float wf = bottom + (top - bottom) * Decay(t, seconds * 0.4f);
            s += wh.Sine(wf * (1 + 0.01f * MathF.Sin(t * 70))) * Perc(t, 0.01f, seconds * 0.28f) * 0.45f * whine;
            o[i] = MathF.Tanh(1.3f * s);
        }
        return o;
    }

    // ---------- arena ----------

    /// <summary>A crate splintering: a hollow wooden knock, then a few sharp cracks of breaking boards.</summary>
    private static float[] Crate(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.5f);
        var knock = new Svf(260 + n.Unit() * 60, 3f);
        var crack = new Svf(1800, 1.4f);
        var thud = new Osc();
        float[] at = { 0, 0.018f + n.Unit() * 0.01f, 0.05f + n.Unit() * 0.02f, 0.1f + n.Unit() * 0.04f };
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate, w = n.Next();
            float s = knock.BandPass(w) * Perc(t, 0.001f, 0.06f) * 2.2f;
            s += thud.Sine(120 * (1 + 0.6f * Decay(t, 0.01f))) * Perc(t, 0.001f, 0.04f) * 0.7f;
            for (int k = 0; k < at.Length; k++)
                if (t > at[k]) s += crack.BandPass(w) * Decay(t - at[k], 0.012f) * (1.2f - k * 0.2f);
            o[i] = MathF.Tanh(1.6f * s);
        }
        Echo(o, 0.07f, 0.15f, 1400);
        return o;
    }

    /// <summary>Glass shattering: a bright crash and a scatter of high inharmonic tinkles.</summary>
    private static float[] Glass(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.7f);
        var crash = new Svf(6000, 0.7f);
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate;
            o[i] = crash.HighPass(n.Next()) * Perc(t, 0.0005f, 0.05f) * 1.2f;
        }
        // Falling shards: short pings at random pitches, thinning out.
        for (int k = 0; k < 14; k++)
        {
            float start = n.Unit() * n.Unit() * 0.45f, f = 2600 + n.Unit() * 4200, amp = 0.35f * (1 - start * 1.5f);
            var a = new Osc();
            var b = new Osc();
            for (int i = (int)(start * Rate); i < o.Length; i++)
            {
                float t = i / (float)Rate - start;
                if (t > 0.12f) break;
                o[i] += (a.Sine(f) + b.Sine(f * 2.76f) * 0.4f) * Perc(t, 0.0005f, 0.03f) * amp;
            }
        }
        for (int i = 0; i < o.Length; i++) o[i] = MathF.Tanh(1.3f * o[i]);
        Echo(o, 0.09f, 0.2f, 5000);
        return o;
    }

    /// <summary>A sawblade biting: a grinding buzz at the blade's tooth rate with metal screech on top.</summary>
    private static float[] Saw(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.42f);
        var grind = new Svf(1400, 2f);
        var screech = new Svf(3800 + n.Unit() * 600, 8f);
        Osc tooth = new(), sub = new();
        float rate = 165 + n.Unit() * 20;
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate, w = n.Next();
            float buzz = tooth.Saw(rate * (1 - 0.15f * t)) * (0.6f + 0.4f * MathF.Sin(t * 260));
            float s = grind.BandPass(buzz + w * 0.6f) * Perc(t, 0.004f, 0.16f) * 1.8f;
            s += screech.BandPass(w) * Perc(t, 0.01f, 0.12f) * 1.1f;
            s += sub.Sine(70) * Perc(t, 0.001f, 0.03f) * 0.6f;
            o[i] = MathF.Tanh(1.8f * s);
        }
        Echo(o, 0.06f, 0.15f, 2000);
        return o;
    }

    /// <summary>A short hiss with a few crackles: feet in magma or a poison tick.</summary>
    private static float[] Sizzle(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.32f);
        var hiss = new Svf(5200, 0.8f);
        var pop = new Svf(1500, 2f);
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate;
            float s = hiss.BandPass(n.Next()) * Perc(t, 0.012f, 0.1f);
            float spark = n.Unit() < 0.004f ? n.Next() * 5 : 0;
            s += pop.BandPass(spark) * Decay(t, 0.12f);
            o[i] = MathF.Tanh(1.5f * s);
        }
        return o;
    }

    // ---------- movement ----------

    private static float[] Jump(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.2f);
        var bp = new Svf(600, 1.2f);
        var thud = new Osc();
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate;
            if ((i & 15) == 0) bp.Set(500 + 1600 * (t / 0.2f), 1.2f);
            float s = bp.BandPass(n.Next()) * Perc(t, 0.018f, 0.045f) * 1.4f;
            s += thud.Sine(85) * Perc(t, 0.001f, 0.025f) * 0.6f;
            o[i] = s;
        }
        return o;
    }

    /// <summary>Teleport: a rising noise sweep that cuts off, with a bright FM zap on top.</summary>
    private static float[] Blink(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.42f);
        var bp = new Svf(300, 2f);
        Osc car = new(), mod = new();
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate;
            float u = Math.Clamp(t / 0.16f, 0, 1);
            if ((i & 15) == 0) bp.Set(250 + 5500 * u * u, 2f);
            float swell = t < 0.16f ? u * u : Decay(t - 0.16f, 0.05f);
            float s = bp.BandPass(n.Next()) * swell * 1.6f;
            float zf = 900 + 1800 * u;
            s += car.Sine(zf + mod.Sine(zf * 1.5f) * zf * 1.2f * swell) * swell * 0.35f;
            o[i] = MathF.Tanh(s);
        }
        Echo(o, 0.09f, 0.3f, 3000);
        return o;
    }

    /// <summary>Jump pad: a rising thump with a springy twang.</summary>
    private static float[] Pad(uint seed)
    {
        var n = new Noise(seed);
        var o = Buf(0.4f);
        var twang = new Svf(700, 6f);
        Osc thump = new(), spring = new();
        for (int i = 0; i < o.Length; i++)
        {
            float t = i / (float)Rate;
            float s = thump.Sine(70 + 220 * (1 - Decay(t, 0.06f))) * Perc(t, 0.002f, 0.08f);
            if ((i & 15) == 0) twang.Set(500 + 900 * (1 - Decay(t, 0.08f)), 6f);
            s += twang.BandPass(spring.Saw(150) + n.Next() * 0.3f) * Perc(t, 0.004f, 0.12f) * 0.7f;
            o[i] = MathF.Tanh(1.4f * s);
        }
        return o;
    }

    // ---------- interface (synthwave-styled, to match the music) ----------

    /// <summary>Filtered detuned-saw plucks, one per note, <paramref name="gap"/> seconds apart, with a dotted echo.</summary>
    private static float[] Pluck(float[] notes, float gap, float seconds)
    {
        var o = Buf(seconds);
        for (int k = 0; k < notes.Length; k++)
        {
            float hz = Hz(notes[k]);
            Osc a = new(), b = new();
            var lp = new Svf(3000, 1.4f);
            int start = (int)(k * gap * Rate);
            for (int i = start; i < o.Length; i++)
            {
                float t = (i - start) / (float)Rate;
                if ((i & 15) == 0) lp.Set(350 + 3800 * Decay(t, 0.05f), 1.4f);
                o[i] += lp.LowPass(a.Saw(hz * Cents(-7)) + b.Saw(hz * Cents(7))) * Perc(t, 0.002f, 0.09f);
            }
        }
        Echo(o, 0.12f, 0.35f, 2500);
        return o;
    }

    /// <summary>A brassy detuned-saw chord stab; with a gap the notes roll in like an arpeggio and then hold.</summary>
    private static float[] Stab(float[] notes, float gap, float seconds)
    {
        var n = new Noise(99);
        var o = Buf(seconds);
        float hold = seconds * 0.35f;
        for (int k = 0; k < notes.Length; k++)
        {
            float hz = Hz(notes[k]);
            Osc a = new() { Phase = n.Unit() }, b = new() { Phase = n.Unit() }, c = new() { Phase = n.Unit() };
            var lp = new Svf(3000, 1f);
            int start = (int)(k * gap * Rate);
            float len = hold + (notes.Length - 1 - k) * gap;
            for (int i = start; i < o.Length; i++)
            {
                float t = (i - start) / (float)Rate;
                if ((i & 15) == 0) lp.Set(700 + 4200 * Decay(t, 0.12f), 1f);
                float s = a.Saw(hz * Cents(-9)) + b.Saw(hz) + c.Saw(hz * Cents(9));
                o[i] += lp.LowPass(s) * Adsr(t, len, 0.006f, 0.15f, 0.6f, 0.18f) * 0.4f;
            }
        }
        for (int i = 0; i < o.Length; i++) o[i] = MathF.Tanh(o[i]);
        Echo(o, 0.15f, 0.3f, 2200);
        return o;
    }
}
