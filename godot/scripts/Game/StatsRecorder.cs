using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Host-only collector for card balance data. GameManager feeds it match events; when a match ends
/// (or is abandoned) the whole match is appended as one JSON line to the stats log.
/// Analyse the log with tools/CardStats (see README).
/// </summary>
public sealed class StatsRecorder
{
    public const string FileName = "card_stats.jsonl";

    /// <summary>Folder for the log. Defaults to user://stats; override with --statsdir=PATH.</summary>
    public string Dir = "user://stats";
    public bool Enabled = true;
    /// <summary>"offline", "host" or "sim" — written into each record.</summary>
    public string Mode = "offline";

    private MatchRecord _match;
    private BattleRec _battle;
    private readonly Dictionary<int, BattlePlayerRec> _bp = new();
    private readonly Dictionary<int, int> _lastHitBy = new();
    private readonly Dictionary<int, PickRec> _openPicks = new();
    private float _fightT, _matchT, _pickT;
    private bool _fighting;

    public string FilePath => $"{Dir.TrimEnd('/')}/{FileName}";

    public void MatchStart(MatchSettings s, IEnumerable<MatchPlayer> players, Func<MatchPlayer, bool> isBot)
    {
        if (!Enabled) return;
        _match = new MatchRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            StartedUtc = DateTime.UtcNow.ToString("o"),
            Mode = Mode,
            CardsHash = StatsJson.CardsHash(),
            Settings = new SettingsRec
            {
                BattlesPerRound = s.BattlesPerRound, RoundsToWin = s.RoundsToWin,
                LoserChoices = s.LoserChoices, WinnerChoices = s.WinnerChoices, RareChance = s.RareChance, CursedDeals = s.CursedDeals,
                TeamSize = s.TeamSize,
            },
        };
        foreach (var p in players.OrderBy(p => p.Slot))
            _match.Players.Add(new PlayerRec { Slot = p.Slot, Name = p.Info.Name, Bot = isBot(p) });
        _matchT = 0;
        _battle = null;
    }

    public void BattleStart(int n, int round, int seed, IEnumerable<MatchPlayer> active)
    {
        if (_match == null) return;
        _battle = new BattleRec { N = n, Round = round, Seed = seed };
        _bp.Clear();
        _lastHitBy.Clear();
        foreach (var p in active.OrderBy(p => p.Slot))
        {
            var r = new BattlePlayerRec { Slot = p.Slot, Cards = p.Cards.ToList() };
            _battle.Players.Add(r);
            _bp[p.Slot] = r;
        }
        _fightT = 0;
        _fighting = false;
    }

    public void FightBegan() => _fighting = _battle != null;

    /// <summary>Called every host physics tick.</summary>
    public void Tick(float dt, Phase phase)
    {
        if (_match == null) return;
        _matchT += dt;
        if (_fighting && phase == Phase.Fighting) _fightT += dt;
        if (phase == Phase.CardPick) _pickT += dt;
    }

    public void Shot(int slot)
    {
        if (_fighting && _bp.TryGetValue(slot, out var r)) r.Shots++;
    }

    public void BlockUsed(int slot)
    {
        if (_fighting && _bp.TryGetValue(slot, out var r)) r.BlocksUsed++;
    }

    public void HitBlocked(int victim)
    {
        if (_fighting && _bp.TryGetValue(victim, out var r)) r.HitsBlocked++;
    }

    /// <param name="effective">Damage actually removed (capped at the victim's remaining HP).</param>
    public void Damage(int attacker, int victim, float effective)
    {
        if (!_fighting) return;
        if (_bp.TryGetValue(victim, out var v))
        {
            if (attacker == victim) v.SelfDamage += effective;
            else v.DamageTaken += effective;
        }
        if (attacker >= 0 && attacker != victim && _bp.TryGetValue(attacker, out var a))
        {
            a.Hits++;
            a.DamageDealt += effective;
        }
        if (attacker >= 0) _lastHitBy[victim] = attacker;
    }

    public void Healed(int slot, float amount)
    {
        if (_fighting && _bp.TryGetValue(slot, out var r)) r.Healed += amount;
    }

    public void Death(int victim, bool fell)
    {
        if (!_fighting) return;
        int killer = _lastHitBy.TryGetValue(victim, out int k) ? k : -1;
        _battle.Kills.Add(new KillRec { T = Round2(_fightT), Victim = victim, Killer = killer, Cause = fell ? "fall" : "shot" });
        if (_bp.TryGetValue(victim, out var v)) v.DiedAt = Round2(_fightT);
        if (killer >= 0 && killer != victim && _bp.TryGetValue(killer, out var kr)) kr.Kills++;
    }

    public void BattleEnd(int winner, bool wonRound, IEnumerable<MatchPlayer> active)
    {
        if (_battle == null || _match == null) return;
        _battle.Winner = winner;
        _battle.WonRound = wonRound;
        _battle.FightSec = Round2(_fightT);
        foreach (var p in active)
            if (_bp.TryGetValue(p.Slot, out var r))
                r.HpLeft = p.Avatar != null && p.Avatar.Alive ? Mathf.Max(0, p.Avatar.Hp) : 0;
        foreach (var r in _battle.Players)
        {
            r.DamageDealt = Round2(r.DamageDealt);
            r.DamageTaken = Round2(r.DamageTaken);
            r.SelfDamage = Round2(r.SelfDamage);
            r.Healed = Round2(r.Healed);
            r.HpLeft = Round2(r.HpLeft);
        }
        _match.Battles.Add(_battle);
        _battle = null;
        _fighting = false;
    }

    public void PickOffered(int round, MatchPlayer p, bool isBot, bool roundWinner, List<int> offered, (int Rare, int Curse)? deal)
    {
        if (_match == null) return;
        _pickT = 0;
        _openPicks[p.Slot] = new PickRec
        {
            Round = round, Slot = p.Slot, Bot = isBot, RoundWinner = roundWinner,
            Offered = offered.ToList(), HeldBefore = p.Cards.ToList(),
            Deal = deal is { } d ? new List<int> { d.Rare, d.Curse } : new List<int>(),
        };
    }

    /// <param name="curse">The curse taken with a cursed deal, or -1.</param>
    public void Picked(int slot, int cardId, bool timedOut, int curse = -1)
    {
        if (_match == null || !_openPicks.Remove(slot, out var open)) return;
        open.Picked = cardId;
        open.TimedOut = timedOut;
        open.TookDeal = curse >= 0;
        open.DecisionSec = Round2(_pickT);
        _match.Picks.Add(open);
    }

    /// <summary>Writes the match. <paramref name="complete"/> is false for abandoned matches.</summary>
    public void MatchEnd(int winner, bool complete, IEnumerable<MatchPlayer> players)
    {
        if (_match == null) return;
        var m = _match;
        _match = null;
        _openPicks.Clear();
        if (_battle != null) _battle = null; // an unfinished battle has no result to learn from
        m.Complete = complete;
        m.Winner = winner;
        m.DurationSec = Round2(_matchT);
        foreach (var p in players)
        {
            var pr = m.Players.Find(x => x.Slot == p.Slot);
            if (pr == null) continue;
            pr.FinalCards = p.Cards.ToList();
            pr.RoundWins = p.RoundWins;
            pr.Left = p.Left;
        }
        if (m.Battles.Count == 0) return; // nothing worth keeping
        Append(m);
    }

    private void Append(MatchRecord m)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(Dir);
            var path = FilePath;
            using var f = FileAccess.FileExists(path)
                ? FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite)
                : FileAccess.Open(path, FileAccess.ModeFlags.Write);
            if (f == null)
            {
                Log.Info($"stats: could not open {path}: {FileAccess.GetOpenError()}");
                return;
            }
            f.SeekEnd();
            f.StoreLine(StatsJson.ToLine(m));
            Log.Info($"stats: match {m.Id} saved ({m.Battles.Count} battles, {m.Picks.Count} picks) → {ProjectSettings.GlobalizePath(path)}");
        }
        catch (Exception e)
        {
            Log.Info($"stats: write failed: {e.Message}");
        }
    }

    private static float Round2(float v) => MathF.Round(v * 100f) / 100f;
}
