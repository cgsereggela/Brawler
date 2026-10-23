using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Brawler.Net;
using Brawler.UI;
using Godot;

namespace Brawler;

/// <summary>
/// Root of the game. Builds every system in code and switches between screens.
///
/// The automated test modes (command-line flags for tests, benchmarks and screenshots) are in Main.CommandLine.cs,
/// with the checks they run in Main.Checks.cs and the screenshot and benchmark modes in Main.Shots.cs.
/// </summary>
public partial class Main : Node
{
    private NetworkManager _net;
    private GameManager _game;
    private Sfx _sfx;
    private Music _music;
    private OptionsUI _options;
    private Control _optionsReturn;
    private bool _overlay; // options open over a running match (Esc), rather than as its own screen
    private MenuUI _menu;
    private LobbyUI _lobby;
    private HudUI _hud;
    private Crosshair _crosshair;
    private CardPickUI _pick;
    private MatchOverUI _over;
    private CustomizeUI _custom;
    private Control _customReturn;
    private MapEditorUI _editor;
    private bool _playtest; // the offline match came from the editor's Playtest, so leaving goes back there

    public override void _Ready()
    {
        ErrorWatch.Install(); // first, so the check modes see every error the run logs
        RunSeed.Init(); // before the GameManager makes its host rolls
        LocalInput.RegisterActions();
        Pads.RegisterMenuButtons();
        Keybinds.Init();
        AudioSettings.Init();
        DisplaySettings.Init();
        // Before anything rolls or names a card (--cards= included).
        var cardDir = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--carddir="));
        CustomCardLoader.Load(cardDir?["--carddir=".Length..].Replace('\\', '/'));

        // Bloom: anything drawn brighter than 1.0 (HDR 2D) glows.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Canvas,
            GlowEnabled = true,
            GlowIntensity = 0.9f,
            GlowStrength = 1.1f,
            GlowBloom = 0.0f,
            GlowHdrThreshold = 1.0f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
        };
        for (int i = 0; i < 7; i++) env.SetGlowLevel(i, i is >= 1 and <= 4 ? 1f : 0f);
        BloomSettings.Init(env);
        AddChild(new WorldEnvironment { Environment = env });

        // Animated scenery behind the menus (the arena has its own).
        var bgLayer = new CanvasLayer { Name = "MenuBackdrop", Layer = -10 };
        AddChild(bgLayer);
        _menuBackdrop = new Backdrop { Name = "Backdrop" };
        bgLayer.AddChild(_menuBackdrop);
        _menuBackdrop.Set(ArenaTheme.All[0], 7);

        _net = new NetworkManager { Name = "Net" };
        AddChild(_net);
        (_net.LocalName, _net.LocalColor, _net.LocalLook) = Profile.Load();
        CareerBook.Load();
        _net.LocalLook = Unlocks.Earned(CareerBook.Current, _net.LocalLook); // only what this player has earned goes out
        _sfx = new Sfx { Name = "Sfx" };
        AddChild(_sfx);
        _music = new Music { Name = "Music" };
        AddChild(_music);
        _game = new GameManager { Name = "Game", Net = _net, Sfx = _sfx };
        AddChild(_game);

        // Lay the world (and the menu backdrop) on painted canvas; the UI above stays crisp.
        var paint = new CanvasLayer { Name = "Paint", Layer = 5 };
        AddChild(paint);
        paint.AddChild(PaintShaders.ScreenPass());

        var ui = new CanvasLayer { Name = "UI", Layer = 10 };
        AddChild(ui);
        _uiRoot = new Control { Name = "Root", Theme = UiKit.BuildTheme() };
        var root = _uiRoot;
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        ui.AddChild(root);
        ApplyUiScale();
        GetViewport().SizeChanged += ApplyUiScale;
        Accessibility.Changed += ApplyUiScale;

