using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brawler.Logic;

/// <summary>
/// One finished (or aborted) match, written by the host as a single JSON line in the card stats log.
/// Everything a balance question could need is kept raw here; aggregation happens later in <see cref="StatsReport"/>.
/// Bump <see cref="CurrentVersion"/> when the shape changes so old logs can still be told apart.
/// </summary>
public sealed class MatchRecord
{
    public const int CurrentVersion = 1;

    public int V { get; set; } = CurrentVersion;
    public string Id { get; set; }
    public string StartedUtc { get; set; }
    /// <summary>"offline", "host" (LAN) or "sim" (headless bot simulation).</summary>
    public string Mode { get; set; }
    /// <summary>Hash of the card table so stats from different balance versions are not mixed by accident.</summary>
    public string CardsHash { get; set; }
    public bool Complete { get; set; }
    public int Winner { get; set; } = -1;
    public float DurationSec { get; set; }
    public SettingsRec Settings { get; set; } = new();
    public List<PlayerRec> Players { get; set; } = new();
    public List<BattleRec> Battles { get; set; } = new();
    public List<PickRec> Picks { get; set; } = new();
}

public sealed class SettingsRec
{
    public int BattlesPerRound { get; set; }
    public int RoundsToWin { get; set; }
    public int LoserChoices { get; set; }
    public int WinnerChoices { get; set; }
}

public sealed class PlayerRec
{
    public int Slot { get; set; }
    public string Name { get; set; }
    /// <summary>True for AI bots and for an autopilot-driven local player.</summary>
    public bool Bot { get; set; }
    public List<int> FinalCards { get; set; } = new();
    public int RoundWins { get; set; }
    public bool Left { get; set; }
}

public sealed class BattleRec
{
    public int N { get; set; }
    /// <summary>1-based round this battle belongs to.</summary>
    public int Round { get; set; }
    public int Seed { get; set; }
    /// <summary>Seconds from "FIGHT!" until the last player standing was decided.</summary>
    public float FightSec { get; set; }
    public int Winner { get; set; } = -1;
    public bool WonRound { get; set; }
    public List<BattlePlayerRec> Players { get; set; } = new();
    public List<KillRec> Kills { get; set; } = new();
}

public sealed class BattlePlayerRec
{
    public int Slot { get; set; }
    /// <summary>Cards held during this battle (duplicates = stacks).</summary>
    public List<int> Cards { get; set; } = new();
    public int Shots { get; set; }
    public int Hits { get; set; }
    public float DamageDealt { get; set; }
    public float DamageTaken { get; set; }
    public float SelfDamage { get; set; }
    public float Healed { get; set; }
    public int BlocksUsed { get; set; }
    public int HitsBlocked { get; set; }
    public int Kills { get; set; }
    /// <summary>Seconds into the fight when this player died, or -1 if they survived.</summary>
    public float DiedAt { get; set; } = -1;
    public float HpLeft { get; set; }
}

public sealed class KillRec
{
    public float T { get; set; }
    public int Victim { get; set; }
    /// <summary>Last player to damage the victim (may equal Victim for self-kills), or -1 for none.</summary>
    public int Killer { get; set; } = -1;
    /// <summary>"shot" or "fall".</summary>
    public string Cause { get; set; }
}

public sealed class PickRec
{
    public int Round { get; set; }
    public int Slot { get; set; }
    public bool Bot { get; set; }
    public bool RoundWinner { get; set; }
    /// <summary>True when the pick timer ran out and a card was chosen at random.</summary>
    public bool TimedOut { get; set; }
    public float DecisionSec { get; set; }
    public List<int> Offered { get; set; } = new();
    public int Picked { get; set; }
    public List<int> HeldBefore { get; set; } = new();
}

public static class StatsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string ToLine(MatchRecord m) => JsonSerializer.Serialize(m, Options);

    /// <summary>Parses a JSONL log; malformed or truncated lines are skipped and counted.</summary>
    public static List<MatchRecord> ParseLines(IEnumerable<string> lines, out int skipped)
    {
        var list = new List<MatchRecord>();
        skipped = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                var m = JsonSerializer.Deserialize<MatchRecord>(line, Options);
                if (m != null) list.Add(m);
                else skipped++;
            }
            catch (JsonException) { skipped++; }
        }
        return list;
    }

    /// <summary>Short stable hash of the rules that shape balance: every card's id, name and modifiers, the base stats and the body size.</summary>
    public static string CardsHash()
    {
        ulong h = 1469598103934665603UL; // FNV-1a
        void Mix(string s)
        {
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        }
        foreach (var c in CardRegistry.All)
        {
            Mix($"{c.Id}|{c.Name}|{c.EffectId}|");
            foreach (var m in c.Mods) Mix($"{m.Stat}{m.Op}{m.Value.ToString("R", CultureInfo.InvariantCulture)};");
        }
        var b = StatBlock.Base();
        foreach (Stat s in Enum.GetValues<Stat>()) Mix($"{s}={b[s].ToString("R", CultureInfo.InvariantCulture)};");
        Mix($"body={Body.W}x{Body.H}");
        return h.ToString("x16")[..8];
    }

}
