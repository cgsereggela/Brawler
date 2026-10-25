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
            case "clips": await DemoClips(); break;
            case "access": await DemoAccess(); break;
            case "drift": await DemoDrift(DriftDemoArena()); break;
            case "hanger": await DemoHanger(); break;
            case "driftstyle": await DemoDrift(MapDesign.Generate(MapStyle.Drift, ThemeId.CandyClouds, 1)); break;
            case "sky": await DemoDrift(MapDesign.Generate(MapStyle.Sky, ThemeId.SunsetPeaks, 1)); break;
            case "customcards": await DemoCustomCards(); break;
            case "editor": await DemoEditor(); break;
            case "wardrobe": await DemoWardrobe(); break;
            case "daily": await DemoDaily(); break;
            case "training": await DemoTraining(); break;
            case "stats": await DemoStats(); break;
            case "cardicons": await DemoCardIcons(); break;
            case "portals": await DemoPortals(); break;
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

    // Hanging platforms: from a ledge in the middle the player shoots the chains of one platform after another while two
    // bots fight underneath. One hangs from a block, one from the ceiling on two chains, and one over a pit falls right out.
    private async Task DemoHanger()
    {
        _game.DisableKillCam = true;
        var d = MapDesign.Empty(ThemeId.DungeonDepths, 2);
        d.Name = "Hanger Demo";
        d.Blocks.Add(new RectI(440, 304, 120, 16));
        d.Blocks.Add(new RectI(560, 120, 120, 16));
        d.Blocks.Add(new RectI(824, 440, 96, 16));
        d.Pits.Add((680, 800));
        d.Hangers.Add(new Hanger(600, 232, 48, 8));
        d.Hangers.Add(new Hanger(232, 280, 128, 8));
        d.Hangers.Add(new Hanger(696, 288, 96, 8));
        d.Spawns.Add(new SpawnPoint(860, 440));
        d.Spawns.Add(new SpawnPoint(300, MapGenerator.FloorY));
        d.Spawns.Add(new SpawnPoint(560, MapGenerator.FloorY));
        MapLibrary.LobbyMaps = new() { MapCode.Encode(d) };
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 3) _net.AddBot();
        await DemoWait(1.0);
        DemoMark("match");
        _game.HostStartMatch();
        double t = 0;
        int dropped = 0;
        bool placed = false;
        while (_game.Phase != Phase.MatchOver && t < 60)
        {
            if (_game.LocalMatchPlayer is { } me && me.Input is not ChainShooter) me.Input = new ChainShooter(_game);
            // Up on the ledge, level with the chains, once each fight starts, and kept alive to shoot them all.
            if (_game.Phase != Phase.Fighting) placed = false;
            else if (_game.LocalMatchPlayer?.Avatar is { Alive: true } av)
            {
                if (!placed) av.GlobalPosition = new Vector2(500, 304 - av.HalfSize.Y);
                if (!placed) av.Velocity = Vector2.Zero;
                placed = true;
                av.Hp = Mathf.Max(av.Hp, av.S[Stat.MaxHp] * 0.6f);
            }
            if (_game.Arena.HangersDropped != dropped) DemoMark($"dropped {dropped = _game.Arena.HangersDropped}");
            await DemoWait(0.1);
            t += 0.1;
        }
        DemoMark($"over: {_game.Arena.Summary}");
        await DemoWait(2.0);
        DemoMark("end");
    }

    /// <summary>Demo input: stand still and shoot the nearest chain still holding a platform up.</summary>
    private sealed class ChainShooter : IInputSource
    {
        private readonly GameManager _game;
        private float _t;
        public ChainShooter(GameManager game) => _game = game;

        public InputFrame Next(Avatar self, float dt)
        {
            _t += dt;
            var f = new InputFrame { Aim = self.GlobalPosition + Vector2.Up * 40 };
            if (_game.Phase != Phase.Fighting || _t < 1.5f) return f;
            float best = float.MaxValue;
            foreach (var h in _game.Arena.Hangers)
            {
                if (h.FellAt >= 0) continue;
                for (int c = 0; c < h.ChainXs.Length; c++)
                {
                    if (h.ChainCut(c)) continue;
                    var (top, bottom) = h.ChainEnds(c);
                    var at = (top + bottom) * 0.5f;
                    float dist = at.DistanceTo(self.GlobalPosition);
                    if (dist < best) { best = dist; f.Aim = at; }
                }
            }
            if (best == float.MaxValue) return f;
            // Bullets drop: aim above the chain by as much as one falls on the way there.
            float time = best / Mathf.Max(1, self.S[Stat.BulletSpeed]);
            f.Aim -= new Vector2(0, 0.5f * self.S[Stat.BulletGravity] * time * time);
            f.Shoot = (int)(_t * 3) % 2 == 0;
            return f;
        }
    }

    // A floor and a staircase of drifting platforms.
    private static MapDesign DriftDemoArena()
    {
        var d = MapDesign.Empty(ThemeId.SunsetPeaks, 3);
        int w = MapLayout.Width;
        foreach (var (x, tier) in new[] { (120, 1), (w - 184, 1), (300, 2), (w - 364, 2), (w / 2 - 32, 3), (180, 4), (w - 244, 4) })
            d.Drifts.Add(new Drift(x, MapGenerator.TierY(tier) / 8 * 8, 64, 8));
        return d;
    }

    // Drifting platforms (drift), or a Drift arena (driftstyle): bots fight over platforms that sway when landed on,
    // shot or blasted.
    private async Task DemoDrift(MapDesign d)
    {
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        d.Name = "Drift Demo";
        MapLibrary.LobbyMaps = new() { MapCode.Encode(d) };
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        await DemoWait(1.0);
        DemoMark("match");
        _game.HostStartMatch();
        double t = 0;
        float last = 0;
        while (_game.Phase != Phase.MatchOver && t < 90)
        {
            if (_game.Arena.DriftTravel - last > 50) DemoMark($"drift {last = _game.Arena.DriftTravel:0}");
            await DemoWait(0.25);
            t += 0.25;
        }
        DemoMark($"over, drifts moved {_game.Arena.DriftTravel:0} px");
        await DemoWait(3.0);
        DemoMark("end");
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

    // Clip export: a one-battle match, its kill cam, Save kill cam GIF on the results, then the GIF itself playing.
    private async Task DemoClips()
    {
        _game.AutopilotLocal = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 1 });
        _game.KillCamStarted += () => DemoMark("killcam");
        DemoMark("match");
        _game.HostStartMatch();
        for (int i = 0; i < 240 && !(_over.Visible && KillCamClips.HasClip); i++) await DemoWait(0.5);
        await DemoWait(1.5);
        DemoMark("results");
        await DemoWait(1.2);
        Press(_over, "SAVE KILL CAM GIF");
        for (int i = 0; i < 60 && !KillCamClips.Saved; i++) await DemoWait(0.25);
        DemoMark("saved");
        await DemoWait(1.8);
        if (KillCamClips.LastSaved != null) System.IO.File.Delete(KillCamClips.LastSaved); // the demo's clip isn't kept

        // The saved GIF, played back over the results the way a friend would see it.
        var frames = KillCamClips.Frames;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.75f) };
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _over.GetParent().AddChild(shade);
        var box = UiKit.VBox(12);
        box.Alignment = BoxContainer.AlignmentMode.Center;
        shade.AddChild(box);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.AddChild(UiKit.Label("brawler-killcam.gif", UiKit.Body, Palette.TextDim, HorizontalAlignment.Center, Brawler.Fx.Fonts.Bold));
        var shot = new TextureRect { CustomMinimumSize = new Vector2(KillCamClips.W, KillCamClips.H) * 2.6f, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        box.AddChild(shot);
        DemoMark("gif");
        for (int loop = 0; loop < 2; loop++)
            for (int f = 0; f < frames.Count; f++)
            {
                shot.Texture = ImageTexture.CreateFromImage(Image.CreateFromData(KillCamClips.W, KillCamClips.H, false, Image.Format.Rgb8, frames[f]));
                await DemoWait(0.05);
            }
        DemoMark("end");
    }

    /// <summary>
    /// Accessibility: a full lobby in the usual colors, then the colorblind-safe ones; the Video tab's new rows, the UI
    /// growing to 115% and 130%; then a match at that size. Nothing is saved: the user's own settings come back after.
    /// </summary>
    private async Task DemoAccess()
    {
        bool userColors = Accessibility.ColorblindColors;
        float userScale = Accessibility.UiScale;
        Accessibility.PreviewColorblind(false);
        Accessibility.PreviewUiScale(1);
        _game.AutopilotLocal = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < LobbyState.MaxPlayers) _net.AddBot();
        await DemoWait(0.4);
        DemoMark("lobby");
        await DemoWait(2.5);
        OpenOptions(_lobby);
        var tabs = FindAll<TabContainer>(_options, _ => true).First();
        tabs.CurrentTab = 1;
        DemoMark("options");
        await DemoWait(1.5);
        FindAll<CheckButton>(_options, c => c.Text == "Colorblind-safe colors").First().SetPressedNoSignal(true);
        Accessibility.PreviewColorblind(true);
        DemoMark("safe");
        await DemoWait(1.5);
        Accessibility.PreviewUiScale(1.15f);
        DemoMark("scale115");
        await DemoWait(1.2);
        Accessibility.PreviewUiScale(1.3f);
        DemoMark("scale130");
        await DemoWait(1.8);
        Press(_options, "DONE");
        DemoMark("lobby_safe");
        await DemoWait(2.5);
        _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 1 });
        DemoMark("match");
        _game.HostStartMatch();
        await DemoWait(5);
        await DemoWait(5);
        DemoMark("end");
        Accessibility.PreviewColorblind(userColors);
        Accessibility.PreviewUiScale(userScale);
    }

    // Custom cards: a card file typed out in a text editor, the card it makes (with its CUSTOM tag), then a match where
    // everyone starts holding it.
    private const string DemoCardJson =
        "{\n" +
        "  \"id\": 1000,\n" +
        "  \"name\": \"Disco Ball\",\n" +
        "  \"category\": \"Gun\",\n" +
        "  \"description\": [\"+4 bounces\", \"+2 bullets\", \"-30% damage\"],\n" +
        "  \"mods\": [\n" +
        "    { \"stat\": \"Bounces\", \"op\": \"add\", \"value\": 4 },\n" +
        "    { \"stat\": \"Projectiles\", \"op\": \"add\", \"value\": 2 },\n" +
        "    { \"stat\": \"Damage\", \"op\": \"mul\", \"value\": 0.7 }\n" +
        "  ]\n" +
        "}";

    private async Task DemoCustomCards()
    {
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        var layer = new CanvasLayer { Name = "DemoCards", Layer = 15 };
        AddChild(layer);
        var screen = new Control { Theme = UiKit.BuildTheme(), MouseFilter = Control.MouseFilterEnum.Ignore };
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(screen);
        var scrim = new ColorRect { Color = new Color(0.03f, 0.03f, 0.05f, 0.88f), MouseFilter = Control.MouseFilterEnum.Ignore };
        scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(scrim);

        // The editor window: a title bar with the file's name over the text, sized to sit in the middle square.
        var mono = new SystemFont { FontNames = new[] { "Cascadia Mono", "Consolas", "Courier New", "Monospace" }, Antialiasing = TextServer.FontAntialiasing.Gray };
        var editor = new PanelContainer { Size = new Vector2(530, 300) };
        editor.Position = (GetViewport().GetVisibleRect().Size - editor.Size) / 2;
        editor.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("#16141f"), BorderColor = new Color("#3a3550"),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14, ContentMarginTop = 10, ContentMarginRight = 14, ContentMarginBottom = 14,
        });
        screen.AddChild(editor);
        var box = UiKit.VBox(10);
        editor.AddChild(box);
        box.AddChild(UiKit.Label("cards / disco-ball.json", UiKit.Small, Palette.TextDim, font: Fx.Fonts.Bold));
        var code = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.Off, CustomMinimumSize = new Vector2(500, 0) };
        code.AddThemeFontOverride("normal_font", mono);
        code.AddThemeFontSizeOverride("normal_font_size", 13);
        code.Text = DemoHighlight(DemoCardJson);
        code.VisibleCharacters = 0;
        box.AddChild(code);

        await DemoWait(0.8);
        DemoMark("typing");
        int total = code.GetTotalCharacterCount();
        for (double t = 0; code.VisibleCharacters < total; t += 1 / 60.0)
        {
            code.VisibleCharacters = Mathf.Min(total, (int)(t * 70));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        DemoMark("typed");
        await DemoWait(1.6);

        // Saved into the cards folder: the game reads it the same way it reads the folder at startup.
        var log = new System.Collections.Generic.List<string>();
        var cards = CustomCards.Collect(new[] { ("disco-ball.json", DemoCardJson) }, log);
        if (cards.Count != 1) { Finish(1, $"demo customcards: the card didn't parse ({string.Join("; ", log)})"); return; }
        CardRegistry.SetCustom(cards);
        editor.Visible = false;
        // The card at twice its size, its middle on the screen's (it sets its own Y as it rises into view).
        var view = new CardView(cards[0], 0, 0) { Locked = true, Chosen = true };
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Scale = Vector2.One * 2 };
        holder.Position = GetViewport().GetVisibleRect().Size / 2 - view.CustomMinimumSize;
        holder.AddChild(view);
        screen.AddChild(holder);
        DemoMark("card");
        await DemoWait(3.2);

        layer.QueueFree();
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        _game.StartingCards = new() { cards[0].Id };
        await DemoWait(0.6);
        DemoMark("match");
        _game.HostStartMatch();
        await DemoWait(20.0);
        DemoMark("end");
        _game.StartingCards = null;
    }

    // The map editor: an arena built from empty with a drawn pointer (blocks, a gap, spawns, pads, conveyor belts with
    // crates on them, a sawblade), named, saved, and played against bots.
    private async Task DemoEditor()
    {
        var ed = _editor;
        var layer = new CanvasLayer { Name = "DemoPointer", Layer = 25 };
        AddChild(layer);
        var pointer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(500, 300), Scale = Vector2.One * 1.3f };
        bool pressed = false;
        pointer.Draw += () =>
        {
            var arrow = new[] { Vector2.Zero, new Vector2(0, 17), new Vector2(4.5f, 13), new Vector2(8, 20), new Vector2(10.5f, 19), new Vector2(7, 12), new Vector2(12.5f, 12) };
            pointer.DrawColoredPolygon(arrow, pressed ? new Color("#f2d58a") : Colors.White);
            var loop = new Vector2[arrow.Length + 1];
            arrow.CopyTo(loop, 0);
            loop[^1] = arrow[0];
            pointer.DrawPolyline(loop, new Color("#150a24"), 1.4f, true);
        };
        layer.AddChild(pointer);

        var canvas = new Rect2(MapEditorUI.Corner, new Vector2(MapLayout.Width, MapLayout.Height) * MapEditorUI.Zoom);
        async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        void Feed(InputEvent e) { if (e is InputEventMouse m && ed.IsVisibleInTree() && canvas.HasPoint(pointer.Position)) { m.Position = pointer.Position - MapEditorUI.Corner; ed.Feed(e); } }
        async Task GlideTo(Vector2 to, double s)
        {
            var from = pointer.Position;
            int n = Mathf.Max(1, (int)(s * 60));
            for (int i = 1; i <= n; i++)
            {
                float k = i / (float)n;
                pointer.Position = from.Lerp(to, k * k * (3 - 2 * k));
                Feed(new InputEventMouseMotion());
                await Frame();
            }
        }
        Task Glide(float x, float y, double s = 0.35) => GlideTo(MapEditorUI.Corner + new Vector2(x, y) * MapEditorUI.Zoom, s);
        void Button(bool down, MouseButton b = MouseButton.Left)
        {
            pressed = down;
            pointer.QueueRedraw();
            Feed(new InputEventMouseButton { ButtonIndex = b, Pressed = down });
        }
        async Task Click(float x, float y)
        {
            await Glide(x, y);
            Button(true);
            for (int i = 0; i < 5; i++) await Frame();
            Button(false);
            await DemoWait(0.2);
        }
        async Task Drag(float x1, float y1, float x2, float y2, double s = 0.5)
        {
            await Glide(x1, y1);
            Button(true);
            await Glide(x2, y2, s);
            Button(false);
            await DemoWait(0.2);
        }
        async Task PressButton(Node root, string text)
        {
            var b = FindButton(root, text);
            await GlideTo(b.GlobalPosition + b.Size / 2, 0.4);
            pressed = true;
            pointer.QueueRedraw();
            for (int i = 0; i < 5; i++) await Frame();
            pressed = false;
            pointer.QueueRedraw();
            b.EmitSignal(BaseButton.SignalName.Pressed);
            await DemoWait(0.15);
        }
        async Task Tool(string label, EditTool tool)
        {
            await PressButton(ed, label);
            ed.SetTool(tool);
        }

        DemoMark("menu");
        await DemoWait(0.6);
        await PressButton(_menu, "Map Editor");
        DemoMark("editor");
        await DemoWait(1.6);
        await PressButton(ed, "Empty");
        ed.Load(MapDesign.Empty(ThemeId.SunsetPeaks, 1));
        await DemoWait(0.5);

        DemoMark("blocks");
        await Tool("Block", EditTool.Block);
        await Drag(112, 472, 272, 480);
        await Drag(728, 472, 888, 480);
        await Drag(424, 350, 576, 358);
        await Tool("Gap", EditTool.Gap);
        await Drag(464, 545, 536, 545);

        DemoMark("pieces");
        await Tool("Spawn", EditTool.Spawn);
        await Click(192, 400);
        await Click(808, 400);
        await Click(56, 500);
        await Click(944, 500);
        await Tool("Pad", EditTool.Pad);
        await Click(368, 500);
        await Click(632, 500);
        await Tool("Saw", EditTool.Saw);
        await Drag(392, 260, 608, 260);

        DemoMark("belts");
        await Tool("Belt", EditTool.Belt);
        await Drag(112, 505, 296, 505, 0.7);
        await Drag(888, 505, 704, 505, 0.7);
        await DemoWait(0.4);
        await Click(800, 528); // turned around…
        await DemoWait(0.5);
        await Click(800, 528); // …and back
        await DemoWait(0.3);
        await Tool("Crate", EditTool.Crate);
        await Click(152, 480);
        await Click(848, 480);
        await Glide(500, 470, 0.5);
        await DemoWait(0.8);

        DemoMark("name");
        var name = FindChild<LineEdit>(ed, l => l.PlaceholderText.Contains("Name"));
        await GlideTo(name.GlobalPosition + new Vector2(60, name.Size.Y / 2), 0.4);
        const string arena = "Belt Factory";
        for (int i = 1; i <= arena.Length; i++)
        {
            name.Text = arena[..i];
            name.EmitSignal(LineEdit.SignalName.TextChanged, name.Text);
            await DemoWait(0.06);
        }
        await PressButton(ed, "Save");
        await DemoWait(0.4);
        var code = FindChild<LineEdit>(ed, l => l.Editable == false && l.Text.Length > 20) ?? FindChild<LineEdit>(ed, l => l != name && l.Text.Length > 20);
        if (code != null) await GlideTo(code.GlobalPosition + new Vector2(code.Size.X * 0.4f, code.Size.Y / 2), 0.5);
        await PressButton(ed, "Copy");
        DemoMark("code");
        await DemoWait(1.4);
        if (ed.Design.Problems() is { Count: > 0 } problems) { Finish(1, $"demo editor: the arena has problems: {string.Join(" | ", problems)}"); return; }

        await PressButton(ed, "PLAYTEST");
        layer.QueueFree();
        await DemoWait(0.4);
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        _net.Lobby.Settings.BattlesPerRound = 3;
        await DemoWait(0.3);
        DemoMark("play");
        _game.HostStartMatch();
        await DemoWait(16.0);
        DemoMark("end");
    }

    /// <summary>JSON with editor colors: keys, strings and numbers.</summary>
    private static string DemoHighlight(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json.Replace("[", "[lb]"), @"(""[^""]*"")(\s*:)?|(-?\d+(\.\d+)?)", m =>
            m.Groups[1].Success
                ? (m.Groups[2].Success ? $"[color=#8ec7ff]{m.Groups[1].Value}[/color]{m.Groups[2].Value}" : $"[color=#f2d58a]{m.Groups[1].Value}[/color]")
                : $"[color=#c9a0ff]{m.Value}[/color]");

    // Every look: everything earned, then each part of the Customize screen stepped all the way round (colors a few
    // swatches each), Randomize a few times, and a battle to show the trails off.
    private async Task DemoWardrobe()
    {
        _game.DisableKillCam = true;
        _game.AutopilotLocal = true;
        CareerBook.Set(new Career { Matches = 40, Wins = 15, Kills = 250, BestWave = 6, GunGameWins = 1 });
        CareerBook.Enabled = true;
        Press(_menu, "Customize Character");
        await DemoWait(1.5);
        DemoMark("editor");
        var grid = FindChild<GridContainer>(_custom, _ => true);
        foreach (var row in grid.GetChildren().OfType<HBoxContainer>().ToList())
        {
            var name = FindAll<Label>(row, _ => true).Select(l => l.Text).FirstOrDefault() ?? "?";
            DemoMark($"row {name}");
            if (FindButton(row, ">") is { } next)
            {
                // All the way round, back to where it started (the value is the row's last label).
                string Value() => FindAll<Label>(row, l => l.IsVisibleInTree()).Select(l => l.Text).LastOrDefault();
                var start = Value();
                for (int i = 0; i < 40; i++)
                {
                    next.EmitSignal(BaseButton.SignalName.Pressed);
                    await DemoWait(0.38);
                    if (Value() == start) break;
                }
            }
            else
            {
                var rng = new Rng((ulong)name.Length * 977);
                for (int i = 0; i < 4; i++)
                {
                    var dots = row.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).ToList();
                    if (dots.Count == 0) break;
                    dots[rng.Range(0, dots.Count - 1)].EmitSignal(BaseButton.SignalName.Pressed);
                    await DemoWait(0.45);
                }
            }
            await DemoWait(0.3);
        }
        DemoMark("randomize");
        for (int i = 0; i < 5; i++)
        {
            Press(_custom, "Randomize");
            await DemoWait(0.9);
        }
        await DemoWait(1.0);
        Press(_custom, "DONE");
        await DemoWait(0.6);
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 6) _net.AddBot();
        await DemoWait(1.5);
        DemoMark("fight");
        _game.HostStartMatch();
        await DemoWait(18.0);
        DemoMark("end");
    }

    // Daily Challenge: the menu line with today's mode and cards, the match against three Hard bots, the result, and
    // the best of the day back on the menu.
    private async Task DemoDaily()
    {
        _game.AutopilotLocal = true;
        CareerBook.Enabled = true;
        await DemoWait(1.0);
        DemoMark("menu");
        await DemoWait(3.0);
        Press(_menu, "Daily Challenge");
        DemoMark("match");
        _game.KillCamStarted += () => DemoMark("killcam");
        for (double t = 0; _game.Phase != Phase.MatchOver && t < 240; t += 0.1) await DemoWait(0.1);
        DemoMark("over");
        await DemoWait(5.0);
        Press(_over, "Leave");
        await DemoWait(1.5);
        DemoMark("best");
        await DemoWait(4.0);
        DemoMark("end");
    }

    // Training Range: hold a few cards from the browser, put it away and shoot the dummies; then another build.
    private async Task DemoTraining()
    {
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        await DemoWait(0.8);
        Press(_menu, "Training Range");
        await DemoWait(2.0);
        void Tab()
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, Pressed = false });
        }
        foreach (var build in new[] { new[] { "Bouncy", "Splitter", "Explosive" }, new[] { "Shotgun", "Homing", "Big Bullet" } })
        {
            DemoMark("browser");
            Press(_training, "Clear Cards");
            await DemoWait(0.8);
            foreach (var card in build)
            {
                Press(_training, card);
                DemoMark($"hold {card}");
                await DemoWait(1.0);
            }
            await DemoWait(0.8);
            Tab();
            DemoMark("shoot");
            await DemoWait(11.0);
            Tab();
            await DemoWait(0.8);
        }
        DemoMark("end");
    }

    // End-of-match stats: a quick match against five bots, the results, then the Stats tab.
    private async Task DemoStats()
    {
        _game.AutopilotLocal = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 6) _net.AddBot();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 2, RoundsToWin = 1 });
        await DemoWait(1.0);
        DemoMark("match");
        _game.HostStartMatch();
        while (_game.Phase != Phase.MatchOver) await DemoWait(0.1);
        DemoMark("results");
        await DemoWait(3.5);
        _over.ShowStats();
        DemoMark("stats");
        await DemoWait(7.0);
        DemoMark("end");
    }

    // Card icons: every built-in card, a page at a time, then a match up to its first two card picks.
    private async Task DemoCardIcons()
    {
        var layer = new CanvasLayer { Layer = 50 };
        AddChild(layer);
        layer.AddChild(new ColorRect { Color = new Color(0.05f, 0.03f, 0.1f), Size = GetViewport().GetVisibleRect().Size });
        var cards = CardRegistry.All;
        const int perPage = 24;
        for (int page = 0; page * perPage < cards.Count; page++)
        {
            var grid = new GridContainer { Columns = 8 };
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 8);
            foreach (var c in cards.Skip(page * perPage).Take(perPage))
            {
                var view = new CardView(c, 0, 0) { MouseFilter = Control.MouseFilterEnum.Ignore };
                var cell = new Control { CustomMinimumSize = view.CustomMinimumSize };
                cell.AddChild(view);
                grid.AddChild(cell);
            }
            var size = GetViewport().GetVisibleRect().Size;
            float fit = Mathf.Min((size.X - 24) / (8 * 140f), (size.Y - 24) / (3 * 196f));
            var holder = new Control { Scale = Vector2.One * fit, Position = new Vector2(12, 12) };
            holder.AddChild(grid);
            layer.AddChild(holder);
            DemoMark($"page {page + 1}");
            await DemoWait(4.0);
            holder.QueueFree();
        }
        layer.QueueFree();
        _game.AutopilotLocal = true;
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        await DemoWait(0.8);
        DemoMark("match");
        _game.HostStartMatch();
        int picks = 0;
        for (double t = 0; picks < 2 && t < 120; t += 0.1)
        {
            bool picking = _game.Phase == Phase.CardPick;
            await DemoWait(0.1);
            if (!picking && _game.Phase == Phase.CardPick) DemoMark($"pick {++picks}");
        }
        await DemoWait(4.0);
        DemoMark("end");
    }

    // Portals: a wall down the middle with a pair of portals through it, and another pair between two ledges. First
    // the player shoots into the near ring and the bullets come out of the far one into the bots; then everyone fights,
    // running through to get at each other.
    private async Task DemoPortals()
    {
        _game.DisableKillCam = true;
        _game.LavaOverride = 0;
        var d = MapDesign.Empty(ThemeId.NeonNight, 3);
        d.Name = "Portal Demo";
        d.Blocks.Add(new RectI(490, MapGenerator.Wall, 20, MapGenerator.FloorY - MapGenerator.Wall));
        d.Blocks.Add(new RectI(120, 400, 160, 12));
        d.Blocks.Add(new RectI(720, 400, 160, 12));
        int ground = MapGenerator.FloorY - PortalRing.Half;
        d.Portals.Add(new PortalPair(new PortalRing(420, ground, 2), new PortalRing(900, ground, 2)));
        d.Portals.Add(new PortalPair(new PortalRing(200, 400 - PortalRing.Half, 0), new PortalRing(800, 400 - PortalRing.Half, 2)));
        d.Spawns.Add(new SpawnPoint(150, MapGenerator.FloorY));
        d.Spawns.Add(new SpawnPoint(640, MapGenerator.FloorY));
        d.Spawns.Add(new SpawnPoint(760, MapGenerator.FloorY));
        d.Spawns.Add(new SpawnPoint(330, MapGenerator.FloorY));
        MapLibrary.LobbyMaps = new() { MapCode.Encode(d) };
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(0.6);
        while (_net.Lobby.Players.Count < 4) _net.AddBot();
        _net.SetSettings(new MatchSettings { KillCam = false, LavaAfter = 0, RoundsToWin = 5, Maps = { MapCode.Pack(d) } });
        await DemoWait(1.0);
        DemoMark("match");
        _game.HostStartMatch();
        while (_game.Phase != Phase.Fighting) await DemoWait(0.1);
        // The player stands left of the near ring and fires into its front; the bots stand still on the far side.
        var me = _game.LocalMatchPlayer;
        me.Input = new RingShooter(new Vector2(420, ground - 4));
        foreach (var a in _game.LiveAvatars())
        {
            bool mine = a == me.Avatar;
            a.GlobalPosition = new Vector2(mine ? 160 : 560 + 70 * a.Player.Slot, MapGenerator.FloorY - a.HalfSize.Y);
            a.Velocity = Vector2.Zero;
            if (!mine) a.Player.Input = new StillInput();
        }
        DemoMark("shoot");
        for (double t = 0; t < 7; t += 0.1)
        {
            if (me.Avatar is { Alive: true } av) av.Hp = av.S[Stat.MaxHp];
            await DemoWait(0.1);
        }
        DemoMark("fight");
        _game.AutopilotLocal = true;
        foreach (var p in _game.Slots.Where(p => p != null)) p.Input = new BotBrain(_game);
        await DemoWait(35.0);
        DemoMark("end");
    }

    /// <summary>Stands still, aims at a point and fires in bursts.</summary>
    private sealed class RingShooter : IInputSource
    {
        private readonly Vector2 _at;
        private int _n;
        public RingShooter(Vector2 at) => _at = at;
        public InputFrame Next(Avatar self, float dt) => new() { Aim = _at, Shoot = _n++ % 20 < 10 };
    }
}
