using System.Linq;
using System.Threading.Tasks;
using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Brawler.UI;
using Godot;

namespace Brawler;

// --demo=NAME: a scripted walk through one feature, paced for a viewer, to record with --write-movie for a Short.
// Each demo logs "DEMO <frame> <label>" at the moments worth cutting to, so the edit can find them in the recording
// (frame / 60 is the time in the video). Demos keep their maps and settings apart from the player's.
public partial class Main
{
    private async void Demo(string name)
    {
        _autoMode = "demo";
        GetViewport().GuiDisableInput = true; // the real mouse over the window mustn't hover things in the recording
        _game.Stats.Enabled = false;
        MapLibrary.Dir = "user://demo/maps";
        MapLibrary.SettingsPath = "user://demo/settings.cfg";
        Profile.Path = "user://demo/profile.cfg"; // never the player's own profile or career
        DirAccess.RemoveAbsolute(Profile.Path);
        CareerBook.Set(new Career());
        DirAccess.MakeDirRecursiveAbsolute(MapLibrary.Dir);
        foreach (var f in DirAccess.GetFilesAt(MapLibrary.Dir)) DirAccess.RemoveAbsolute($"{MapLibrary.Dir}/{f}");
        MapLibrary.Draft = "";
        MapLibrary.LobbyMaps = new();
        await DemoWait(0.5);
        switch (name)
        {
            case "thumbs": await DemoThumbs(); break;
            case "botskill": await DemoBotSkill(); break;
            case "draft": await DemoDraft(); break;
            case "gungame": await DemoGunGame(); break;
            case "survival": await DemoSurvival(); break;
            case "cosmetics": await DemoCosmetics(); break;
            default: Finish(1, $"--demo: no demo called {name}"); return;
        }
        Finish(0, $"demo {name} done");
    }

    private async Task DemoWait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private static void DemoMark(string label) => Log.Info($"DEMO {Engine.GetFramesDrawn()} {label}");

    /// <summary>Hides labels showing a folder on this computer (its user name) before they get recorded.</summary>
    private static void HidePaths(Node root)
    {
        while (FindChild<Label>(root, l => l.Visible && l.Text.Contains(":/Users/")) is { } l) l.Visible = false;
    }

    private static void Press(Node root, string text) => FindButton(root, text)?.EmitSignal(BaseButton.SignalName.Pressed);

    private static System.Collections.Generic.List<T> FindAll<T>(Node root, System.Func<T, bool> match) where T : Node
    {
        var list = new System.Collections.Generic.List<T>();
        foreach (var c in root.GetChildren())
        {
            if (c is T t && match(t)) list.Add(t);
            list.AddRange(FindAll(c, match));
        }
        return list;
    }

    // Map thumbnails: a few saved arenas in My maps, then the lobby's Arenas panel picking some for a match.
    private async Task DemoThumbs()
    {
        var maps = new (string Name, MapStyle Style, ThemeId Theme, int Seed)[]
        {
            ("Sky Temple", MapStyle.Temple, ThemeId.CandyClouds, 11), ("Lava Lamp", MapStyle.Cave, ThemeId.MagmaCore, 23),
            ("Neon Rooftops", MapStyle.Towers, ThemeId.NeonNight, 5), ("Coral Steps", MapStyle.Tiers, ThemeId.DeepReef, 41),
            ("Frozen Tunnel", MapStyle.Tunnel, ThemeId.FrostBite, 8), ("Mirror Grove", MapStyle.Mirror, ThemeId.ToxicGrove, 17),
        };
        foreach (var m in maps)
        {
            var d = MapDesign.Generate(m.Style, m.Theme, m.Seed);
            d.Name = m.Name;
            MapLibrary.Save(d);
        }
        Press(_menu, "Map Editor");
        await DemoWait(1.5);
        DemoMark("library");
        Press(_editor, "My maps");
        HidePaths(_editor);
        await DemoWait(4.5);
        Press(_editor, "DONE");
        await DemoWait(0.5);
        Press(_editor, "Back");
        await DemoWait(0.4);
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(1.0);
        DemoMark("arenas");
        Press(_lobby, "Random");
        await DemoWait(1.5);
        foreach (var pick in new[] { "Lava Lamp", "Sky Temple", "Neon Rooftops" })
        {
            if (FindChild<CheckBox>(_lobby, c => c.Text == pick && c.IsVisibleInTree()) is { } tick) tick.ButtonPressed = true;
            await DemoWait(0.9);
        }
        await DemoWait(2.0);
        Press(_lobby, "DONE");
        await DemoWait(0.8);
        DemoMark("match");
        _game.AutopilotLocal = true;
        _game.HostStartMatch();
        await DemoWait(9.0);
        DemoMark("end");
    }

