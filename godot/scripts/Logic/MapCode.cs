using System;
using System.Collections.Generic;
using System.Text;

namespace Brawler.Logic;

/// <summary>Why a map code didn't open.</summary>
public enum MapCodeError { None, NotACode, Damaged, Newer }

/// <summary>
/// Map codes: a <see cref="MapDesign"/> packed into a short line of text to paste to friends. The design is
/// bit-packed (every coordinate takes 10 bits or fewer, in the coarsest of 1, 2, 4 or 8 px steps that is still exact for
/// its piece), ends in a checksum that catches a mangled paste, and is written in URL-safe base64. A typical
/// generated arena comes to about 200 characters, a simple hand-made one to well under 100. Pure, so the tests cover it.
/// </summary>
public static class MapCode
{
    /// <summary>2 added drifting platforms, hanging platforms and a fourth style bit; 3 added conveyor belts; 4 added portals. Older codes still open.</summary>
    public const int Version = 4;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static string Encode(MapDesign d) => Text(Pack(d));

    /// <summary>Reads a code (spaces and line breaks are ignored); null if it isn't a valid map code.</summary>
    public static MapDesign Decode(string code) => Decode(code, out _);

    /// <summary>Reads a code, and when it can't, says why (see <see cref="Problem"/>).</summary>
    public static MapDesign Decode(string code, out MapCodeError why)
    {
        if (Bytes(code) is not { } b) { why = MapCodeError.NotACode; return null; }
        return Unpack(b, out why);
    }

    /// <summary>What to tell a player about a code that didn't open.</summary>
    public static string Problem(MapCodeError why) => why switch
    {
        MapCodeError.Damaged => "That map code is damaged: part of it is missing or changed. Copy the whole code again.",
        MapCodeError.Newer => "That map was made in a newer version of Brawler. Update the game to open it.",
        _ => "That isn't a map code.",
    };