        _menu = new MenuUI(_net) { Name = "Menu" };
        _lobby = new LobbyUI(_net) { Name = "Lobby" };
        _hud = new HudUI(_game) { Name = "Hud" };
        _pick = new CardPickUI(_game) { Name = "CardPick" };
        _over = new MatchOverUI(_game, _net) { Name = "MatchOver" };
        _custom = new CustomizeUI(_net) { Name = "Customize" };
        _options = new OptionsUI(_sfx) { Name = "Options" };
        _editor = new MapEditorUI(_game) { Name = "MapEditor", Visible = false };
        foreach (var c in Screens) root.AddChild(c);

        // Above every screen: the crosshair that stands in for the mouse pointer while you fight. The normal cursor
        // comes back for the card pick, the match-over screen, the Esc overlay and everything outside a match.
        var aimLayer = new CanvasLayer { Name = "Aim", Layer = 20 };
        AddChild(aimLayer);
        _crosshair = new Crosshair(_game)
        {
            Name = "Crosshair",
            ShouldShow = () => _hud.Visible && !_pick.Visible && !_over.Visible && !_options.Visible && !_game.KillCam.Playing,
        };
        aimLayer.AddChild(_crosshair);

        Wire();
        KillCamClips.Attach(_game);
        Show(_menu);
        HandleCommandLine();
        CareerBook.Enabled = _autoMode == null; // test runs earn nothing (a demo turns it back on with its own career)
    }

    private Control _uiRoot;

    /// <summary>
    /// Every screen lives under one root control: at a bigger UI scale (Accessibility) it is laid out in a smaller
    /// space and drawn scaled up to fill the window, so the menus and HUD grow but still fit.
    /// </summary>
    private void ApplyUiScale()
    {
        if (_uiRoot == null || !IsInstanceValid(_uiRoot)) return;
        float s = Accessibility.UiScale;
        _uiRoot.Position = Vector2.Zero;
        _uiRoot.Scale = Vector2.One * s;
        _uiRoot.Size = GetViewport().GetVisibleRect().Size / s;
    }

    private void Wire()
    {
        _menu.PlayBots += () =>
        {
            SaveProfile();
            _net.StartOffline();
            LoadCardPool();
            _net.AddBot();
            Show(_lobby);
        };
        _menu.HostLan += () =>
        {
            SaveProfile();
            if (!_net.Host(_menu.Port)) return;
            LoadCardPool();
            Show(_lobby);
        };
        _menu.JoinLan += () =>
        {
            SaveProfile();
            // A streamer-mode invite code carries its own address and port.
            var (host, port) = InviteCode.TryDecode(_menu.AddressEdit.Text, out var codeHost, out var codePort)
                ? (codeHost, codePort) : NetAddress.Parse(_menu.AddressEdit.Text, _menu.Port);
            Profile.SaveLastAddress(_menu.AddressEdit.Text.Trim());
            _net.Join(host, port);
        };
        _menu.Customize += () => OpenCustomize(_menu);
        _menu.MapEditor += () => Show(_editor);
        _menu.Daily += StartDaily;
        _game.MatchEnded += DailyMatchEnded;
        _menu.SetDaily(DailyLine());
        _editor.Closed += () => Show(_menu);
        _editor.Playtest += PlaytestArena;
        _lobby.CustomizePressed += () => OpenCustomize(_lobby);
        _custom.Closed += () => Show(_customReturn == _lobby && _net.Mode != NetMode.None && !_net.InMatch ? _lobby : _menu);
        _menu.Options += () => OpenOptions(_menu);
        _lobby.OptionsPressed += () => OpenOptions(_lobby);
        _options.Closed += () =>
        {
            if (_overlay) CloseOverlay();
            else Show(_optionsReturn == _lobby && _net.Mode != NetMode.None && !_net.InMatch ? _lobby : _menu);
        };
        _options.LeaveRequested += () => _net.Leave();

        _net.Status += _menu.SetStatus;
        _net.LobbyChanged += () =>
        {
            if (_net.Mode == NetMode.Client && !_net.InMatch && _menu.Visible) Show(_lobby);
        };
        _net.SessionEnded += reason =>
        {
            _game.ResetWorld();
            DailyEnded();
            Show(_playtest ? _editor : _menu);
            _playtest = false;
            _menu.SetStatus(reason);
            if (_autoMode != null && !_dropping) Finish(_matchFinished ? 0 : 1, $"session ended: {reason}");
        };

        _lobby.StartPressed += () => _game.HostStartMatch();
        _lobby.LeavePressed += () => _net.Leave();

        _over.BackToLobby += () => _game.HostReturnToLobby();
        _over.LeavePressed += () => _net.Leave();

        _game.MatchBegan += () => Show(_hud);
        _game.BattleStarted += () => Show(_hud);
        _game.BattleStarted += () => _battleFrames = 0;
        _game.CardPickBegan += () => Show(_hud, _pick);
        _game.MatchEnded += OnMatchEnded;
        _game.ReturnedToLobby += () => Show(_lobby);
    }

    private Control[] Screens => new Control[] { _menu, _lobby, _hud, _pick, _over, _custom, _options, _editor };

    private void OpenCustomize(Control from)
    {
        _customReturn = from;
        Show(_custom);
    }

    private void OpenOptions(Control from)
    {
        _optionsReturn = from;
        _options.SetOverlay(false);
        Show(_options);
    }

    /// <summary>Options over the match, which keeps running underneath; only your own input is muted.</summary>
    private void OpenOverlay()
    {
        if (_overlay || !_hud.Visible) return;
        _overlay = true;
        _options.SetArena(_game.ArenaCode);
        _options.SetOverlay(true);
        _options.Visible = true;
        LocalInput.Suppressed = true;
    }

    private void CloseOverlay()
    {
        _overlay = false;
        _options.Visible = false;
        _options.SetOverlay(false);
        LocalInput.Suppressed = false;
    }

    // Esc in a match opens the overlay. Unhandled only, so an Esc taken by the Controls tab's key capture (or the
    // open overlay closing itself) never reaches here. Test modes never open it, except --overlaycheck on purpose.
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        if (_overlay || !_hud.Visible || (_autoMode != null && !_overlayCheck)) return;
        OpenOverlay();
        GetViewport().SetInputAsHandled();
    }

    // ---------- controllers ----------

    /// <summary>Synthetic Esc presses (Start, or B in menus) carry this device id, so they don't count as keyboard use.</summary>
    private const int PadEscDevice = 77;

    public override void _Process(double delta)
    {
        Pads.Poll();
        PadMenus();
    }

    public override void _Input(InputEvent e)
    {
        if (e.Device == PadEscDevice) return;
        Pads.Note(e);
        if (e is InputEventMouseMotion m && m.Relative.LengthSquared() > 4 || e is InputEventKey { Pressed: true } || e is InputEventMouseButton { Pressed: true })
            Pads.KeyboardUsed();
    }

    /// <summary>
    /// Start works as Esc everywhere (the options overlay in a match), and B goes back in menus. With nothing focused,
    /// the first press of the d-pad, stick or A puts the focus on the screen's first button, so menus can be driven
    /// from the couch; from there the engine's own ui_* actions (which include the pad) move it.
    /// </summary>
    private void PadMenus()
    {
        if (_autoMode != null && !_padCheck) return;
        // The map editor reads the controller itself while its arena cursor is out (B deletes there, A places).
        if (_editor.Visible && !_editor.PadMenus) return;
        bool inMenus = !_hud.Visible || _overlay;
        bool nav = false;
        foreach (int d in Pads.Devices)
        {
            if (Pads.JustPressed(d, PadLayout.Start) || (inMenus && Pads.JustPressed(d, PadLayout.B))) PressEscFromPad();
            nav |= Pads.JustPressedAny(d, MenuButtons) || (Pads.State(d).Move != System.Numerics.Vector2.Zero && !_padStickHeld.Contains(d));
            if (Pads.State(d).Move != System.Numerics.Vector2.Zero) _padStickHeld.Add(d);
            else _padStickHeld.Remove(d);
        }
        if (!nav || GetViewport().GuiGetFocusOwner() is { } owner && owner.IsVisibleInTree()) return;
        // In a match only the card pick and the screens over it take the focus; the fight itself is not a menu.
        if (_hud.Visible && !_overlay && !_pick.Visible && !_over.Visible) return;
        FirstFocusable(FocusRoot())?.GrabFocus();
    }

    private static readonly int[] MenuButtons = { PadLayout.A, PadLayout.DpadUp, PadLayout.DpadDown, PadLayout.DpadLeft, PadLayout.DpadRight };
    private readonly System.Collections.Generic.HashSet<int> _padStickHeld = new();

    private void PressEscFromPad()
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = down, Device = PadEscDevice });
    }

    /// <summary>The screen a controller should land on: whatever is on top.</summary>
    private Control FocusRoot()
    {
        if (_options.Visible) return _options;
        if (_custom.Visible) return _custom;
        if (_editor.Visible) return _editor;
        if (_over.Visible) return _over;
        if (_pick.Visible) return _pick;
        if (_lobby.Visible) return _lobby.FocusRoot;
        return _menu;
    }

    /// <summary>
    /// Where a controller starts on a screen: its main call to action (a Primary button, like PLAY VS BOTS), else the
    /// first card, else the first button, else anything visible and enabled that takes focus.
    /// </summary>
    private static Control FirstFocusable(Control root)
    {
        Control best = null;
        int bestRank = int.MaxValue;
        void Walk(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is not Control ctl || !ctl.Visible) continue;
                if (ctl.FocusMode == Control.FocusModeEnum.All && ctl is not BaseButton { Disabled: true })
                {
                    int rank = ctl is Button b && b.ThemeTypeVariation == "Primary" ? 0 : ctl is CardView ? 1 : ctl is BaseButton ? 2 : 3;
                    if (rank < bestRank) (best, bestRank) = (ctl, rank);
                }
                Walk(ctl);
            }
        }
        Walk(root);
        return best;
    }

    private void SaveProfile() => Profile.Save(_net.LocalName, _net.LocalColor, _net.LocalLook);

    /// <summary>The editor's Playtest: an offline lobby with a bot, playing only this arena. Leaving comes back to the editor.</summary>
    private void PlaytestArena(MapDesign design)
    {
        SaveProfile();
        _net.StartOffline();
        LoadCardPool();
        var s = _net.Lobby.Settings.Copy();
        s.Maps = new() { MapCode.Pack(design) };
        _net.SetSettings(s);
        _net.AddBot();
        _playtest = true;
        Show(_lobby);
    }

    /// <summary>A new lobby starts from the card pool this player last hosted with.</summary>
    private void LoadCardPool()
    {
        var s = _net.Lobby.Settings;
        s.DisabledCards = CardPoolSettings.Load();
        s.Maps = MapLibrary.LobbyMaps.Select(c => MapCode.Pack(MapCode.Decode(c))).ToList();
        _net.SetSettings(s);
    }

    private void Show(params Control[] screens)
    {
        bool inArena = screens.Contains(_hud);
        // Leaving the arena (back to the lobby or menu) closes the overlay; screen changes inside the match keep it on top.
        if (_overlay && !inArena) CloseOverlay();
        foreach (var c in Screens)
            c.Visible = screens.Contains(c) || (c == _options && _overlay);
        // The menu scenery shows whenever no arena is on screen, with a new theme each time you come back.
        if (!inArena && !_menuBackdrop.Visible)
            _menuBackdrop.Set(ArenaTheme.All[(int)(GD.Randi() % (uint)ArenaTheme.All.Length)], (int)GD.Randi());
        _menuBackdrop.Visible = !inArena;
        _music.Play(inArena ? "battle" : "lobby");
    }

    private Backdrop _menuBackdrop;

    private void OnMatchEnded(int winner)
    {
        Show(_hud, _over);
        _matchFinished = true;
        if (_autoMode != null) TestMatchEnded(winner);
    }
}