    // Bot skill: three bots in the lobby set to Easy, Normal and Hard with their buttons, then a match against them.
    private async Task DemoBotSkill()
    {
        _game.DisableKillCam = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        await DemoWait(1.2);
        DemoMark("lobby");
        // Each press rebuilds the cards, so look the buttons up again every time.
        Button SkillButton(int bot) => FindAll<Button>(_lobby, b => b.IsVisibleInTree() && b.Text is "Easy" or "Normal" or "Hard") is var bs && bot < bs.Count ? bs[bot] : null;
        foreach (var (bot, presses) in new[] { (0, 2), (1, 1), (2, 3) })
            for (int i = 0; i < presses; i++)
            {
                SkillButton(bot)?.EmitSignal(BaseButton.SignalName.Pressed);
                await DemoWait(0.7);
            }
        await DemoWait(2.0);
        DemoMark("match");
        _game.AutopilotLocal = true;
        _game.HostStartMatch();
        await DemoWait(40.0);
        DemoMark("end");
    }

    // Draft: turn Draft on in the lobby, then four players take turns picking from one shared pool, then the first battle.
    private async Task DemoDraft()
    {
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        _pick.ShowAutopilotPick = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        await DemoWait(1.0);
        DemoMark("lobby");
        // The setting's button sits right after its label in the settings grid.
        if (FindChild<Label>(_lobby, l => l.Text == "Draft") is { } label) (label.GetParent().GetChild(label.GetIndex() + 1) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        if (!_net.Lobby.Settings.Draft) { Finish(1, "demo draft: the Draft button didn't turn it on"); return; }
        await DemoWait(1.8);
        DemoMark("draft");
        _game.HostStartMatch();
        while (_game.Phase is Phase.CardPick or Phase.Idle) await DemoWait(0.1);
        DemoMark("battle");
        await DemoWait(14.0);
        DemoMark("end");
    }

    // Gun Game: pick it with the lobby's Game mode button, then a free-for-all where every kill swaps your card.
    private async Task DemoGunGame()
    {
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        await DemoWait(1.0);
        DemoMark("lobby");
        for (int i = 0; i < 6 && _net.Lobby.Settings.Mode != GameMode.GunGame; i++)
        {
            _gameModeButton()?.EmitSignal(BaseButton.SignalName.Pressed);
            await DemoWait(0.6);
        }
        if (_net.Lobby.Settings.Mode != GameMode.GunGame) { Finish(1, "demo gungame: the Game mode button never reached Gun Game"); return; }
        await DemoWait(1.6);
        DemoMark("match");
        _game.HostStartMatch();
        var levels = new int[LobbyState.MaxPlayers];
        double t = 0;
        while (_game.Phase != Phase.MatchOver && t < 150)
        {
            foreach (var p in _game.Slots)
                if (p != null && GameManager.GunLevel(p) is var lv && lv != levels[p.Slot])
                {
                    levels[p.Slot] = lv;
                    DemoMark($"level {p.Slot} {lv}");
                }
            await DemoWait(0.1);
            t += 0.1;
        }
        DemoMark("over");
        await DemoWait(5.0);
        DemoMark("end");

        Button _gameModeButton() => FindChild<Label>(_lobby, l => l.Text == "Game mode") is { } label ? label.GetParent().GetChild(label.GetIndex() + 1) as Button : null;
    }

    // Survival: two players at one screen pick Survival, then hold off the waves of bots together.
    private async Task DemoSurvival()
    {
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        _pick.ShowAutopilotPick = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        _net.AddGuest(100);
        while (_net.Lobby.Players.Count < 6) _net.AddBot();
        await DemoWait(1.0);
        DemoMark("lobby");
        for (int i = 0; i < 7 && _net.Lobby.Settings.Mode != GameMode.Survival; i++)
        {
            (FindChild<Label>(_lobby, l => l.Text == "Game mode") is { } label ? label.GetParent().GetChild(label.GetIndex() + 1) as Button : null)?.EmitSignal(BaseButton.SignalName.Pressed);
            await DemoWait(0.45);
        }
        if (_net.Lobby.Settings.Mode != GameMode.Survival) { Finish(1, "demo survival: the Game mode button never reached Survival"); return; }
        await DemoWait(1.6);
        // A run that falls in the first couple of waves shows too little: go again (the edit cuts from the last run).
        for (int run = 1; run <= 4; run++)
        {
            DemoMark($"run {run}");
            _game.HostStartMatch();
            int wave = 0;
            double t = 0;
            while (_game.Phase != Phase.MatchOver && t < 240)
            {
                if (_game.BattleNumber != wave && _game.Phase == Phase.Countdown) DemoMark($"wave {wave = _game.BattleNumber} bots {_game.WaveBots}");
                await DemoWait(0.1);
                t += 0.1;
            }
            if (_game.LocalMatchPlayer.RoundWins >= 3) break;
            DemoMark($"retry after {_game.LocalMatchPlayer.RoundWins} waves");
            await DemoWait(2.0);
        }
        DemoMark("over");
        await DemoWait(5.0);
        DemoMark("end");
    }

    // Cosmetics: try on a locked trail and hat in the editor, finish a match that reaches two milestones, then wear them.
    private async Task DemoCosmetics()
    {
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        CareerBook.Set(new Career { Matches = 2, Wins = 1, Kills = 49 }); // one match short of Sparks, one kill short of Pirate
        CareerBook.Enabled = true;
        Press(_menu, "Customize Character");
        await DemoWait(1.2);
        DemoMark("editor");
        // The steppers, in order: eyes, mouth, hair, hat, trail.
        Button Next(int stepper) => FindAll<Button>(_custom, b => b.Text == ">" && b.IsVisibleInTree()) is var bs && stepper < bs.Count ? bs[stepper] : null;
        Next(4)?.EmitSignal(BaseButton.SignalName.Pressed); // Sparks trail: locked
        await DemoWait(2.4);
        // Back through the hats (None, then the end of the list) to the Pirate hat: locked too.
        for (int i = 0; i < 20 && FindChild<Label>(_custom, l => l.Text.StartsWith("Pirate")) == null; i++)
        {
            FindAll<Button>(_custom, b => b.Text == "<" && b.IsVisibleInTree())[3].EmitSignal(BaseButton.SignalName.Pressed);
            await DemoWait(0.6);
        }
        await DemoWait(2.4);
        Press(_custom, "DONE");
        await DemoWait(0.6);
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 3) _net.AddBot();
        _net.Lobby.Settings.RoundsToWin = 1;
        _net.Lobby.Settings.BattlesPerRound = 1;
        await DemoWait(0.8);
        // Keep playing until the milestones fall (the autopilot needs three kills for the hat).
        for (int match = 1; match <= 6 && Unlocks.For(LookPart.Hat, 14) is { } hat && !hat.Met(CareerBook.Current); match++)
        {
            DemoMark($"match {match}");
            _game.HostStartMatch();
            while (_game.Phase != Phase.MatchOver) await DemoWait(0.1);
            DemoMark(CareerBook.JustUnlocked.Count > 0 ? $"unlocked {string.Join(",", CareerBook.JustUnlocked.Select(u => u.Name))}" : "nothing new");
            await DemoWait(CareerBook.JustUnlocked.Count > 0 ? 4.5 : 1.0);
            if (!Unlocks.For(LookPart.Hat, 14).Met(CareerBook.Current)) _game.HostReturnToLobby();
            await DemoWait(0.5);
        }
        _game.HostReturnToLobby();
        await DemoWait(0.8);
        Press(_lobby, "Customize");
        await DemoWait(1.0);
        DemoMark("wear");
        // The editor opens on the saved look, which had the locked picks taken off: put them back on, earned now.
        Next(4)?.EmitSignal(BaseButton.SignalName.Pressed);
        await DemoWait(0.8);
        for (int i = 0; i < 20 && _net.LocalLook.Hat != 14; i++) // one step back from None is the Pirate hat
        {
            FindAll<Button>(_custom, b => b.Text == "<" && b.IsVisibleInTree())[3].EmitSignal(BaseButton.SignalName.Pressed);
            await DemoWait(0.6);
        }
        await DemoWait(2.0);
        Press(_custom, "DONE");
        await DemoWait(0.8);
        DemoMark("fight");
        _game.HostStartMatch();
        await DemoWait(16.0);
        DemoMark("end");
    }
}
