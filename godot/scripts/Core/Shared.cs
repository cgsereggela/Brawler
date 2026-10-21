using System.Collections.Generic;
using System.IO;
using Brawler.Logic;
using Godot;

namespace Brawler.Core;

public static class Palette
{
    public static readonly Color[] Players =
    {
        new("#ff4d5a"), // red
        new("#3d9bff"), // blue
        new("#4ee06e"), // green
        new("#ffd23d"), // yellow
        new("#b46cff"), // purple
        new("#ff9233"), // orange
        new("#3de6e0"), // cyan
        new("#ff7ad9"), // pink
    };

    public static readonly string[] Names = { "Red", "Blue", "Green", "Yellow", "Purple", "Orange", "Cyan", "Pink" };

    public static Color Player(int idx) => Players[((idx % Players.Length) + Players.Length) % Players.Length];

    public static readonly Color Text = new("#ece6d8");
    public static readonly Color TextDim = new("#9c978c");
    public static readonly Color Accent = new("#e6cf8f");  // numbers, ammo, highlights
    public static readonly Color Hot = new("#d8c9a4");     // primary actions: pale parchment, like a lit mask
    public static readonly Color Cool = new("#a8cbe0");    // hover and focus: a cold, pale light
    public static readonly Color Outline = new("#15151f");

    private static readonly Color[] DyeColors = System.Array.ConvertAll(Appearance.Dyes, d => new Color(d.Hex));
    public static Color Dye(int idx) => DyeColors[((idx % DyeColors.Length) + DyeColors.Length) % DyeColors.Length];
}

public sealed class PlayerInfo
{
    public int Slot;
    public long PeerId;
    public string Name = "Player";
    public int ColorIdx;
    public bool IsBot;
    public Appearance Look = Appearance.Default();
    public string Key; // host only, never sent: the player's rejoin key (see NetworkManager.RejoinKey)
}

public sealed class MatchSettings
{
    public int BattlesPerRound = 2;  // battle wins needed to take a round
    public int RoundsToWin = 5;      // round wins needed to take the match
    public int LoserChoices = 5;     // cards offered to round losers
    public int WinnerChoices = 3;    // cards offered to the round winner (0 = winner doesn't pick)
    public HashSet<int> DisabledCards = new(); // card ids the host took out of the pool (see CardPool)
}

public sealed class LobbyState
{
    public const int MaxPlayers = 8;
    public readonly List<PlayerInfo> Players = new();
    public MatchSettings Settings = new();

    public PlayerInfo BySlot(int slot) => Players.Find(p => p.Slot == slot);
    public PlayerInfo ByPeer(long peer) => Players.Find(p => p.PeerId == peer && !p.IsBot);

    public int FreeSlot()
    {
        for (int s = 0; s < MaxPlayers; s++)
            if (BySlot(s) == null) return s;
        return -1;
    }

    public int FreeColor()
    {
        for (int c = 0; c < Palette.Players.Length; c++)
            if (!Players.Exists(p => p.ColorIdx == c)) return c;
        return 0;
    }

    public byte[] Serialize()
    {
        var w = new Wire();
        w.U8(Settings.BattlesPerRound).U8(Settings.RoundsToWin).U8(Settings.LoserChoices).U8(Settings.WinnerChoices);
        w.Blob(CardPool.Encode(Settings.DisabledCards));
        w.U8(Players.Count);
        foreach (var p in Players)
            {
            w.U8(p.Slot).I64(p.PeerId).Str(p.Name).U8(p.ColorIdx).Bool(p.IsBot);
            w.Bytes(p.Look.ToBytes());
        }
        return w.Done();
    }

