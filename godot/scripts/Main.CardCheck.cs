using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler;

/// <summary>
/// --cardcheck: every built-in card's effect, played. On the training range (the fallen come back, the battle never
/// ends) the autopilot and two bots all hold one card at a time and fight until each effect the card adds has been seen
/// to happen (<see cref="EffectTally"/>: a bounce, a split, a lifesteal heal, a blink...), then move on to the next card.
/// Cards that only change numbers (HP, speed, damage...) are checked to change them. Fails on an effect that never
/// fires within the time limit, a number that doesn't change, or any error logged along the way.
/// </summary>
public partial class Main
{
    /// <summary>How long one card gets to show every effect it adds.</summary>
    private const float CardCheckSeconds = 90;

    private List<CardDef> _ccCards;
    private int _ccIndex = -1, _ccSeen, _ccNumbersOnly;
    private float _ccT;
    private List<Stat> _ccWant;
    private readonly List<string> _ccProblems = new();

    private void StartCardCheck()
    {
        _autoMode = "cardcheck";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _game.DisableKillCam = true;
        _net.LocalName = "Auto";
        _cardFilter = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--cardcheck="))?["--cardcheck=".Length..];
        _ccCards = CardRegistry.All.Where(c => c.Id < CustomCards.MinId && (_cardFilter == null || c.Name.Contains(_cardFilter, System.StringComparison.OrdinalIgnoreCase))).ToList();
        if (_ccCards.Count == 0) { Finish(1, $"cardcheck: no card matches '{_cardFilter}'"); return; }
        _net.StartOffline();
        _net.SetSettings(new MatchSettings { KillCam = false, LavaAfter = 0 });
        _net.AddBot();
        _net.AddBot();
        _game.Training = true;
        _game.Stats.Enabled = false;
        _trainingOpen = false;
        EffectTally.On = true;
        _game.HostStartMatch();
    }

    private string _cardFilter;

    private void PollCards()
    {
        if (_game.Phase != Phase.Fighting || _ccIndex >= _ccCards.Count) return;
        if (_ccIndex >= 0)
        {
            _ccT += 1f / 60;
            CardCheckNudge();
            var missing = _ccWant.Where(s => EffectTally.Count(s) == 0).ToList();
            if (missing.Count > 0 && _ccT < CardCheckSeconds) return;
            var card = _ccCards[_ccIndex];
            if (missing.Count > 0)
                _ccProblems.Add($"{card.Name} ({card.Id}): {string.Join(", ", missing)} never happened in {CardCheckSeconds:0} s");
            _ccSeen += _ccWant.Count - missing.Count;
            Log.Info($"CARDCHECK {card.Name} ({card.Id}): " + (_ccWant.Count == 0 ? "numbers only"
                : string.Join(", ", _ccWant.Select(s => $"{s} x{EffectTally.Count(s)}"))) + $" in {_ccT:0.0} s");
        }
        if (++_ccIndex >= _ccCards.Count)
        {
            EffectTally.On = false;
            Finish(_ccProblems.Count > 0 ? 1 : 0, _ccProblems.Count > 0
                ? $"cardcheck: {_ccProblems.Count} card(s) failed: {string.Join("; ", _ccProblems)}"
                : $"cardcheck: {_ccCards.Count} cards played, {_ccSeen} effects seen happening, {_ccNumbersOnly} cards only change numbers (checked to change them)");
            return;
        }
        StartCardCheckCard(_ccCards[_ccIndex]);
    }

    private void StartCardCheckCard(CardDef card)
    {
        var baseStats = StatBlock.Base();
        var held = StatBlock.Compute(new[] { card });
        // Every number the card touches has to move (a mod can't be lost on the way to the stat block).
        var still = card.Mods.Select(m => m.Stat).Distinct().Where(s => held[s] == baseStats[s]).ToList();
        if (still.Count > 0) _ccProblems.Add($"{card.Name} ({card.Id}): {string.Join(", ", still)} didn't change");
        // The effects to wait for: what the card turns on or strengthens (a curse that weakens one has nothing to show).
        _ccWant = card.Mods.Select(m => m.Stat).Distinct().Where(s => EffectTally.Observable(s) && held[s] > baseStats[s]).ToList();
        if (card.EffectId == "blink" && !_ccWant.Contains(Stat.TeleportOnBlock)) _ccWant.Add(Stat.TeleportOnBlock);
        if (_ccWant.Count == 0) _ccNumbersOnly++;
        foreach (var p in _game.Slots)
            if (p != null) _game.SetTrainingCards(p, new[] { card.Id });
        // Each card from a fresh battle (Reset keeps the cards): the range's battle never ends on its own, so revives
        // used up under one card would otherwise still be used up under the next.
        if (_ccIndex > 0) _game.HostResetTraining();
        EffectTally.Clear();
        _ccT = 0;
    }

    /// <summary>
    /// Some effects need a moment an open fight with bots seldom brings about. While one of those hasn't happened yet,
    /// every 2 s the check sets it up between the two bots (who both hold the card), and the game does the rest.
    /// </summary>
    private void CardCheckNudge()
    {
        bool Want(Stat s) => _ccWant.Contains(s) && EffectTally.Count(s) == 0;
        int frame = (int)(_ccT * 60) % 120;
        var bots = _game.Slots.Where(p => p is { Info.IsBot: true, Avatar: { Alive: true, Guarded: false } }).Select(p => p.Avatar).ToList();
        if (bots.Count < 2) return;
        var (a, b) = (bots[0], bots[1]);
        if (frame >= 60 && frame < 90)
        {
            // For half a second: Shield Bash lands on someone touching your shield (bots block into an enemy that
            // close); Chain Lightning arcs from whoever is hit to another enemy within 150 px.
            float gap = Want(Stat.ShieldBash) ? a.HalfSize.X + b.HalfSize.X + 2 : Want(Stat.Chain) ? 70 : -1;
            if (gap < 0) return;
            b.GlobalPosition = a.GlobalPosition + new Vector2(gap, 0);
            b.Velocity = Vector2.Zero;
        }
        else if (frame == 0 && _ccT > 5 && a.BlockActive <= 0)
        {
            // A revive (or a parting blast) needs a lethal hit, and big-HP cards can go a minute without one: a hit
            // worth all of one bot's HP from the other, through the usual damage path. Adrenaline and Berserk need
            // someone low on HP (and firing): a hit down to 30%.
            if (Want(Stat.ExtraLives) || Want(Stat.Kamikaze)) _game.HostDamage(a, a.Hp + 1, Vector2.Zero, b.Player.Slot, a.GlobalPosition);
            else if ((Want(Stat.Adrenaline) || Want(Stat.Berserk)) && a.Hp > a.S[Stat.MaxHp] * 0.35f)
                _game.HostDamage(a, a.Hp - a.S[Stat.MaxHp] * 0.3f, Vector2.Zero, b.Player.Slot, a.GlobalPosition);
        }
    }
}
