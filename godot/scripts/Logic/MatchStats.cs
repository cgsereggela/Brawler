using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Brawler.Logic;

/// <summary>One player's numbers for the match, as the match-over screen's Stats tab shows them.</summary>
/// <param name="Blocked">Hits taken on the shield.</param>
public readonly record struct MatchStatLine(int Slot, int Kills, float DamageDealt, float DamageTaken, int Shots, int Hits, int Blocked)
{
    /// <summary>Hits per shot, 0..1. A blast that catches two players counts twice, so it's capped at every shot hitting.</summary>
    public float Accuracy => Shots > 0 ? Math.Min(1f, (float)Hits / Shots) : 0;

    public string AccuracyText => Shots > 0 ? $"{MathF.Round(Accuracy * 100):0}%" : "-";
}

/// <summary>
/// The Stats tab's table: read off the host's <see cref="AwardBook"/> at the end of the match (it tallies the whole
/// match for the awards) and sent to everyone with the awards.
/// </summary>
public static class MatchStats
{
    public enum Column { Kills, DamageDealt, DamageTaken, Accuracy, Blocked }

    public static List<MatchStatLine> From(AwardBook b, IEnumerable<int> slots) => slots.Select(s => new MatchStatLine(s,
        (int)b.Get(s, AwardStat.Kills), b.Get(s, AwardStat.DamageDealt), b.Get(s, AwardStat.DamageTaken),
        (int)b.Get(s, AwardStat.Shots), (int)b.Get(s, AwardStat.Hits), (int)b.Get(s, AwardStat.HitsBlocked))).ToList();

    public static float Value(MatchStatLine l, Column c) => c switch
    {
        Column.Kills => l.Kills,
        Column.DamageDealt => l.DamageDealt,
        Column.DamageTaken => l.DamageTaken,
        Column.Accuracy => l.Accuracy,
        _ => l.Blocked,
    };

    /// <summary>
    /// The slot that leads a column (most kills, damage, accuracy, blocks; damage taken isn't something to lead), or -1
    /// when no one does: nobody scored, or two share the top.
    /// </summary>
    public static int Leader(IReadOnlyList<MatchStatLine> lines, Column c)
    {
        if (c == Column.DamageTaken || lines.Count < 2) return -1;
        var top = lines.OrderByDescending(l => Value(l, c)).ToList();
        float best = Value(top[0], c);
        return best > 0 && Value(top[1], c) < best ? top[0].Slot : -1;
    }

    public static void Write(BinaryWriter w, IReadOnlyList<MatchStatLine> lines)
    {
        w.Write((byte)lines.Count);
        foreach (var l in lines)
        {
            w.Write((byte)l.Slot);
            w.Write((byte)Math.Clamp(l.Kills, 0, 255));
            w.Write(U16(MathF.Round(l.DamageDealt)));
            w.Write(U16(MathF.Round(l.DamageTaken)));
            w.Write(U16(l.Shots));
            w.Write(U16(l.Hits));
            w.Write(U16(l.Blocked));
        }
    }

    public static List<MatchStatLine> Read(BinaryReader r)
    {
        int n = r.ReadByte();
        var lines = new List<MatchStatLine>(n);
        for (int i = 0; i < n; i++)
            lines.Add(new MatchStatLine(r.ReadByte(), r.ReadByte(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16()));
        return lines;
    }

    private static ushort U16(float v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);
}
