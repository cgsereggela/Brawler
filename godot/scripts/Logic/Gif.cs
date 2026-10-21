using System;
using System.Collections.Generic;
using System.IO;

namespace Brawler.Logic;

/// <summary>
/// Minimal looping GIF encoder for the README clips. All frames share one 255-colour palette (median cut over the
/// whole clip, so colours don't flicker between frames); each frame after the first stores only the rectangle that
/// changed, with unchanged pixels left transparent.
/// </summary>
public static class Gif
{
    private const int Transparent = 255;

    /// <summary>Encodes RGB frames (3 bytes per pixel, row-major) shown <paramref name="delayCs"/> hundredths of a second each.</summary>
    public static byte[] Encode(IReadOnlyList<byte[]> frames, int w, int h, int delayCs)
    {
        var (palette, lut) = BuildPalette(frames);
        var ms = new MemoryStream();
        var o = new BinaryWriter(ms);
        o.Write("GIF89a"u8);
        o.Write((ushort)w);
        o.Write((ushort)h);
        o.Write((byte)0xF7); // global colour table, 256 entries
        o.Write((byte)0);
        o.Write((byte)0);
        o.Write(palette);
        o.Write([0x21, 0xFF, 0x0B]);
        o.Write("NETSCAPE2.0"u8);
        o.Write([0x03, 0x01, 0x00, 0x00, 0x00]); // loop forever

        var screen = new byte[w * h];
        var idx = new byte[w * h];
        for (int f = 0; f < frames.Count; f++)
        {
            var px = frames[f];
            for (int i = 0; i < idx.Length; i++) idx[i] = lut[Key(px[i * 3], px[i * 3 + 1], px[i * 3 + 2])];

            int x0 = 0, y0 = 0, x1 = w - 1, y1 = h - 1;
            if (f > 0)
            {
                (x0, y0, x1, y1) = (w, h, -1, -1);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (idx[y * w + x] != screen[y * w + x])
                        (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                if (x1 < 0) (x0, y0, x1, y1) = (0, 0, 0, 0); // nothing changed: one transparent pixel keeps the timing
            }
            int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
            var rect = new byte[rw * rh];
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = (y0 + y) * w + x0 + x;
                rect[y * rw + x] = f > 0 && idx[i] == screen[i] ? (byte)Transparent : idx[i];
                screen[i] = idx[i];
            }

            o.Write([0x21, 0xF9, 0x04, (1 << 2) | 1]); // keep the previous frame underneath, transparency on
            o.Write((ushort)delayCs);
            o.Write([Transparent, 0x00]);
            o.Write((byte)0x2C);
            o.Write((ushort)x0);
            o.Write((ushort)y0);
            o.Write((ushort)rw);
            o.Write((ushort)rh);
            o.Write((byte)0);
            o.Write((byte)8);
            var data = Lzw(rect);
            for (int i = 0; i < data.Length; i += 255)
            {
                int n = Math.Min(255, data.Length - i);
                o.Write((byte)n);
                o.Write(data, i, n);
            }
            o.Write((byte)0);
        }
        o.Write((byte)0x3B);
        return ms.ToArray();
    }

    private static int Key(int r, int g, int b) => (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;

    /// <summary>Median cut over a 15-bit colour histogram; returns the 768-byte palette and a 15-bit → index table.</summary>
    private static (byte[] Palette, byte[] Lut) BuildPalette(IReadOnlyList<byte[]> frames)
    {
        var count = new long[32768];
        var sum = new long[32768 * 3];
        foreach (var px in frames)
            for (int i = 0; i < px.Length; i += 3)
            {
                int k = Key(px[i], px[i + 1], px[i + 2]);
                count[k]++;
                sum[k * 3] += px[i];
                sum[k * 3 + 1] += px[i + 1];
                sum[k * 3 + 2] += px[i + 2];
            }
        var boxes = new List<List<int>> { new() };
        for (int k = 0; k < count.Length; k++) if (count[k] > 0) boxes[0].Add(k);

        int Channel(int k, int c) => (k >> (10 - c * 5)) & 31;
        while (boxes.Count < 255)
        {
            // Split the box with the most pixels times its widest channel range.
            int best = -1, bestCh = 0;
            double bestScore = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                long pixels = 0;
                foreach (int k in boxes[b]) pixels += count[k];
                for (int c = 0; c < 3; c++)
                {
                    int lo = 31, hi = 0;
                    foreach (int k in boxes[b]) { int v = Channel(k, c); lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                    double score = (double)pixels * (hi - lo);
                    if (score > bestScore) (best, bestCh, bestScore) = (b, c, score);
                }
            }
            if (best < 0) break;
            var box = boxes[best];
            box.Sort((p, q) => Channel(p, bestCh).CompareTo(Channel(q, bestCh)));
            long total = 0, run = 0;
            foreach (int k in box) total += count[k];
            int cut = 1;
            for (; cut < box.Count - 1; cut++)
            {
                run += count[box[cut - 1]];
                if (run * 2 >= total) break;
            }
            boxes[best] = box.GetRange(0, cut);
            boxes.Add(box.GetRange(cut, box.Count - cut));
        }

        var palette = new byte[768];
        var cols = new List<(int R, int G, int B)>();
        foreach (var box in boxes)
        {
            long n = 0, r = 0, g = 0, b = 0;
            foreach (int k in box) { n += count[k]; r += sum[k * 3]; g += sum[k * 3 + 1]; b += sum[k * 3 + 2]; }
            n = Math.Max(1, n);
            cols.Add(((int)(r / n), (int)(g / n), (int)(b / n)));
        }
        if (cols.Count == 0) cols.Add((0, 0, 0));
        for (int i = 0; i < cols.Count; i++) (palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]) = ((byte)cols[i].R, (byte)cols[i].G, (byte)cols[i].B);

        var lut = new byte[32768];
        for (int k = 0; k < lut.Length; k++)
        {
            int r = Channel(k, 0) * 8 + 4, g = Channel(k, 1) * 8 + 4, b = Channel(k, 2) * 8 + 4;
            int bestI = 0, bestD = int.MaxValue;
            for (int i = 0; i < cols.Count; i++)
            {
                int dr = r - cols[i].R, dg = g - cols[i].G, db = b - cols[i].B;
                int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                if (d < bestD) (bestI, bestD) = (i, d);
            }
            lut[k] = (byte)bestI;
        }
        return (palette, lut);
    }

    /// <summary>GIF LZW with 8-bit pixels: variable 9–12 bit codes, packed least significant bit first.</summary>
    private static byte[] Lzw(byte[] pixels)
    {
        const int clear = 256, end = 257;
        var o = new List<byte>();
        int bits = 0, nbits = 0;
        int size = 9, next = 258;
        void Put(int code)
        {
            bits |= code << nbits;
            nbits += size;
            while (nbits >= 8) { o.Add((byte)bits); bits >>= 8; nbits -= 8; }
        }
        var dict = new Dictionary<int, int>();
        Put(clear);
        int prefix = pixels[0];
        for (int i = 1; i < pixels.Length; i++)
        {
            int k = pixels[i], key = prefix << 8 | k;
            if (dict.TryGetValue(key, out int code)) { prefix = code; continue; }
            Put(prefix);
            if (next < 4096)
            {
                dict[key] = next++;
                if (next - 1 >= 1 << size && size < 12) size++;
            }
            else
            {
                Put(clear);
                dict.Clear();
                (size, next) = (9, 258);
            }
            prefix = k;
        }
        Put(prefix);
        Put(end);
        if (nbits > 0) o.Add((byte)bits);
        return o.ToArray();
    }
}
