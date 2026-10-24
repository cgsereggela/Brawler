using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Brawler.UI;
using Godot;

namespace Brawler;

/// <summary>
/// The training range (Training Range on the main menu): you alone on a small arena with two target dummies and a bot
/// that moves and blocks but never fires, in one battle that never ends (the fallen come back). The card browser
/// (<see cref="TrainingUI"/>, Tab) puts any card on you, rares and curses included.
/// </summary>
public partial class Main
{
    private TrainingUI _training;
    private bool _trainingOpen = true; // the browser shows when the range starts; Tab toggles it
    private bool _statsBeforeTraining; // the card balance log is off on the range (hand-picked cards and dummies would skew it)

    /// <summary>The range's slots after yours: two dummies, then the sparring bot.</summary>
    private static readonly string[] TrainingNames = { "Dummy", "Dummy 2", "Sparring Bot" };

    private void AddTrainingUI(Control root)
    {
        _training = new TrainingUI { Name = "Training", Visible = false };
        _training.Held = () => _game.LocalMatchPlayer?.Cards.ToList() ?? new List<int>();
        _training.CardsSet += ids => _game.SetTrainingCards(_game.LocalMatchPlayer, ids);
        _training.ResetPressed += _game.HostResetTraining;
        root.AddChild(_training);
    }

    /// <summary>The browser shows over the arena while the range is on, unless Tab put it away or a menu is over it.</summary>
    private void UpdateTrainingUI()
    {
        bool show = _game.Training && _trainingOpen && _hud.Visible && !_options.Visible && !_over.Visible;
        if (_training.Visible != show) _training.Visible = show;
        if (_game.Training && !_overlay) LocalInput.Suppressed = show;
    }

    private void StartTraining()
    {
        if (_autoMode == null) SaveProfile();
        _net.StartOffline();
        _net.SetSettings(new MatchSettings { KillCam = false, LavaAfter = 0, Maps = { MapCode.Pack(TrainingArena()) } });
        for (int i = 0; i < TrainingNames.Length; i++) _net.AddBot();
        var bots = _net.Lobby.Players.Where(p => p.IsBot).OrderBy(p => p.Slot).ToList();
        for (int i = 0; i < bots.Count; i++) bots[i].Name = TrainingNames[i];
        _game.Training = true;
        _statsBeforeTraining = _game.Stats.Enabled;
        _game.Stats.Enabled = false;
        _trainingOpen = true;
        _game.HostStartMatch();
    }

    /// <summary>Each battle (the start and every Reset): the dummies stand still and the sparring bot holds its fire.</summary>
    private void TrainingBattleStarted()
    {
        if (!_game.Training || _autoMode == "cardcheck") return; // --cardcheck fights two ordinary bots
        var bots = _game.Slots.Where(p => p != null && p.Info.IsBot).OrderBy(p => p.Slot).ToList();
        for (int i = 0; i < bots.Count; i++)
        {
            if (i < 2) { if (bots[i].Input is not DummyInput) bots[i].Input = new DummyInput(); }
            else if (bots[i].Input is not PassiveInput) bots[i].Input = new PassiveInput(bots[i].Input);
        }
    }

    private void TrainingEnded()
    {
        if (!_game.Training) return;
        _game.Stats.Enabled = _statsBeforeTraining;
        _game.Training = false;
        if (_training != null) _training.Visible = false;
        if (!_overlay) LocalInput.Suppressed = false;
    }

    /// <summary>
    /// A small open range: a floor with a ledge on each side, so there is something to bounce shots off and to jump to.
    /// Four spawns (who gets which turns with each Reset, as in any battle), one of them up on the right ledge.
    /// </summary>
    private static MapDesign TrainingArena()
    {
        var d = MapDesign.Empty(ThemeId.SunsetPeaks, 1);
        d.Name = "Training Range";
        int floor = MapGenerator.FloorY;
        d.Blocks.Add(new RectI(80, floor - 150, 180, 16));
        d.Blocks.Add(new RectI(740, floor - 150, 180, 16));
        d.Spawns.Add(new SpawnPoint(160, floor));
        d.Spawns.Add(new SpawnPoint(560, floor));
        d.Spawns.Add(new SpawnPoint(830, floor - 150));
        d.Spawns.Add(new SpawnPoint(700, floor));
        return d;
    }

    // ---------- --trainingcheck: the range on autopilot ----------