    /// <summary>The design as bytes (what a lobby sends to everyone); see <see cref="Unpack"/>.</summary>
    public static byte[] Pack(MapDesign d)
    {
        var w = new BitWriter();
        w.Bits(Version, 4);
        w.Bits((uint)d.Seed, 32);
        w.Bits((int)d.Style, 4);
        var name = Encoding.UTF8.GetBytes(Trim(d.Name));
        w.Bits(name.Length, 5);
        foreach (byte b in name) w.Bits(b, 8);

        w.Bits(Math.Min(d.Pits.Count, MapDesign.MaxPits), 4);
        for (int i = 0; i < Math.Min(d.Pits.Count, MapDesign.MaxPits); i++)
        {
            var (x1, x2) = d.Pits[i];
            w.Coords(x1, x2 - x1);
        }
        Count(w, d.Blocks.Count, MapDesign.MaxBlocks, 7);
        foreach (var r in Take(d.Blocks, MapDesign.MaxBlocks)) w.Coords(r.X, r.Y, r.W, r.H);
        Count(w, d.Hills.Count, MapDesign.MaxHills, 4);
        foreach (var h in Take(d.Hills, MapDesign.MaxHills))
        {
            int n = Math.Min(h.Count, 15);
            w.Bits(n, 4);
            var v = new int[n * 2];
            for (int i = 0; i < n; i++) (v[i * 2], v[i * 2 + 1]) = (h[i].x, h[i].h);
            w.Coords(v);
        }
        Count(w, d.Spawns.Count, MapDesign.MaxSpawns, 4);
        foreach (var s in Take(d.Spawns, MapDesign.MaxSpawns)) w.Coords(s.X, s.Y);
        Count(w, d.Pads.Count, MapDesign.MaxPads, 4);
        foreach (var p in Take(d.Pads, MapDesign.MaxPads)) w.Coords(p.X1, p.Y);

        // Glass comes in rows of 8 px panes: each row goes in as one piece.
        var props = new List<(PropKind kind, int x, int y, int run)>();
        foreach (var p in d.Props)
        {
            if (p.Kind == PropKind.Glass && props.Count > 0 && props[^1] is { kind: PropKind.Glass } last
                && last.y == p.Y && last.x + last.run * ArenaFeatures.GlassTile == p.X && last.run < 32)
                props[^1] = last with { run = last.run + 1 };
            else props.Add((p.Kind, p.X, p.Y, 1));
        }
        Count(w, props.Count, MapDesign.MaxProps, 6);
        foreach (var (kind, x, y, run) in Take(props, MapDesign.MaxProps))
        {
            w.Bits((int)kind, 2);
            w.Coords(x, y);
            if (kind == PropKind.Glass) w.Bits(run - 1, 5);
        }
        Count(w, d.Hazards.Count, MapDesign.MaxHazards, 4);
        foreach (var h in Take(d.Hazards, MapDesign.MaxHazards))
        {
            w.Bits((int)h.Kind, 1);
            w.Coords(h.X1, h.X2 - h.X1, h.Y);
        }
        Count(w, d.Saws.Count, MapDesign.MaxSaws, 4);
        foreach (var s in Take(d.Saws, MapDesign.MaxSaws))
        {
            w.Coords((int)MathF.Round(s.X1), (int)MathF.Round(s.Y1), (int)MathF.Round(s.X2), (int)MathF.Round(s.Y2));
            w.Bits(Phase(s.Phase), 4);
        }
        Count(w, d.Movers.Count, MapDesign.MaxMovers, 4);
        foreach (var m in Take(d.Movers, MapDesign.MaxMovers))
        {
            w.Coords(m.X1, m.X2 - m.X1, m.Y);
            w.Bits(Phase(m.Phase), 4);
        }
        // Drifting platforms snap to the tile grid, so each goes in as tiles: 7 bits for x, y and width, 2 for height.
        Count(w, d.Drifts.Count, MapDesign.MaxDrifts, 5);
        foreach (var dr in Take(d.Drifts, MapDesign.MaxDrifts)) Tiles(w, dr.X, dr.Y, dr.W, dr.H);
        Count(w, d.Hangers.Count, MapDesign.MaxHangers, 4);
        foreach (var h in Take(d.Hangers, MapDesign.MaxHangers)) Tiles(w, h.X, h.Y, h.W, h.H);
        // Belts: x and length in tiles (7 bits each), y to the pixel (block tops aren't all on the grid), which way they run.
        Count(w, d.Conveyors.Count, MapDesign.MaxConveyors, 4);
        foreach (var c in Take(d.Conveyors, MapDesign.MaxConveyors))
        {
            w.Bits(Math.Clamp((c.X + T / 2) / T, 0, 127), 7);
            w.Bits(Math.Clamp(c.Y, 0, 1023), 10);
            w.Bits(Math.Clamp((c.W + T / 2) / T, 1, 127), 7);
            w.Bits(c.Dir > 0 ? 1 : 0, 1);
        }
        // Portals: per pair, each ring's centre to the pixel and which way it faces.
        Count(w, d.Portals.Count, MapDesign.MaxPortals, 3);
        foreach (var p in Take(d.Portals, MapDesign.MaxPortals))
            foreach (var ring in new[] { p.A, p.B })
            {
                w.Bits(Math.Clamp(ring.X, 0, 1023), 10);
                w.Bits(Math.Clamp(ring.Y, 0, 1023), 10);
                w.Bits(ring.Dir & 3, 2);
            }
        var bytes = w.Done(1);
        bytes[^1] = Checksum(bytes, bytes.Length - 1);
        return bytes;
    }

    /// <summary>A design from <see cref="Pack"/>'s bytes, or null if they are damaged or from a newer version.</summary>
    public static MapDesign Unpack(byte[] bytes) => Unpack(bytes, out _);

