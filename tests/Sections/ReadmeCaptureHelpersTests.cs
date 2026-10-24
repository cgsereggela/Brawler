using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class ReadmeCaptureHelpersTests
{
    public static void Run()
    {
        var names = FunNames.Pick(new Rng(3), 4);
        var words = names.SelectMany(n => n.Split(' ')).ToList();
        Check(names.Count == 4 && names.All(n => n.Length <= FunNames.MaxLength) && words.Distinct().Count() == words.Count,
            "Generated names fit the name box and don't repeat words", string.Join(", ", names));

        // Round trip: random pixels from 40 exact colours (so the palette is lossless), enough of them to fill the LZW table.
        int w = 200, h = 100;
        var rng = new Rng(11);
        var colours = Enumerable.Range(0, 40).Select(_ => new[] { (byte)(rng.Range(0, 31) * 8), (byte)(rng.Range(0, 31) * 8), (byte)(rng.Range(0, 31) * 8) }).ToList();
        var frames = new List<byte[]>();
        var cur = new byte[w * h * 3];
        for (int f = 0; f < 4; f++)
        {
            for (int i = 0; i < w * h; i++)
                if (f == 0 || (i % w > 50 && i % w < 120 && i / w > 20 && i / w < 60 && rng.Chance(0.5f)))
                    Array.Copy(colours[rng.Range(0, colours.Count - 1)], 0, cur, i * 3, 3);
            frames.Add((byte[])cur.Clone());
        }
        if (frames[1].SequenceEqual(frames[0])) throw new Exception("test frames didn't change");
        frames.Add((byte[])cur.Clone()); // an unchanged frame
        var gif = Gif.Encode(frames, w, h, 5);
        var decoded = DecodeGif(gif);
        Check(decoded.Count == frames.Count && decoded.Zip(frames).All(p => p.First.SequenceEqual(p.Second)),
            "GIF encoder round-trips frames exactly", $"{gif.Length} bytes, {decoded.Count} frames");
    }

    // Composited RGB frames of a GIF with a global palette (enough of the format to check our encoder).
    static List<byte[]> DecodeGif(byte[] g)
    {
        int p = 6, w = g[p] | g[p + 1] << 8, h = g[p + 2] | g[p + 3] << 8;
        p = 13;
        var pal = g[p..(p + 768)];
        p += 768;
        var screen = new byte[w * h * 3];
        var outFrames = new List<byte[]>();
        int trans = -1;
        while (g[p] != 0x3B)
        {
            if (g[p] == 0x21)
            {
                if (g[p + 1] == 0xF9) trans = (g[p + 3] & 1) != 0 ? g[p + 6] : -1;
                p += 2;
                while (g[p] != 0) p += g[p] + 1;
                p++;
                continue;
            }
            int x0 = g[p + 1] | g[p + 2] << 8, y0 = g[p + 3] | g[p + 4] << 8, rw = g[p + 5] | g[p + 6] << 8, rh = g[p + 7] | g[p + 8] << 8;
            p += 10;
            int min = g[p++];
            var data = new List<byte>();
            while (g[p] != 0) { data.AddRange(g[(p + 1)..(p + 1 + g[p])]); p += g[p] + 1; }
            p++;
            // LZW decode
            int clear = 1 << min, end = clear + 1, size = min + 1, next = end + 1, bitPos = 0;
            var table = new List<byte[]>();
            void Reset() { table.Clear(); for (int i = 0; i < clear; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); size = min + 1; next = end + 1; }
            Reset();
            var px = new List<byte>();
            byte[] prev = null;
            while (true)
            {
                int code = 0;
                for (int b = 0; b < size; b++, bitPos++) code |= ((data[bitPos >> 3] >> (bitPos & 7)) & 1) << b;
                if (code == clear) { Reset(); prev = null; continue; }
                if (code == end) break;
                byte[] entry = code < table.Count ? table[code] : [.. prev, prev[0]];
                px.AddRange(entry);
                if (prev != null && next < 4096)
                {
                    table.Add([.. prev, entry[0]]);
                    next++;
                    if (next >= 1 << size && size < 12) size++;
                }
                prev = entry;
            }
            for (int i = 0; i < rw * rh; i++)
            {
                if (px[i] == trans) continue;
                int s = ((y0 + i / rw) * w + x0 + i % rw) * 3;
                Array.Copy(pal, px[i] * 3, screen, s, 3);
            }
            outFrames.Add((byte[])screen.Clone());
        }
        return outFrames;
    }
}
