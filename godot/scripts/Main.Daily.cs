using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler;

/// <summary>
/// The daily challenge (Daily on the main menu): today's arena, mode and starting hand against three Hard bots, straight
/// from the menu with no lobby. The best result of the day goes in the profile; see <see cref="Daily"/>.
/// </summary>
public partial class Main
{
    private Daily _daily; // the challenge being played, or null

    private void StartDaily() => StartDaily(Daily.For(DateTime.UtcNow));

    private void StartDaily(Daily d)
    {
        if (_autoMode == null) SaveProfile();
        _net.StartOffline();
        // The same rules for everyone: the default match settings, every built-in card (custom cards stay out of the offers).
        var s = new MatchSettings
        {
            Mode = d.Mode,
            Maps = new() { MapCode.Pack(d.Arena()) },
            DisabledCards = CardRegistry.Custom.Select(c => c.Id).ToHashSet(),
        };
        _net.SetSettings(s);
        var skill = _net.NewBotSkill;
        _net.NewBotSkill = BotSkill.Hard;
        for (int i = 0; i < Daily.Bots; i++) _net.AddBot();
        _net.NewBotSkill = skill;
        _daily = d;
        _game.StartingCards = d.StartingCards;
        _game.ReseedHost(d.Seed); // the same card offers as everyone else, pick for pick
        Log.Info($"daily {d.DateText}: {GameModes.Name(d.Mode)}, starting with {string.Join(", ", d.StartingCards.Select(id => CardRegistry.Get(id).Name))}");
        _game.HostStartMatch();
    }

    /// <summary>The match ended: keep the result if it is the day's best.</summary>
    private void DailyMatchEnded(int winner)
    {
        var me = _game.LocalMatchPlayer;
        if (_daily == null || me == null) return;
        var run = new DailyResult(winner >= 0 && winner == me.Slot, me.RoundWins, CareerBook.MatchKills);
        var before = DailyBest.Decode(Profile.LoadDailyBest(), _daily.Day);
        Profile.SaveDailyBest(DailyBest.Merge(Profile.LoadDailyBest(), _daily.Day, run));
        bool best = before is not { } b || run.CompareTo(b) > 0;
        Log.Info($"daily {_daily.DateText}: {run}{(best ? " (new best today)" : $" (best today: {before})")}");
        _menu.SetDaily(DailyLine());
    }

    private void DailyEnded()
    {
        _daily = null;
        _game.StartingCards = null;
    }

    /// <summary>The menu's line under the Daily button: today's mode and your best so far.</summary>
    private static string DailyLine()
    {
        var d = Daily.For(DateTime.UtcNow);
        var best = DailyBest.Decode(Profile.LoadDailyBest(), d.Day);
        return $"{GameModes.Name(d.Mode)} · {string.Join(", ", d.StartingCards.Select(id => CardRegistry.Get(id).Name))}\n"
             + (best is { } b ? $"today's best: {b}" : "not played today");
    }

    // ---------- --dailycheck: autopilot plays a day's challenge ----------

    /// <summary>
    /// --dailycheck[=yyyymmdd]: the autopilot plays that day's challenge (today's without a date) on a scratch profile.
    /// Checks the mode, the arena every battle, the starting hand on everyone and the Hard bots, then that the result
    /// was saved as the day's best and that a worse one doesn't replace it.
    /// </summary>
    private void StartDailyCheck(string arg)
    {
        _autoMode = "daily";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.LocalName = "Auto";
        Profile.Path = "user://dailycheck_profile.cfg";
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Profile.Path));
        var d = arg.Contains('=') && int.TryParse(arg.Split('=')[1], out int day) ? Daily.For(day) : Daily.For(DateTime.UtcNow);
        _dcProblems.Clear();
        _game.BattleStarted += () =>
        {
            if (_game.ArenaName != $"Daily {d.DateText}") _dcProblems.Add($"battle {_game.BattleNumber} on \"{_game.ArenaName}\"");
            if (_game.BattleNumber != 1) return;
            foreach (var p in _game.Slots.Where(p => p != null))
                if (!d.StartingCards.All(p.Cards.Contains)) _dcProblems.Add($"{p.Info.Name} started without the day's cards");
        };
        StartDaily(d);
        if (_game.Mode != d.Mode) _dcProblems.Add($"mode {_game.Mode}, not {d.Mode}");
        if (_net.Lobby.Players.Count(p => p.IsBot && p.Skill == BotSkill.Hard) != Daily.Bots) _dcProblems.Add("not three Hard bots");
    }

    private readonly List<string> _dcProblems = new();

    private string DailyCheckProblem()
    {
        if (_autoMode != "daily") return null;
        if (_dcProblems.Count > 0) return $"dailycheck: {string.Join("; ", _dcProblems)}";
        int day = _daily?.Day ?? 0;
        var saved = DailyBest.Decode(Profile.LoadDailyBest(), day);
        if (saved == null) return "dailycheck: the result wasn't saved";
        Profile.SaveDailyBest(DailyBest.Merge(Profile.LoadDailyBest(), day, new DailyResult(false, 0, 0)));
        if (DailyBest.Decode(Profile.LoadDailyBest(), day) != saved) return "dailycheck: a worse run replaced the best";
        return null;
    }
}