    public static MapDesign Unpack(byte[] bytes, out MapCodeError why)
    {
        // The version is the first 4 bits, and every version ends in this checksum. A sound newer code says so; a
        // broken one of this version is a damaged paste; anything else isn't a map code at all.
        int version = bytes is { Length: > 0 } ? bytes[0] >> 4 : -1;
        bool known = version is >= 1 and <= Version;
        bool sound = bytes is { Length: >= 2 } && bytes[^1] == Checksum(bytes, bytes.Length - 1);
        why = known ? MapCodeError.Damaged : sound && version > Version ? MapCodeError.Newer : MapCodeError.NotACode;
        if (!sound || !known || bytes.Length < 6) return null;

        try
        {
            var r = new BitReader(bytes, bytes.Length - 1);
            r.Bits(4); // the version, checked above
            var d = new MapDesign { Seed = (int)r.UBits(32) };
            int style = r.Bits(version >= 2 ? 4 : 3);
            if (style >= Enum.GetValues<MapStyle>().Length) return null;
            d.Style = (MapStyle)style;
            var name = new byte[r.Bits(5)];
            for (int i = 0; i < name.Length; i++) name[i] = (byte)r.Bits(8);
            d.Name = Trim(Encoding.UTF8.GetString(name));

            for (int i = r.Bits(4); i > 0; i--)
            {
                var v = r.Coords(2);
                d.Pits.Add((v[0], v[0] + v[1]));
            }
            for (int i = r.Bits(7); i > 0; i--)
            {
                var v = r.Coords(4);
                d.Blocks.Add(new RectI(v[0], v[1], v[2], v[3]));
            }
            for (int i = r.Bits(4); i > 0; i--)
            {
                int n = r.Bits(4);
                var v = r.Coords(n * 2);
                var h = new List<(int x, int h)>();
                for (int k = 0; k < n; k++) h.Add((v[k * 2], v[k * 2 + 1]));
                d.Hills.Add(h);
            }
            for (int i = r.Bits(4); i > 0; i--)
            {
                var v = r.Coords(2);
                d.Spawns.Add(new SpawnPoint(v[0], v[1]));
            }
            for (int i = r.Bits(4); i > 0; i--)
            {
                var v = r.Coords(2);
                d.Pads.Add(new JumpPad(v[0], v[0] + MapGenerator.PadWidth, v[1]));
            }
            for (int i = r.Bits(6); i > 0; i--)
            {
                var kind = (PropKind)r.Bits(2);
                var v = r.Coords(2);
                switch (kind)
                {
                    case PropKind.Crate: d.Props.Add(new PropSpawn(kind, v[0], v[1], ArenaFeatures.CrateSize, ArenaFeatures.CrateSize)); break;
                    case PropKind.Barrel: d.Props.Add(new PropSpawn(kind, v[0], v[1], ArenaFeatures.BarrelW, ArenaFeatures.BarrelH)); break;
                    case PropKind.Glass:
                        int run = r.Bits(5) + 1;
                        for (int k = 0; k < run; k++)
                            d.Props.Add(new PropSpawn(kind, v[0] + k * ArenaFeatures.GlassTile, v[1], ArenaFeatures.GlassTile, ArenaFeatures.GlassTile));
                        break;
                    default: return null;
                }
            }
            for (int i = r.Bits(4); i > 0; i--)
            {
                var kind = (HazardKind)r.Bits(1);
                var v = r.Coords(3);
                d.Hazards.Add(new Hazard(kind, v[0], v[0] + v[1], v[2]));
            }
            for (int i = r.Bits(4); i > 0; i--)
            {
                var v = r.Coords(4);
                float phase = r.Bits(4) / 16f;
                d.Saws.Add(new SawTrack(v[0], v[1], v[2], v[3], ArenaFeatures.SawRadius, MapDesign.SawPeriod(v[0], v[1], v[2], v[3]), phase));
            }
            for (int i = r.Bits(4); i > 0; i--)
            {
                var v = r.Coords(3);
                float phase = r.Bits(4) / 16f;
                d.Movers.Add(new Mover(v[0], v[0] + v[1], v[2], ArenaFeatures.MoverW, ArenaFeatures.MoverH, MapDesign.MoverPeriod(v[0], v[0] + v[1]), phase));
            }
            if (version >= 2)
                for (int i = r.Bits(5); i > 0; i--)
                {
                    var t = ReadTiles(r);
                    d.Drifts.Add(new Drift(t.x, t.y, t.w, t.h));
                }
            if (version >= 2)
                for (int i = r.Bits(4); i > 0; i--)
                {
                    var t = ReadTiles(r);
                    d.Hangers.Add(new Hanger(t.x, t.y, t.w, t.h));
                }
            if (version >= 3)
                for (int i = r.Bits(4); i > 0; i--)
                {
                    int x = r.Bits(7) * T, y = r.Bits(10), w = Math.Max(1, r.Bits(7)) * T;
                    d.Conveyors.Add(new Conveyor(x, y, w, r.Bits(1) == 1 ? 1 : -1));
                }
            if (version >= 4)
                for (int i = r.Bits(3); i > 0; i--)
                {
                    PortalRing Ring() => new(r.Bits(10), r.Bits(10), r.Bits(2));
                    var a = Ring();
                    d.Portals.Add(new PortalPair(a, Ring()));
                }
            why = MapCodeError.None;
            return d;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A name as codes carry it: trimmed, at most <see cref="MapDesign.MaxName"/> characters and 31 bytes.</summary>
    public static string Trim(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length > MapDesign.MaxName) name = name[..MapDesign.MaxName];
        while (Encoding.UTF8.GetByteCount(name) > 31) name = name[..^1];
        return name;
    }

    private const int T = MapGenerator.Tile;

    /// <summary>A tile-aligned rect (rounded to the grid): x, y and width in 7 bits of tiles, height 1 to 4 tiles in 2.</summary>
    private static void Tiles(BitWriter w, int x, int y, int width, int height)
    {
        w.Bits(Math.Clamp((x + T / 2) / T, 0, 127), 7);
        w.Bits(Math.Clamp((y + T / 2) / T, 0, 127), 7);
        w.Bits(Math.Clamp((width + T / 2) / T, 1, 127), 7);
        w.Bits(Math.Clamp((height + T / 2) / T, 1, 4) - 1, 2);
    }

    private static (int x, int y, int w, int h) ReadTiles(BitReader r) => (r.Bits(7) * T, r.Bits(7) * T, Math.Max(1, r.Bits(7)) * T, (r.Bits(2) + 1) * T);

    private static int Phase(float phase) => (int)MathF.Round((phase - MathF.Floor(phase)) * 16) & 15;

    private static void Count(BitWriter w, int n, int max, int bits) => w.Bits(Math.Min(n, max), bits);

    private static IEnumerable<T> Take<T>(List<T> list, int max)
    {
        for (int i = 0; i < Math.Min(list.Count, max); i++) yield return list[i];
    }

    internal static byte Checksum(byte[] data, int n)
    {
        int sum = 0x6D;
        for (int i = 0; i < n; i++) sum = (sum * 31 + data[i]) & 0xFF;
        return (byte)sum;
    }

    internal static string Text(byte[] bytes)
    {
        var sb = new StringBuilder();
        int acc = 0, bits = 0;
        foreach (byte b in bytes)
        {
            acc = acc << 8 | b;
            bits += 8;
            while (bits >= 6)
            {
                bits -= 6;
                sb.Append(Alphabet[acc >> bits & 63]);
            }
        }
        if (bits > 0) sb.Append(Alphabet[acc << (6 - bits) & 63]);
        return sb.ToString();
    }

    private static byte[] Bytes(string text)
    {
        var list = new List<byte>();
        int acc = 0, bits = 0;
        foreach (char c in text ?? "")
        {
            if (char.IsWhiteSpace(c)) continue;
            // Standard base64 pasted from elsewhere reads the same.
            int v = c switch { '+' => 62, '/' => 63, '=' => -2, _ => Alphabet.IndexOf(c) };
            if (v == -2) break;
            if (v < 0) return null;
            acc = (acc << 6 | v) & 0xFFFFFF;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                list.Add((byte)(acc >> bits));
            }
        }
        return list.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bit;

        public void Bits(int v, int n) => Bits((uint)v, n);

        public void Bits(uint v, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                if (_bit == 0) _bytes.Add(0);
                if ((v >> i & 1) != 0) _bytes[^1] |= (byte)(0x80 >> _bit);
                _bit = (_bit + 1) & 7;
            }
        }