    private int _tcStage, _tcWait, _tcDummyDeaths, _tcRespawns, _tcSparShots;
    private float _tcFight;
    private readonly Dictionary<int, bool> _tcAlive = new();
    private List<int> _tcCards;

    /// <summary>
    /// --trainingcheck: the autopilot on the range. Checks the card browser opens and Tab hides it, that hand-set cards
    /// (a rare, a curse and a health card) take hold at once, that over 40 s the dummies die and come back while the battle
    /// never ends and the sparring bot never fires, and that Reset starts the battle over with the cards kept.
    /// </summary>
    private void StartTrainingCheck()
    {
        _autoMode = "trainingcheck";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.LocalName = "Auto";
        StartTraining();
    }

    private void PollTraining()
    {
        if (_game.Phase != Phase.Fighting) return;
        var me = _game.LocalMatchPlayer;
        var bots = _game.Slots.Where(p => p != null && p.Info.IsBot).OrderBy(p => p.Slot).ToList();
        switch (_tcStage)
        {
            case 0: // the browser is up; Tab puts it away
                if (!_training.Visible) { Finish(1, "trainingcheck: the card browser didn't open with the range"); return; }
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, Pressed = true });
                _tcStage = 1;
                return;
            case 1:
                if (++_tcWait < 10) return; // the key goes through input and the browser updates in _Process
                if (_training.Visible) { Finish(1, "trainingcheck: Tab didn't hide the card browser"); return; }
                var baseHp = StatBlock.Base()[Stat.MaxHp];
                var health = CardRegistry.All.First(c => !c.IsRare && !c.IsCurse && StatBlock.Compute(new[] { c })[Stat.MaxHp] > baseHp);
                _tcCards = new() { CardRegistry.All.First(c => c.IsRare).Id, CardRegistry.All.First(c => c.IsCurse).Id, health.Id };
                _game.SetTrainingCards(me, _tcCards);
                if (_shotDir != null) { _trainingOpen = true; GetTree().CreateTimer(0.5).Timeout += () => { SaveShot(); _trainingOpen = false; }; } // the browser with the cards held
                if (!me.Cards.SequenceEqual(_tcCards) || me.Avatar?.Hp != me.Stats[Stat.MaxHp])
                { Finish(1, $"trainingcheck: the cards didn't take hold (holding {me.Cards.Count}, hp {me.Avatar?.Hp} of {me.Stats[Stat.MaxHp]})"); return; }
                if (bots.Count != 3 || bots[0].Input is not DummyInput || bots[1].Input is not DummyInput || bots[2].Input is not PassiveInput)
                { Finish(1, "trainingcheck: not two dummies and a passive bot"); return; }
                _tcStage = 2;
                return;
            case 2: // fight for a while
                _tcFight += 1f / 60;
                for (int i = 0; i < 2; i++)
                {
                    bool alive = bots[i].Avatar is { Alive: true };
                    if (_tcAlive.TryGetValue(i, out bool was) && was != alive) { if (alive) _tcRespawns++; else _tcDummyDeaths++; }
                    _tcAlive[i] = alive;
                }
                _tcSparShots += _game.Bullets.Bullets.Count(b => b.Owner == bots[2].Slot);
                if (_game.BattleNumber != 1) { Finish(1, $"trainingcheck: the battle ended (now battle {_game.BattleNumber})"); return; }
                if (_tcFight < 40) return;
                if (_tcSparShots > 0) { Finish(1, "trainingcheck: the sparring bot fired"); return; }
                if (_tcDummyDeaths == 0 || _tcRespawns == 0) { Finish(1, $"trainingcheck: dummies died {_tcDummyDeaths} times and came back {_tcRespawns} times in 40 s"); return; }
                _game.HostResetTraining();
                _tcStage = 3;
                return;
            case 3: // after Reset
                if (_game.BattleNumber != 2) return;
                if (!me.Cards.SequenceEqual(_tcCards)) { Finish(1, "trainingcheck: Reset lost the cards"); return; }
                Finish(0, $"trainingcheck: dummies died {_tcDummyDeaths} times and came back {_tcRespawns} times in 40 s, the sparring bot held its fire, Reset kept the cards");
                _tcStage = 4;
                return;
        }
    }

    /// <summary>Tab on the range shows or hides the card browser. Ahead of the GUI, which would take Tab to move focus.</summary>
    private bool TrainingKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab } || !_game.Training || !_hud.Visible || _overlay) return false;
        _trainingOpen = !_trainingOpen;
        GetViewport().SetInputAsHandled();
        return true;
    }
}