    public static LobbyState Deserialize(byte[] data)
    {
        var r = new WireReader(data);
        var s = new LobbyState();
        s.Settings.BattlesPerRound = r.U8();
        s.Settings.RoundsToWin = r.U8();
        s.Settings.LoserChoices = r.U8();
        s.Settings.WinnerChoices = r.U8();
        s.Settings.DisabledCards = CardPool.Decode(r.Blob());
        int n = r.U8();
        for (int i = 0; i < n; i++)
            s.Players.Add(new PlayerInfo { Slot = r.U8(), PeerId = r.I64(), Name = r.Str(), ColorIdx = r.U8(), IsBot = r.Bool(), Look = Appearance.FromBytes(r.Bytes()) });
        return s;
    }
}

/// <summary>One physics tick of player intent. Presses are counters so a dropped packet can't eat a tap.</summary>
public struct InputFrame
{
    public float MoveX;
    public bool JumpHeld;
    public byte JumpPresses;
    public bool Shoot;
    public byte BlockPresses;
    public Vector2 Aim; // world-space point

    public byte[] ToBytes()
    {
        var w = new Wire();
        w.I8((int)Mathf.Round(Mathf.Clamp(MoveX, -1, 1) * 127));
        w.U8((JumpHeld ? 1 : 0) | (Shoot ? 2 : 0));
        w.U8(JumpPresses).U8(BlockPresses);
        w.I16((int)Aim.X).I16((int)Aim.Y);
        return w.Done();
    }

    public static InputFrame FromBytes(byte[] b)
    {
        var r = new WireReader(b);
        var f = new InputFrame { MoveX = r.I8() / 127f };
        int flags = r.U8();
        f.JumpHeld = (flags & 1) != 0;
        f.Shoot = (flags & 2) != 0;
        f.JumpPresses = (byte)r.U8();
        f.BlockPresses = (byte)r.U8();
        f.Aim = new Vector2(r.I16(), r.I16());
        return f;
    }
}

/// <summary>Compact binary writer for RPC payloads.</summary>
public sealed class Wire
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _w;
    public Wire() => _w = new BinaryWriter(_ms);
    public Wire U8(int v) { _w.Write((byte)v); return this; }
    public Wire I8(int v) { _w.Write((sbyte)v); return this; }
    public Wire I16(int v) { _w.Write((short)Mathf.Clamp(v, short.MinValue, short.MaxValue)); return this; }
    public Wire I32(int v) { _w.Write(v); return this; }
    public Wire I64(long v) { _w.Write(v); return this; }
    public Wire F(float v) { _w.Write(v); return this; }
    public Wire V2(Vector2 v) { _w.Write(v.X); _w.Write(v.Y); return this; }
    public Wire Bool(bool v) { _w.Write(v); return this; }
    public Wire Str(string v) { _w.Write(v ?? ""); return this; }
    public Wire Bytes(byte[] v) { _w.Write((byte)v.Length); _w.Write(v, 0, System.Math.Min(v.Length, 255)); return this; }
    /// <summary>Like <see cref="Bytes"/> with a 16-bit length, for payloads that can pass 255 bytes.</summary>
    public Wire Blob(byte[] v) { int n = System.Math.Min(v.Length, ushort.MaxValue); _w.Write((ushort)n); _w.Write(v, 0, n); return this; }
    public byte[] Done() { _w.Flush(); return _ms.ToArray(); }
}

public sealed class WireReader
{
    private readonly BinaryReader _r;
    public WireReader(byte[] data) => _r = new BinaryReader(new MemoryStream(data));
    public bool More => _r.BaseStream.Position < _r.BaseStream.Length;
    public int U8() => _r.ReadByte();
    public int I8() => _r.ReadSByte();
    public int I16() => _r.ReadInt16();
    public int I32() => _r.ReadInt32();
    public long I64() => _r.ReadInt64();
    public float F() => _r.ReadSingle();
    public Vector2 V2() => new(_r.ReadSingle(), _r.ReadSingle());
    public bool Bool() => _r.ReadBoolean();
    public string Str() => _r.ReadString();
    public byte[] Bytes() => _r.ReadBytes(_r.ReadByte());
    public byte[] Blob() => _r.ReadBytes(_r.ReadUInt16());
}

public static class Log
{
    public static void Info(string msg) => GD.Print($"[Brawler] {msg}");
}