        /// <summary>
        /// Non-negative coordinates below 1024 (clamped), in the coarsest step of 1, 2, 4 or 8 px they all fall on:
        /// two bits for the step, then each value in 10 bits less one per halving.
        /// </summary>
        public void Coords(params int[] v)
        {
            int shift = 3;
            foreach (int x in v)
                while (shift > 0 && (Math.Clamp(x, 0, 1023) & ((1 << shift) - 1)) != 0) shift--;
            Bits(shift, 2);
            foreach (int x in v) Bits(Math.Clamp(x, 0, 1023) >> shift, 10 - shift);
        }

        /// <summary>The bytes, with <paramref name="extra"/> zero bytes on the end.</summary>
        public byte[] Done(int extra)
        {
            for (int i = 0; i < extra; i++) _bytes.Add(0);
            return _bytes.ToArray();
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _length;
        private int _pos;

        public BitReader(byte[] data, int length) => (_data, _length) = (data, length);

        public uint UBits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++, _pos++)
            {
                if (_pos >> 3 >= _length) throw new IndexOutOfRangeException();
                v = v << 1 | (uint)(_data[_pos >> 3] >> (7 - (_pos & 7)) & 1);
            }
            return v;
        }

        public int Bits(int n) => (int)UBits(n);

        public int[] Coords(int n)
        {
            int shift = Bits(2);
            var v = new int[n];
            for (int i = 0; i < n; i++) v[i] = Bits(10 - shift) << shift;
            return v;
        }
    }
}
