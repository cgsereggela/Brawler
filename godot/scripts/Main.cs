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
/// Automated test modes (pass after "--" on the command line):
///   --autotest            offline match, local player + 3 bots (--players=N: N players) on autopilot; exits 0 when the match ends
///   --autohost[=port]     host a LAN game with 1 bot, autopilot, start once a client joins
///   --autojoin[=ip:port]  join a LAN game on autopilot
///                         (all three take some cards out of the pool and fail if a pick offers one; the joiner also
///                         checks it received the host's disabled list)
///   --simulate=N          play N bot-only matches back to back (2–8 players) to fill the card stats log; exits 0
///   --players=A[-B]       player count (or range) per simulated match, default 2-8; the screenshot modes and --autotest
///                         take the top of it as their player count (default 4)
///   --statsdir=PATH       where card_stats.jsonl goes (default user://stats)
///   --battlecap=SECS      (with --simulate) abandon a battle after SECS simulated seconds; logs STALL lines with bot state
///   --killcamskip         (with --autotest/--autohost/--autojoin) ask to skip each kill cam half a second in
///   --stats               also record stats during --autotest/--autohost (off by default so tests don't pollute the log)
///   --arenashots          (with --shots=DIR) screenshot battles on seeds rich in crates, pools, saws and movers
///   --terrainshots        (with --shots=DIR) screenshot battles on hilly seeds, one per map style and per arena theme
///   --hudshots            (with --shots=DIR, and --cards=) screenshot each player chip's hover card (cards and stats)
///   --readmeshots         (with --shots=DIR, run with --fixed-fps 60) the README media: a match with generated names,
///                         a GIF clip + screenshot per battle and card pick, hover cards and the match-over screen, then quit
///   --aimshots=DIR        screenshot avatars with the widest faces aiming every way (barrel vs aim line, Muzzle dot,
///                         some with full recoil) into DIR, then quit
///   --crosshairshots=DIR  screenshot the crosshair in a range of ammo/reload states over every arena theme, then quit
///   --walkshots=DIR       screenshot leg poses (walk cycle frames, idle, air, wall slide, landing, ramps) into DIR, then quit
///   --lookshots=DIR       screenshot every eye, mouth, hair and hat option (some firing, some blinking) into DIR, then quit;
///                         --lookscale=S draws them at size S (default 3.2; 1 is their size in a match)
///   --upnpcheck           look for a UPnP router and print its public address (read-only, forwards nothing), then quit
///   --cards=ID,ID,...     (offline) every player starts each battle holding these cards; --cards=random:N deals N random
///                         cards to each player every battle, to exercise the whole card pool
///   --carddir=DIR         load custom cards from DIR instead of user://cards (so tests leave the real folder alone)
///   --overlaycheck        (with --autotest) presses Esc mid-battle, checks the game keeps running under the options
///                         overlay with local input muted until a card pick, tries Leave match then No, closes it with
///                         Esc, then finishes the match; also checks the crosshair replaces the pointer only while
///                         you fight (not under the overlay or the card pick)
///   --lava=SECS           sudden-death lava rises after SECS seconds of fighting (0 = off) in the matches this run hosts
///   --lavacheck           (with --autotest, --autohost or --autojoin) lava after 3 s; checks it rises on every peer, that
///                         nobody survives long in it, and (with --shots=DIR) screenshots it once a battle
///   --rarechance=PCT      percent chance that each offered card is rare, in the matches this run hosts
///   --teams=N             team mode for the matches this run hosts: N players a side (2, 3 or 4); --friendlyfire turns friendly fire on
///   --mode=ID             game mode for the matches this run hosts: elimination, koth, ctf or headhunter (ctf without --teams plays even teams)
///   --rounds=N            round wins to take the match, for the matches this run hosts (a shorter test match)
///   --streamer            streamer mode on for this run only (not saved); with --menushots, shows the hosted lobby hiding addresses
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

    private string _autoMode;
    private bool _matchFinished;
    private long _frames;
    private const long AutotestMaxFrames = 60L * 60 * 30; // 30 simulated minutes per match

    public override void _Ready()
    {
        LocalInput.RegisterActions();
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
        var root = new Control { Name = "Root", Theme = UiKit.BuildTheme() };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        ui.AddChild(root);

        _menu = new MenuUI(_net) { Name = "Menu" };
        _lobby = new LobbyUI(_net) { Name = "Lobby" };
        _hud = new HudUI(_game) { Name = "Hud" };
        _pick = new CardPickUI(_game) { Name = "CardPick" };
        _over = new MatchOverUI(_game, _net) { Name = "MatchOver" };
        _custom = new CustomizeUI(_net) { Name = "Customize" };
        _options = new OptionsUI(_sfx) { Name = "Options" };
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
        Show(_menu);
        HandleCommandLine();
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
            Show(_menu);
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

    private Control[] Screens => new Control[] { _menu, _lobby, _hud, _pick, _over, _custom, _options };

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

    private void SaveProfile() => Profile.Save(_net.LocalName, _net.LocalColor, _net.LocalLook);

    /// <summary>A new lobby starts from the card pool this player last hosted with.</summary>
    private void LoadCardPool()
    {
        var s = _net.Lobby.Settings;
        s.DisabledCards = CardPoolSettings.Load();
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
        if (_autoMode == "sim") SimMatchEnded(winner);
        else if (_rematchTest && _rematches == 0 && _autoMode is "offline" or "host" or "join") AutoVoteRematch();
        else if (_autoMode is not (null or "readme")) // readme quits after its match-over shot
        {
            var t = GetTree().CreateTimer(_autoMode == "host" ? 2.0 : 0.5, true, false, true);
            t.Timeout += () =>
            {
                if (_overlayCheck && _ocStage != OcDone) Finish(1, $"overlaycheck: match ended at check stage {_ocStage}");
                else if (_poolCheck && _poolPicks == 0 && _net.TestRounds != 1) Finish(1, "poolcheck: the match ended without a card pick"); // --rounds=1 has no pick
                else if (_dropAt > 0 && !_rejoinChecked) Finish(1, $"droptest: the match ended without a rejoin");
                else if (ChatCheckProblem() is { } chat) Finish(1, chat);
                else if (ComboCheckProblem() is { } combo) Finish(1, combo);
                else if (LavaCheckProblem() is { } lava) Finish(1, lava);
                else Finish(0, $"match finished, winner slot {winner} | {_game.ScoreLine()}");
            };
        }
    }

    // ---------- automated test modes ----------

    private void ParseDebugCards(string spec)
    {
        if (spec.StartsWith("random:") && int.TryParse(spec["random:".Length..], out int n))
        {
            _game.DebugCards = slot =>
            {
                var hand = new System.Collections.Generic.List<int>();
                for (int i = 0; i < n; i++) hand.Add(CardRegistry.All[(int)(GD.Randi() % (uint)CardRegistry.All.Count)].Id);
                Log.Info($"P{slot} cards: {string.Join(", ", hand.Select(id => CardRegistry.Get(id).Name))}");
                return hand;
            };
            return;
        }
        var ids = spec.Split(',').Select(s => int.TryParse(s, out int id) ? id : -1).Where(id => CardRegistry.Get(id) != null).ToList();
        _game.DebugCards = _ => new System.Collections.Generic.List<int>(ids);
    }

    private void HandleCommandLine()
    {
        var args = OS.GetCmdlineUserArgs();
        bool forceStats = false;
        foreach (var a in args)
        {
            if (a.StartsWith("--shots=")) _shotDir = a["--shots=".Length..];
            else if (a == "--streamer") StreamerMode.SetForRun(true);
            else if (a.StartsWith("--lava=") && int.TryParse(a["--lava=".Length..], out int lava)) _game.LavaOverride = lava;
            else if (a.StartsWith("--rarechance=") && int.TryParse(a["--rarechance=".Length..], out int rare)) _game.RareOverride = Mathf.Clamp(rare, 0, 100);
            else if (a.StartsWith("--teams=") && int.TryParse(a["--teams=".Length..], out int teams)) _net.TestTeamSize = Mathf.Clamp(teams, 0, 4);
            else if (a == "--friendlyfire") _net.TestFriendlyFire = true;
            else if (a.StartsWith("--mode=")) _net.TestMode = GameModes.Parse(a["--mode=".Length..]);
            else if (a.StartsWith("--rounds=") && int.TryParse(a["--rounds=".Length..], out int rounds)) _net.TestRounds = Mathf.Clamp(rounds, 1, 10);
            else if (a == "--lavacheck") { _lavaCheck = true; if (_game.LavaOverride < 0) _game.LavaOverride = 3; }
            else if (a.StartsWith("--battlecap=") && float.TryParse(a["--battlecap=".Length..], System.Globalization.CultureInfo.InvariantCulture, out float cap)) _battleCap = cap;
            else if (a.StartsWith("--statsdir=")) _game.Stats.Dir = a["--statsdir=".Length..].Replace('\\', '/');
            else if (a == "--stats") forceStats = true;
            else if (a == "--overlaycheck") _overlayCheck = true;
            else if (a == "--killcamskip") _game.KillCamStarted += () => GetTree().CreateTimer(0.5, true, false, true).Timeout += _game.SkipKillCam;
            else if (a.StartsWith("--dropat=") && int.TryParse(a["--dropat=".Length..], out int drop)) _dropAt = drop;
            else if (a == "--nobots") _noBots = true;
            else if (a == "--nofxcull") Fx.FxLayer.Culling = false;
            else if (a == "--rematch") _rematchTest = true;
            else if (a.StartsWith("--cards=")) ParseDebugCards(a["--cards=".Length..]);
            else if (a.StartsWith("--players="))
            {
                var r = a["--players=".Length..].Split('-');
                _playersArg = true;
                if (int.TryParse(r[0], out int lo)) _simMin = _simMax = lo;
                if (r.Length > 1 && int.TryParse(r[1], out int hi)) _simMax = hi;
                _simMin = Mathf.Clamp(_simMin, 2, LobbyState.MaxPlayers);
                _simMax = Mathf.Clamp(_simMax, _simMin, LobbyState.MaxPlayers);
            }
        }
        if (args.Any(a => a is "--autotest" || a.StartsWith("--autohost") || a.StartsWith("--autojoin")))
            _game.Stats.Enabled = forceStats;
        if (_overlayCheck) _game.CardPickBegan += () => _ocSawPick = true;
        if (_shotDir != null && !args.Contains("--readmeshots")) // readme names its own shots
        {
            _game.CardPickBegan += () => GetTree().CreateTimer(0.3).Timeout += SaveShot;
            _game.MatchEnded += _ =>
            {
                GetTree().CreateTimer(0.3, true, false, true).Timeout += () =>
                {
                    SaveShot();
                    // Then with a hover card open, which must draw over the match-over screen.
                    _hud.DebugHover = 0;
                    GetTree().CreateTimer(0.1, true, false, true).Timeout += () => { SaveShot(); _hud.DebugHover = -1; };
                };
            };
            // The kill cam's build-up, its slow-motion kill and the aftermath (real seconds: it slows game time).
            _game.KillCamStarted += () =>
            {
                foreach (double at in new[] { 0.7, 1.9, 2.8 })
                    GetTree().CreateTimer(at, true, false, true).Timeout += () => { if (_game.KillCam.Playing) SaveShot(); };
            };
        }

        foreach (var a in args)
        {
            if (a == "--autotest")
            {
                _autoMode = "offline";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.LocalName = "Auto";
                _net.StartOffline();
                AddTestBots();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 2, RoundsToWin = 3, LoserChoices = 5, WinnerChoices = 3, DisabledCards = new(AutoDisabled) });
                var roundTrip = LobbyState.Deserialize(_net.Lobby.Serialize()).Settings.DisabledCards;
                if (!roundTrip.SetEquals(AutoDisabled)) { Finish(1, $"poolcheck: lobby round trip gave [{string.Join(",", roundTrip)}]"); return; }
                StartPoolCheck();
                StartChatCheck();
                Log.Info("AUTOTEST offline starting");
                _game.HostStartMatch();
            }
            else if (a.StartsWith("--autohost"))
            {
                _autoMode = "host";
                int port = a.Contains('=') && int.TryParse(a.Split('=')[1], out int p) ? p : NetworkManager.DefaultPort;
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.LocalName = "AutoHost";
                _net.UseUpnp = false;
                if (!_net.Host(port)) { Finish(1, "could not host"); return; }
                if (!_noBots) _net.AddBot();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 2, LoserChoices = 3, WinnerChoices = 2, DisabledCards = new(AutoDisabled) });
                StartPoolCheck();
                StartChatCheck();
                _net.LobbyChanged += AutoHostMaybeStart;
            }
            else if (a.StartsWith("--autojoin"))
            {
                _autoMode = "join";
                string target = a.Contains('=') ? a.Split('=')[1] : $"127.0.0.1:{NetworkManager.DefaultPort}";
                var parts = target.Split(':');
                int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : NetworkManager.DefaultPort;
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.LocalName = "AutoJoin";
                StartPoolCheck();
                StartChatCheck();
                _net.LobbyChanged += AutoJoinCheckPool;
                (_joinHost, _joinPort) = (parts[0], port);
                if (_dropAt > 0) _game.BattleStarted += AutoDropMaybe;
                _net.Join(parts[0], port);
            }
            else if (a.StartsWith("--simulate"))
            {
                _autoMode = "sim";
                _simTotal = a.Contains('=') && int.TryParse(a.Split('=')[1], out int n) ? Mathf.Max(1, n) : 20;
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _game.Stats.Mode = "sim";
                Log.Info($"SIMULATE {_simTotal} matches, {_simMin}-{_simMax} players → {ProjectSettings.GlobalizePath(_game.Stats.FilePath)}");
                SimNextMatch();
            }
            else if (a == "--themebench")
            {
                // Average frame time for each arena theme (windowed, vsync off), with bots fighting. Prints and quits.
                _autoMode = "bench";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
                _net.StartOffline();
                AddTestBots();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
                _game.HostStartMatch();
                var tree = GetTree();
                int theme = 0;
                void Next()
                {
                    if (theme >= ArenaTheme.All.Length) { tree.Quit(0); return; }
                    int seed = 1;
                    while (ArenaTheme.ForSeed(seed) != ArenaTheme.All[theme]) seed++;
                    _game.MapNode.Build(MapGenerator.Generate(seed, 4));
                    var name = ArenaTheme.All[theme].Name;
                    theme++;
                    tree.CreateTimer(0.5, true, false, true).Timeout += () =>
                    {
                        ulong t0 = Time.GetTicksUsec(), f0 = Engine.GetProcessFrames();
                        tree.CreateTimer(3.0, true, false, true).Timeout += () =>
                        {
                            double ms = (Time.GetTicksUsec() - t0) / 1000.0 / Mathf.Max(1, Engine.GetProcessFrames() - f0);
                            Log.Info($"BENCH {name,-13} {ms:0.00} ms/frame ({1000 / ms:0} fps)");
                            Next();
                        };
                    };
                }
                tree.CreateTimer(3.0, true, false, true).Timeout += Next;
            }
            else if (a == "--fxbench")
            {
                // Floods the arena with top-tier hit and kill effects for 8 s (windowed, vsync off), printing frame
                // time and live particles each second, then a FXBENCH summary. Compare with --nofxcull.
                _autoMode = "bench";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
                _net.StartOffline();
                _net.AddBot();
                _net.AddBot();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
                _game.HostStartMatch();
                var tree = GetTree();
                var all = Game.Flavor.Fire | Game.Flavor.Homing | Game.Flavor.Vampire | Game.Flavor.Glass | Game.Flavor.Feather | Game.Flavor.Bouncy | Game.Flavor.Split | Game.Flavor.Pierce;
                double total = 0, worst = 0;
                int seconds = 0, peak = 0;
                void Burst()
                {
                    if (seconds >= 8) return;
                    _game.DebugShowcase(12, all);
                    tree.CreateTimer(0.04, true, false, true).Timeout += Burst;
                }
                void Second()
                {
                    ulong t0 = Time.GetTicksUsec(), f0 = Engine.GetProcessFrames();
                    tree.CreateTimer(1.0, true, false, true).Timeout += () =>
                    {
                        double ms = (Time.GetTicksUsec() - t0) / 1000.0 / Mathf.Max(1, Engine.GetProcessFrames() - f0);
                        int live = _game.Fx.ParticleCount;
                        peak = Mathf.Max(peak, live);
                        total += ms;
                        worst = Mathf.Max(worst, ms);
                        seconds++;
                        Log.Info($"fxbench {seconds}s: {ms:0.00} ms/frame, {live} particles, cap {_game.Fx.Budget.Cap} (baseline {_game.Fx.Budget.Baseline * 1000:0.0} ms)");
                        if (seconds < 8) { Second(); return; }
                        Log.Info($"FXBENCH culling {(Fx.FxLayer.Culling ? "on" : "off")}: average {total / seconds:0.00} ms/frame, worst second {worst:0.00} ms, peak {peak} particles");
                        tree.Quit(0);
                    };
                }
                tree.CreateTimer(2.6).Timeout += () => { Burst(); Second(); };
            }
            else if (a == "--fxshots" && _shotDir != null)
            {
                // Screenshot hit and kill effects at every power tier (0 to 12 cards, flavors piling up), then quit.
                _autoMode = "fx";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.StartOffline();
                _net.AddBot();
                _net.AddBot();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 5, RoundsToWin = 5 });
                _game.HostStartMatch();
                var tree = GetTree();
                var tiers = new (int Power, Game.Flavor Flavors)[]
                {
                    (0, Game.Flavor.None), (2, Game.Flavor.Bouncy), (4, Game.Flavor.Fire | Game.Flavor.Homing),
                    (6, Game.Flavor.Fire | Game.Flavor.Vampire | Game.Flavor.Pierce), (9, Game.Flavor.Glass | Game.Flavor.Split | Game.Flavor.Homing),
                    (12, Game.Flavor.Fire | Game.Flavor.Homing | Game.Flavor.Vampire | Game.Flavor.Glass | Game.Flavor.Feather | Game.Flavor.Bouncy),
                };
                int k = 0;
                void Step()
                {
                    if (k >= tiers.Length) { tree.Quit(0); return; }
                    _game.DebugShowcase(tiers[k].Power, tiers[k].Flavors);
                    k++;
                    tree.CreateTimer(0.14, true, false, true).Timeout += () =>
                    {
                        SaveShot();
                        tree.CreateTimer(0.25, true, false, true).Timeout += SaveShot;
                        tree.CreateTimer(1.4, true, false, true).Timeout += Step;
                    };
                }
                tree.CreateTimer(2.6).Timeout += Step;
            }
            else if (a == "--arenashots" && _shotDir != null)
            {
                // Screenshot battles on seeds rich in arena features (pools, saws, movers, glass, barrels), then quit.
                _autoMode = "arena";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.StartOffline();
                AddTestBots();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
                _game.HostStartMatch();
                var seeds = Enumerable.Range(1, 400)
                    .Select(s => (Seed: s, M: MapGenerator.Generate(s, 4)))
                    .OrderByDescending(x => x.M.Hazards.Count * 2 + x.M.Saws.Count * 2 + x.M.Movers.Count * 2
                        + (x.M.Props.Any(p => p.Kind == PropKind.Glass) ? 2 : 0) + x.M.Props.Count(p => p.Kind == PropKind.Barrel))
                    .Take(5).Select(x => x.Seed).ToList();
                var tree = GetTree();
                int k = 0;
                void Next()
                {
                    if (k >= seeds.Count) { tree.Quit(0); return; }
                    _game.DebugStartBattle(seeds[k++]);
                    tree.CreateTimer(3.5, true, false, true).Timeout += () => { SaveShot(); Next(); };
                }
                tree.CreateTimer(1.0, true, false, true).Timeout += Next;
            }
            else if (a == "--layoutshots" && _shotDir != null)
            {
                // Screenshot battles on the first two seeds of each map style (MAPSHOT lines name them), then quit.
                _autoMode = "arena";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.StartOffline();
                AddTestBots();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 99, RoundsToWin = 9 });
                _game.HostStartMatch();
                var seeds = System.Enum.GetValues<MapStyle>()
                    .SelectMany(st => Enumerable.Range(1, 400).Where(s => MapGenerator.StyleFor(s) == st).Take(2)).ToList();
                var tree = GetTree();
                int k = 0;
                void Next()
                {
                    if (k >= seeds.Count) { tree.Quit(0); return; }
                    string name = $"layout_{k:D2}_{MapGenerator.StyleFor(seeds[k])}";
                    Log.Info($"MAPSHOT {name} seed {seeds[k]} {ArenaTheme.ForSeed(seeds[k]).Name}");
                    _game.DebugStartBattle(seeds[k++]);
                    tree.CreateTimer(3.5, true, false, true).Timeout += () => { SaveShot(name); Next(); };
                }
                tree.CreateTimer(1.0, true, false, true).Timeout += Next;
            }
            else if (a == "--hudshots" && _shotDir != null)
            {
                // A battle with 4 players (or --players=) holding cards (use --cards=), opening each player chip's hover card in turn.
                _autoMode = "arena";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.StartOffline();
                AddTestBots();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
                _game.HostStartMatch();
                var tree = GetTree();
                int k = 0;
                void Next()
                {
                    if (k >= _net.Lobby.Players.Count) { tree.Quit(0); return; }
                    _hud.DebugHover = k++;
                    tree.CreateTimer(0.3, true, false, true).Timeout += () => { SaveShot(); Next(); };
                }
                tree.CreateTimer(3.0, true, false, true).Timeout += Next;
            }
            else if (a == "--terrainshots" && _shotDir != null)
            {
                // Screenshot a battle on the hilliest seed of each arena theme (one per map style first), then quit.
                _autoMode = "arena";
                _game.AutopilotLocal = true;
                _game.DisableHitStop = true;
                _net.StartOffline();
                AddTestBots();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
                _game.HostStartMatch();
                var maps = Enumerable.Range(1, 600).Select(s => (Seed: s, M: MapGenerator.Generate(s, 4))).Where(x => x.M.Slopes.Count > 0).ToList();
                var seeds = ArenaTheme.All.Select(th => maps.Where(x => ArenaTheme.ForSeed(x.Seed) == th)
                        .OrderByDescending(x => x.M.Slopes.Count + x.M.Terrain.Count).Select(x => x.Seed).FirstOrDefault())
                    .Where(s => s > 0).ToList();
                foreach (MapStyle st in System.Enum.GetValues<MapStyle>())
                    if (maps.FirstOrDefault(x => x.M.Style == st).Seed is int s and > 0 && !seeds.Contains(s)) seeds.Insert(0, s);
                var tree = GetTree();
                int k = 0;
                void Next()
                {
                    if (k >= seeds.Count) { tree.Quit(0); return; }
                    var m = MapGenerator.Generate(seeds[k], 4);
                    Log.Info($"TERRAINSHOT seed {seeds[k]} {m.Style} {ArenaTheme.ForSeed(seeds[k]).Name}: {m.Slopes.Count} slopes, {m.Terrain.Count} raised");
                    _game.DebugStartBattle(seeds[k++]);
                    tree.CreateTimer(3.5, true, false, true).Timeout += () => { SaveShot(); Next(); };
                }
                tree.CreateTimer(1.0, true, false, true).Timeout += Next;
            }
            else if (a == "--readmeshots" && _shotDir != null)
            {
                // README media: a 4-player match with generated names, played out naturally. Each battle gets a GIF clip
                // and a screenshot (plus a hover card once cards are held), each card pick a clip and a screenshot, and
                // the match-over screen a screenshot. Run with --fixed-fps 60 so clips play at game speed.
                _autoMode = "readme";
                _game.AutopilotLocal = true;
                _pick.ShowAutopilotPick = true;
                var rng = new Rng((ulong)System.DateTime.Now.Ticks);
                var names = FunNames.Pick(rng, _playersArg ? _simMax : 4);
                _net.LocalName = names[0];
                _net.LocalLook = Appearance.Random(rng);
                _net.StartOffline();
                for (int i = 1; i < names.Count; i++) _net.AddBot();
                for (int i = 1; i < names.Count; i++) _net.Lobby.Players[i].Name = names[i];
                _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 4, LoserChoices = 5, WinnerChoices = 3 });
                var tree = GetTree();
                int battle = 0, pick = 0;
                _game.BattleStarted += () =>
                {
                    int n = ++battle;
                    tree.CreateTimer(0.4, true, false, true).Timeout += () => RecordClip($"battle_{n:D2}", 6.0);
                    tree.CreateTimer(4.0, true, false, true).Timeout += () => { if (_game.Phase == Phase.Fighting) SaveShot($"battle_{n:D2}"); };
                    if (n < 3) return;
                    tree.CreateTimer(6.6, true, false, true).Timeout += () =>
                    {
                        if (_game.Phase != Phase.Fighting) return;
                        _hud.DebugHover = 0;
                        tree.CreateTimer(0.2, true, false, true).Timeout += () => { SaveShot($"hover_{n:D2}"); _hud.DebugHover = -1; };
                    };
                };
                _game.CardPickBegan += () =>
                {
                    int n = ++pick;
                    RecordClip($"pick_{n:D2}", 2.5);
                    tree.CreateTimer(2.0, true, false, true).Timeout += () => SaveShot($"pick_{n:D2}");
                };
                _game.MatchEnded += _ => tree.CreateTimer(3.0, true, false, true).Timeout += () => { SaveShot("match_over"); tree.Quit(0); };
                _game.HostStartMatch();
            }
            else if (a.StartsWith("--aimshots="))
            {
                // Avatars aiming in 8 directions, then 4 near-vertical aims with the body still facing the other way
                // (the facing hysteresis). Red dot on the aim line, cyan dot at Muzzle(). Screenshot, then quit.
                _shotDir = a["--aimshots=".Length..];
                Show();
                var canvas = new Control();
                canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                _menu.GetParent().AddChild(canvas);
                var gun = GunLook.Default;
                // Faces that reach furthest (visor, big eyes, shades, mustache) to check the gun clears them.
                Appearance Face(int k)
                {
                    var look = Appearance.Default();
                    look[LookPart.Eyes] = new[] { 1, 5, 7, 4 }[k / 4 % 4];
                    look[LookPart.Mouth] = new[] { 7, 3, 6 }[k % 3];
                    return look;
                }
                canvas.Draw += () =>
                {
                    const float s = 2.4f;
                    for (int k = 0; k < 12; k++)
                    {
                        var aim = k < 8 ? Vector2.Right.Rotated(k * Mathf.Pi / 4) : Vector2.Up.Rotated(Mathf.DegToRad(new[] { -8, 8, 172, 188 }[k - 8]));
                        int facing = k < 8 ? (aim.X < -0.01f ? -1 : 1) : (aim.X < 0 ? 1 : -1);
                        var at = canvas.Size * new Vector2((k % 4 + 0.5f) / 4, 0.2f + 0.3f * (k / 4));
                        canvas.DrawLine(at, at + aim * 70, new Color(1, 1, 1, 0.3f), 1);
                        canvas.DrawCircle(at + aim * 70, 4, Colors.Red);
                        AvatarArt.Draw(canvas, at, new AvatarPose
                        {
                            Body = Palette.Player(k % 4), Look = Face(k), Facing = facing, Aim = aim,
                            Scale = s, Grounded = true, Gun = true, GunLook = gun, Flash = k % 2 == 0 ? 1 : 0, Kick = k % 3 == 0 ? 1 : 0,
                        });
                        canvas.DrawCircle(at + AvatarArt.Muzzle(aim, facing, s, gun), 2.5f, Colors.Cyan);
                    }
                };
                GetTree().CreateTimer(0.5).Timeout += () => { SaveShot(); GetTree().Quit(0); };
            }
            else if (a.StartsWith("--crosshairshots="))
            {
                // The crosshair in a range of ammo and reload states, at real size and zoomed 3x, over every arena
                // theme's scenery and platform color. One screenshot per theme, then quit.
                _shotDir = a["--crosshairshots=".Length..];
                Show();
                var canvas = new Control();
                canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                canvas.MouseFilter = Control.MouseFilterEnum.Ignore;
                _menu.GetParent().AddChild(canvas);
                var states = new (int Ammo, int Mag, bool Reloading, float Frac, float Kick, float Ready, bool Armed)[]
                {
                    (3, 3, false, 0, 0, 0, true), (2, 3, false, 0, 0.8f, 0, true), (1, 3, false, 0, 0, 0, true),
                    (0, 3, true, 0.3f, 0, 0, true), (0, 3, true, 0.8f, 0, 0, true), (3, 3, false, 0, 0, 0.6f, true),
                    (1, 1, false, 0, 0, 0, true), (7, 12, false, 0, 0, 0, true), (23, 40, false, 0, 0, 0, true), (0, 3, false, 0, 0, 0, false),
                };
                int theme = 0;
                canvas.Draw += () =>
                {
                    var th = ArenaTheme.All[theme];
                    var vs = canvas.Size;
                    // A platform band so each state is seen over both the sky and a block.
                    canvas.DrawRect(new Rect2(0, vs.Y * 0.62f, vs.X, vs.Y * 0.14f), th.Block);
                    canvas.DrawRect(new Rect2(0, vs.Y * 0.62f, vs.X, 2), Shapes.Hdr(th.Edge, 1.3f));
                    canvas.DrawString(Brawler.Fx.Fonts.Bold, new Vector2(12, 24), th.Name, HorizontalAlignment.Left, -1, 14, Colors.White);
                    for (int k = 0; k < states.Length; k++)
                    {
                        var st = states[k];
                        var cs = new CrosshairState
                        {
                            Tint = Palette.Player(k % Palette.Players.Length), Armed = st.Armed, Ammo = st.Ammo, Mag = st.Mag,
                            Reloading = st.Reloading, ReloadFrac = st.Frac, Kick = st.Kick, Ready = st.Ready, Time = 0,
                        };
                        float x = vs.X * (k + 0.5f) / states.Length;
                        Crosshair.Paint(canvas, new Vector2(x, vs.Y * 0.62f), cs);
                        // Zoomed copy above, over the sky.
                        canvas.DrawSetTransform(new Vector2(x, vs.Y * 0.3f), 0, new Vector2(2.2f, 2.2f));
                        Crosshair.Paint(canvas, Vector2.Zero, cs);
                        canvas.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
                        Crosshair.Paint(canvas, new Vector2(x, vs.Y * 0.88f), cs);
                    }
                };
                var tree = GetTree();
                void Next()
                {
                    _menuBackdrop.Set(ArenaTheme.All[theme], 3 + theme);
                    canvas.QueueRedraw();
                    tree.CreateTimer(0.4).Timeout += () =>
                    {
                        SaveShot();
                        if (++theme >= ArenaTheme.All.Length) tree.Quit(0);
                        else Next();
                    };
                }
                tree.CreateTimer(0.3).Timeout += Next;
            }
            else if (a.StartsWith("--walkshots="))
            {
                // Leg poses on a ground line with the hitbox outlined: 8 frames of the walk cycle, then idle, walking
                // backwards, rise / apex / fall, wall slides, a landing squash, and ramps. Screenshot, then quit.
                _shotDir = a["--walkshots=".Length..];
                Show();
                var canvas = new Control();
                canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                _menu.GetParent().AddChild(canvas);
                var hat = Appearance.Default();
                hat[LookPart.Hat] = 2;
                var longHair = Appearance.Default();
                longHair[LookPart.Hair] = 4;
                longHair[LookPart.Mouth] = 6;
                var poses = new System.Collections.Generic.List<(string Name, AvatarPose Pose, float Slope, int Wall)>();
                AvatarPose P(int k) => new()
                {
                    Body = Palette.Player(k % 4), Look = Appearance.Default(), Facing = 1, Aim = Vector2.Right, Scale = 5f,
                    Time = 0.4f + k * 0.37f, Grounded = true, Gun = true, GunLook = GunLook.Default, MoveDir = 1,
                };
                for (int k = 0; k < 8; k++)
                    poses.Add(($"walk {k * 45}", P(k) with { Walk = 1, Moving = true, WalkPhase = k * Mathf.Pi / 4 }, 0, 0));
                poses.Add(("idle", P(0), 0, 0));
                poses.Add(("backwards", P(1) with { Walk = 1, Moving = true, MoveDir = -1, WalkPhase = 0.6f }, 0, 0));
                poses.Add(("rise", P(2) with { Grounded = false, Air = 1, VelY = -280 }, 0, 0));
                poses.Add(("apex", P(3) with { Grounded = false, Air = 1, VelY = 0 }, 0, 0));
                poses.Add(("fall", P(0) with { Grounded = false, Air = 1, VelY = 300 }, 0, 0));
                poses.Add(("wall front", P(1) with { Grounded = false, Air = 1, VelY = 40, Wall = 1, WallSide = 1 }, 0, 1));
                poses.Add(("wall behind", P(2) with { Grounded = false, Air = 1, VelY = 40, Wall = 1, WallSide = -1 }, 0, -1));
                poses.Add(("landing", P(3) with { Land = 1 }, 0, 0));
                foreach (float k in new[] { -0.67f, 0.67f })
                {
                    poses.Add(($"ramp {k:+0.00;-0.00} idle", P(0) with { Look = hat, GroundSlope = k, GroundDrop = 7 * Mathf.Abs(k) }, k, 0));
                    poses.Add(($"ramp {k:+0.00;-0.00} walk", P(1) with { Look = longHair, GroundSlope = k, GroundDrop = 7 * Mathf.Abs(k), Walk = 1, Moving = true, WalkPhase = 1.2f }, k, 0));
                }
                poses.Add(("left facing walk", P(2) with { Facing = -1, Aim = Vector2.Left, MoveDir = -1, Walk = 1, Moving = true, WalkPhase = 2f }, 0, 0));
                poses.Add(("giant idle", P(3) with { Scale = 7.5f, Look = longHair }, 0, 0));
                int page = 0;
                canvas.Draw += () =>
                {
                    var font = Brawler.Fx.Fonts.Bold;
                    for (int k = page * 8; k < Mathf.Min(poses.Count, page * 8 + 8); k++)
                    {
                        var (name, pose, slope, wall) = poses[k];
                        float s = pose.Scale;
                        int cell = k % 8;
                        var at = canvas.Size * new Vector2((cell % 4 + 0.5f) / 4, 0.3f + 0.48f * (cell / 4));
                        var hs = new Vector2(Body.W, Body.H) * 0.5f * s;
                        canvas.DrawRect(new Rect2(at - hs, hs * 2), new Color(1, 1, 1, 0.35f), false, 1);
                        if (wall != 0)
                            canvas.DrawRect(new Rect2(at + new Vector2(wall > 0 ? hs.X : -hs.X - 30, -hs.Y - 30), new Vector2(30, hs.Y * 2 + 60)), new Color(0.4f, 0.45f, 0.6f));
                        else
                        {
                            // Ground line: flat at the hitbox bottom, or a ramp touching its uphill corner.
                            float drop = Mathf.Abs(slope) * hs.X;
                            var g0 = at + new Vector2(-60, hs.Y + drop + slope * -60);
                            var g1 = at + new Vector2(60, hs.Y + drop + slope * 60);
                            canvas.DrawColoredPolygon(new[] { g0, g1, g1 + new Vector2(0, 40), g0 + new Vector2(0, 40) }, new Color(0.3f, 0.32f, 0.45f));
                            canvas.DrawLine(g0, g1, new Color(0.8f, 0.85f, 1f), 1.5f);
                        }
                        AvatarArt.Draw(canvas, at, pose);
                        canvas.DrawCircle(at + AvatarArt.BodyOffset(pose) + AvatarArt.TorsoAnchor * s, 2f, Colors.Cyan);
                        canvas.DrawString(font, at + new Vector2(-70, -hs.Y - 60), name, HorizontalAlignment.Left, -1, 18);
                    }
                };
                void NextPage()
                {
                    SaveShot();
                    if (++page * 8 >= poses.Count) { GetTree().Quit(0); return; }
                    canvas.QueueRedraw();
                    GetTree().CreateTimer(0.3).Timeout += NextPage;
                }
                GetTree().CreateTimer(0.5).Timeout += NextPage;
            }
            else if (a.StartsWith("--lookshots="))
            {
                // Every eye, mouth, hair and hat option on a default avatar, a page of 21 at a time (some firing, some
                // mid-blink), to compare the art before and after a drawing change. Screenshot each page, then quit.
                _shotDir = a["--lookshots=".Length..];
                var scaleArg = OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--lookscale="));
                float lookScale = scaleArg != null && float.TryParse(scaleArg["--lookscale=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ls) ? ls : 3.2f;
                Show();
                var canvas = new Control();
                canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                _menu.GetParent().AddChild(canvas);
                var looks = new System.Collections.Generic.List<(string Name, Appearance Look)>();
                void Add(LookPart part, string[] names)
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        var look = Appearance.Default();
                        look[part] = i;
                        if (part == LookPart.Hat) look[LookPart.HatColor] = i;
                        looks.Add(($"{part} {names[i]}", look));
                    }
                }
                Add(LookPart.Eyes, Appearance.EyeNames);
                Add(LookPart.Mouth, Appearance.MouthNames);
                Add(LookPart.Hair, Appearance.HairNames);
                Add(LookPart.Hat, Appearance.HatNames);
                const int perPage = 21;
                int page = 0;
                canvas.Draw += () =>
                {
                    var font = Brawler.Fx.Fonts.Bold;
                    for (int k = page * perPage; k < Mathf.Min(looks.Count, (page + 1) * perPage); k++)
                    {
                        int cell = k % perPage;
                        var at = canvas.Size * new Vector2((cell % 7 + 0.5f) / 7, 0.24f + 0.31f * (cell / 7));
                        AvatarArt.Draw(canvas, at, new AvatarPose
                        {
                            Body = Palette.Player(k % 4), Look = looks[k].Look, Facing = k % 5 == 4 ? -1 : 1,
                            Aim = (k % 5 == 4 ? Vector2.Left : Vector2.Right).Rotated(-0.3f), Scale = lookScale, Time = 0.3f + k * 0.41f,
                            Grounded = true, Gun = true, GunLook = GunLook.Default, Flash = k % 3 == 0 ? 1 : 0, Blink = k % 11 == 10,
                        });
                        canvas.DrawString(font, at + new Vector2(-60, -80), looks[k].Name, HorizontalAlignment.Left, -1, 12);
                    }
                };
                void NextPage()
                {
                    SaveShot();
                    if (++page * perPage >= looks.Count) { GetTree().Quit(0); return; }
                    canvas.QueueRedraw();
                    GetTree().CreateTimer(0.3).Timeout += NextPage;
                }
                GetTree().CreateTimer(0.5).Timeout += NextPage;
            }
            else if (a == "--upnpcheck")
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    string result = PortOpener.Probe();
                    Callable.From(() => { Log.Info($"UPNPCHECK {result}"); GetTree().Quit(0); }).CallDeferred();
                });
            }
            else if (a == "--makeicon")
            {
                _autoMode = "icon";
                Fx.IconMaker.Make(this);
            }
            else if (a == "--menushots" && _shotDir != null)
            {
                // Screenshot the menu, the character editor, an offline lobby with bots, and a gallery
                // of every option of every look part, then quit.
                var tree = GetTree();
                var steps = new System.Collections.Generic.List<System.Action>
                {
                    () => OpenCustomize(_menu),
                    () => OpenOptions(_menu),
                    () =>
                    {
                        _net.StartOffline();
                        AddTestBots();
                        Show(_lobby);
                    },
                    // The same lobby as 2v2 (a column per team, team tags, friendly fire), then the mode button once more for 3v3.
                    () => _net.SetTeamSize(2),
                    () => _net.SetTeamSize(3),
                    () =>
                    {
                        _net.SetSettings(new MatchSettings { DisabledCards = new(AutoDisabled) });
                        _lobby.ShowCardPool(true);
                    },
                    () =>
                    {
                        // Every category's None: the pool stops at its minimum and says why. Saves next to the shots.
                        CardPoolSettings.Path = $"{_shotDir}/cards_settings.cfg";
                        foreach (var b in _lobby.FindChildren("*", "Button", true, false).OfType<Button>().Where(b => b.Text == "None"))
                            b.EmitSignal(BaseButton.SignalName.Pressed);
                        int left = CardPool.EnabledCount(_net.Lobby.Settings.DisabledCards);
                        Log.Info($"MENUSHOTS card pool after None everywhere: {left} enabled, saved [{string.Join(",", CardPoolSettings.Load().OrderBy(id => id))}]");
                    },
                    () =>
                    {
                        _lobby.ShowCardPool(false);
                        _net.UseUpnp = false; // a screenshot run must not touch the router
                        _net.Host(24611);
                        _net.AddBot();
                        Show(_lobby);
                    },
                    () =>
                    {
                        // The Video tab: window, bloom, shake and flashes.
                        OpenOptions(_lobby);
                        foreach (var t in _options.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>()) t.CurrentTab = 1;
                    },
                    () =>
                    {
                        // The Controls tab: every action, emotes and ping included, must fit on screen.
                        OpenOptions(_lobby);
                        foreach (var t in _options.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>()) t.CurrentTab = 2;
                    },
                    () =>
                    {
                        // The Online tab, with the lobby still hosting behind it.
                        OpenOptions(_lobby);
                        foreach (var t in _options.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>()) t.CurrentTab = 3;
                    },
                    () => { Show(_lobby); Log.Info($"MENUSHOTS copy button: {_lobby.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text.StartsWith("Copy"))?.Text}"); },
                    () => { _net.Leave(); Show(_menu); },
                };
                foreach (var part in Appearance.Editable)
                {
                    var p = part;
                    steps.Add(() => ShowGallery(p));
                }
                int i = 0;
                void Next()
                {
                    SaveShot();
                    if (i >= steps.Count) { tree.Quit(0); return; }
                    steps[i++]();
                    tree.CreateTimer(0.4).Timeout += Next;
                }
                tree.CreateTimer(0.5).Timeout += Next;
            }
        }
    }

    // ---------- --simulate: bot-only matches for the card stats log ----------

    private int _simTotal, _simDone, _simMin = 2, _simMax = LobbyState.MaxPlayers;
    private bool _playersArg; // --players= given: also sets how many play in --autotest, --hudshots and the --menushots lobby

    /// <summary>Bots to add beside the local player in the test modes that take --players= (default four players).</summary>
    private void AddTestBots() { for (int i = 1; i < (_playersArg ? _simMax : 4); i++) _net.AddBot(); }

    private readonly Rng _simRng = new((ulong)System.DateTime.Now.Ticks);
    private readonly System.Diagnostics.Stopwatch _simClock = new();
    private long _battleFrames;
    private float _battleCap; // --battlecap=SECS (diagnostics): abandon a single battle that runs longer than this

    /// <summary>One log line per live fighter, for working out why a simulated battle drags on.</summary>
    private void LogStall(string why)
    {
        Log.Info($"STALL {why}: battle {_game.BattleNumber}, {_battleFrames / 60}s, map seed {_game.Map?.Seed}");
        foreach (var a in _game.LiveAvatars())
        {
            var cards = string.Join(",", a.Player.Cards.Select(id => CardRegistry.Get(id)?.Name ?? id.ToString()));
            var bot = a.Player.Input is BotBrain b ? b.DebugState : "";
            Log.Info($"  slot {a.Player.Slot} pos ({a.GlobalPosition.X:0},{a.GlobalPosition.Y:0}) vel ({a.Velocity.X:0},{a.Velocity.Y:0}) hp {a.Hp:0}/{a.S[Stat.MaxHp]:0} floor {a.IsOnFloor()} {bot} cards [{cards}]");
        }
    }

    private void SimNextMatch()
    {
        if (!_simClock.IsRunning) _simClock.Start();
        int players = _simRng.Range(_simMin, _simMax);
        _net.LocalName = "Sim";
        _net.StartOffline();
        for (int i = 1; i < players; i++) _net.AddBot();
        _net.SetSettings(new MatchSettings()); // the lobby defaults: what people actually play
        _game.DisableKillCam = true;
        _frames = 0;
        _matchFinished = false;
        _game.HostStartMatch();
    }

    private void SimMatchEnded(int winner)
    {
        _simDone++;
        double secs = _simClock.Elapsed.TotalSeconds;
        Log.Info($"SIMULATE {_simDone}/{_simTotal} done (winner slot {winner}, {_game.BattleNumber} battles, {secs:0}s elapsed)");
        // Defer: we're inside the BattleOver RPC; tearing the match down right here would pull state from under it.
        Callable.From(() =>
        {
            _game.HostReturnToLobby();
            if (_simDone >= _simTotal) Finish(0, $"simulated {_simDone} matches in {secs:0}s");
            else SimNextMatch();
        }).CallDeferred();
    }

    private void AutoHostMaybeStart()
    {
        if (_net.InMatch || _net.Lobby.Players.Count(p => !p.IsBot) < 2) return;
        _net.LobbyChanged -= AutoHostMaybeStart;
        Log.Info("AUTOTEST host: client joined, starting match");
        GetTree().CreateTimer(1.0).Timeout += () => _game.HostStartMatch();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_autoMode == null) return;
        if (_overlayCheck && _ocStage != OcDone) OverlayCheckStep();
        if (_chatCheck) PollGhosts();
        if (_lavaCheck) PollLava();
        if (_autoMode == "sim" && _game.Phase == Phase.Fighting)
        {
            _battleFrames++;
            if (_battleFrames % (60 * 60) == 0) LogStall("long battle");
            if (_battleCap > 0 && _battleFrames > _battleCap * 60)
            {
                LogStall("battle cap");
                Log.Info("SIMULATE match timed out, abandoning it (saved as incomplete)");
                _frames = 0;
                _battleFrames = 0;
                SimMatchEnded(-1);
                return;
            }
        }
        if (++_frames > AutotestMaxFrames)
        {
            if (_autoMode != "sim") { Finish(1, "timed out before the match ended"); return; }
            LogStall("match timeout");
            Log.Info("SIMULATE match timed out, abandoning it (saved as incomplete)");
            _frames = 0;
            SimMatchEnded(-1);
        }
        if (_shotDir != null && _autoMode != "readme" && _frames % 240 == 0 && _shotCount < 40) SaveShot();
        // And one mid-countdown each battle, where the YOU marker shows.
        bool counting = _game.Phase == Phase.Countdown && _game.CountdownLeft < GameManager.CountdownTime / 2;
        if (_shotDir != null && _autoMode != "readme" && counting && !_countdownShot && _shotCount < 40) SaveShot();
        _countdownShot = counting;
    }

    // ---------- --dropat=N (with --autojoin): leave in battle N, join again, and check the slot came back ----------

    private int _dropAt, _dropCards, _joinPort;
    private bool _noBots, _dropping, _rejoinChecked;
    private bool _countdownShot;
    private string _joinHost;

    private void AutoDropMaybe()
    {
        if (_dropping || _rejoinChecked || _game.BattleNumber != _dropAt) return;
        _dropping = true;
        _dropCards = _game.LocalMatchPlayer?.Cards.Count ?? -1;
        GetTree().CreateTimer(1.5).Timeout += () =>
        {
            Log.Info($"DROPTEST leaving in battle {_dropAt} with {_dropCards} cards");
            _net.Leave();
            _game.MatchBegan += AutoRejoinCheck;
            GetTree().CreateTimer(1.0).Timeout += () => _net.Join(_joinHost, _joinPort);
        };
    }

    private void AutoRejoinCheck()
    {
        _game.MatchBegan -= AutoRejoinCheck;
        _dropping = false;
        var me = _game.LocalMatchPlayer;
        if (me == null || me.Cards.Count != _dropCards)
        {
            Finish(1, $"droptest: rejoined with {me?.Cards.Count ?? -1} cards, had {_dropCards}");
            return;
        }
        _rejoinChecked = true;
        Log.Info($"DROPTEST rejoined slot {me.Slot} with all {_dropCards} cards");
    }

    // ---------- --rematch (with --autotest, --autohost or --autojoin): vote for a rematch, check it starts fresh ----------

    private bool _rematchTest;
    private int _rematches;

    private void AutoVoteRematch()
    {
        // The host votes first and the client a second later, so the host has to wait for the second vote.
        GetTree().CreateTimer(_autoMode == "join" ? 1.5 : 0.5, true, false, true).Timeout += () =>
        {
            Log.Info($"REMATCHTEST voting for a rematch ({_game.RematchVotes.Count} votes in so far)");
            _game.MatchBegan += AutoRematchCheck;
            _game.LocalVoteRematch(true);
        };
    }

    private void AutoRematchCheck()
    {
        _game.MatchBegan -= AutoRematchCheck;
        var stale = _game.Slots.Where(p => p != null && (p.Cards.Count > 0 || p.RoundWins > 0 || p.BattleWins > 0)).ToList();
        if (stale.Count > 0 || _game.RematchVotes.Count > 0)
        {
            Finish(1, $"rematchtest: rematch didn't start fresh: {string.Join(", ", stale.Select(p => p.Info.Name))}, {_game.RematchVotes.Count} votes left");
            return;
        }
        _rematches++;
        Log.Info($"REMATCHTEST rematch started with {_game.Active().Count()} players");
    }

    // ---------- emote, ping and ghost check (--autotest, --autohost, --autojoin) ----------

    private bool _chatCheck;
    private int _myEmotes, _myPings, _otherChat;
    // Each ghost seen (avatars are rebuilt every battle, so keyed by instance): who, how long it was up, how far it went.
    private readonly System.Collections.Generic.Dictionary<ulong, (string Name, float Age, float Travel)> _ghosts = new();

    /// <summary>
    /// Two seconds into every fight, the autopilot emotes (a different one each battle) and pings above its head. Our
    /// own emotes and pings must come back from the host, and every ghost that was up for a second must have moved.
    /// </summary>
    private void StartChatCheck()
    {
        _chatCheck = true;
        StartComboCheck();
        _game.EmoteShown += (slot, _) => { if (slot == _game.LocalMatchPlayer?.Slot) _myEmotes++; else _otherChat++; };
        _game.PingShown += (slot, _) => { if (slot == _game.LocalMatchPlayer?.Slot) _myPings++; else _otherChat++; };
        _game.BattleStarted += () => GetTree().CreateTimer(GameManager.CountdownTime + 2.0, true, false, true).Timeout += () =>
        {
            var me = _game.LocalMatchPlayer?.Avatar;
            if (me == null || _game.Phase != Phase.Fighting) return;
            _game.LocalEmote(_game.BattleNumber % Emotes.All.Length);
            _game.LocalPing(me.GlobalPosition + new Vector2(0, -40));
        };
        // With --shots: a few pictures of a ghost's bubble, once the ghost has risen.
        if (_shotDir != null)
            _game.EmoteShown += (slot, _) =>
            {
                if (_chatShots >= 3 || _game.Slots[slot]?.Avatar is not { Alive: false }) return;
                int n = ++_chatShots;
                GetTree().CreateTimer(Avatar.GhostDelay + 0.3, true, false, true).Timeout += () => SaveShot($"chat_{n}");
            };
    }

    private int _chatShots;

    private void PollGhosts()
    {
        foreach (var p in _game.Slots)
            if (p?.Avatar is { GhostUp: true } a)
                _ghosts[a.GetInstanceId()] = (p.Info.Name, a.GhostAge, a.GhostTravel);
    }

    /// <summary>Null when the check passed (or isn't running); otherwise what went wrong.</summary>
    private string ChatCheckProblem()
    {
        if (!_chatCheck) return null;
        var stuck = _ghosts.Values.Where(g => g.Age > 1f && g.Travel < 10f).ToList();
        Log.Info($"CHATCHECK own emotes {_myEmotes}, own pings {_myPings}, others' {_otherChat}; ghosts {_ghosts.Count}, "
            + $"floated {(_ghosts.Count > 0 ? _ghosts.Values.Average(g => g.Travel) : 0):0} px on average");
        if (_myEmotes == 0) return "chatcheck: our emotes never came back from the host";
        if (_myPings == 0) return "chatcheck: our pings never came back from the host";
        if (_ghosts.Count == 0) return "chatcheck: no ghost ever rose";
        if (stuck.Count > 0) return $"chatcheck: ghosts that never moved: {string.Join(", ", stuck.Select(g => g.Name))}";
        return null;
    }

    // ---------- combo check (with the chat check) ----------

    private readonly System.Collections.Generic.List<(int Slot, ComboDef Combo)> _combosFound = new();

    private void StartComboCheck()
    {
        _game.ComboFound += (slot, combo) => _combosFound.Add((slot, combo));
        _game.MatchBegan += _combosFound.Clear; // a rematch starts everyone with no cards again
        if (_shotDir == null) return;
        // With --shots: an offer with a COMBO tag on it, and the "combo found" panel after our pick.
        _game.CardPickBegan += () =>
        {
            var me = _game.LocalMatchPlayer;
            if (_comboShots >= 4 || me == null || !_game.PickOptions.TryGetValue(me.Slot, out var opts)) return;
            if (!opts.Any(id => Combos.Unlocks(me.Cards, id).Count > 0)) return;
            int n = ++_comboShots;
            GetTree().CreateTimer(0.6, true, false, true).Timeout += () => SaveShot($"combo_offer_{n}");
        };
        _game.ComboFound += (slot, _) =>
        {
            if (_comboShots >= 4 || slot != _game.LocalMatchPlayer?.Slot) return;
            int n = ++_comboShots;
            GetTree().CreateTimer(0.4, true, false, true).Timeout += () => SaveShot($"combo_found_{n}");
        };
    }

    private int _comboShots;

    /// <summary>
    /// Every combo announced must really be active for that player at the end, and every combo a player ends with must
    /// have been announced. Skipped with --cards, which replaces everyone's cards each battle, and after a rejoin.
    /// </summary>
    private string ComboCheckProblem()
    {
        if (!_chatCheck) return null;
        bool dealt = _game.DebugCards != null || _dropAt > 0;
        var players = _game.Slots.Where(p => p != null).ToList();
        int held = players.Sum(p => Combos.Active(p.Cards).Count);
        Log.Info($"COMBOCHECK combos found {_combosFound.Count}, held at the end {held}, bounce blasts {_game.Bullets.BounceBlasts}");
        if (dealt) return null;
        foreach (var (slot, combo) in _combosFound)
            if (_game.Slots[slot] is { } p && !combo.ActiveFor(p.Cards))
                return $"combocheck: {p.Info.Name} was told they found {combo.Name} but their cards don't make it";
        foreach (var p in players)
            foreach (var combo in Combos.Active(p.Cards))
                if (!_combosFound.Contains((p.Slot, combo)))
                    return $"combocheck: {p.Info.Name} holds {combo.Name} but it was never announced";
        return null;
    }

    // ---------- lava check (--lavacheck with --autotest, --autohost, --autojoin) ----------

    private bool _lavaCheck;
    private readonly System.Collections.Generic.HashSet<int> _lavaBattles = new(), _lavaShots = new();
    private readonly System.Collections.Generic.Dictionary<int, float> _inLava = new();
    private float _lavaWorst;

    private void PollLava()
    {
        if (_game.Phase != Phase.Fighting || _game.Map == null) return;
        float y = _game.Arena.LavaY;
        int battle = _game.BattleNumber;
        if (y < MapGenerator.FloorY - 40) _lavaBattles.Add(battle);
        if (_shotDir != null && y < MapGenerator.FloorY - 120 && _lavaShots.Add(battle)) SaveShot($"lava_{battle:D2}");
        // How long each fighter has stood in it without dying: lava that doesn't hurt (or a death that never synced) shows up here.
        foreach (var a in _game.LiveAvatars())
        {
            int slot = a.Player.Slot;
            bool inside = a.GlobalPosition.Y + a.HalfSize.Y - 2 > y;
            float t = inside ? (_inLava.TryGetValue(slot, out float was) ? was : 0) + 1f / 60f : 0;
            _inLava[slot] = t;
            _lavaWorst = Mathf.Max(_lavaWorst, t);
        }
    }

    /// <summary>Null when the check passed (or isn't running); otherwise what went wrong.</summary>
    private string LavaCheckProblem()
    {
        if (!_lavaCheck) return null;
        Log.Info($"LAVACHECK setting {_game.Settings.LavaAfter}s, lava over the floor in {_lavaBattles.Count}/{_game.BattleNumber} battles, longest anyone lived in it {_lavaWorst:0.00}s");
        if (_game.Settings.LavaAfter != 3) return $"lavacheck: this peer got lava after {_game.Settings.LavaAfter}s, not 3";
        if (_lavaBattles.Count == 0) return "lavacheck: the lava never rose over the floor";
        if (_lavaWorst > 2.5f) return $"lavacheck: someone lived {_lavaWorst:0.0}s in the lava";
        return null;
    }

    // ---------- card pool check (--autotest, --autohost, --autojoin) ----------

    // Out of the pool in test matches: a few cards from every category, plus ids no card has (custom cards
    // another machine may have) to show unknown ids pass through.
    private static readonly int[] AutoDisabled = { 1, 2, 3, 17, 30, 43, 55, 56, 57, 58, 59, 60, 82, 92, 1000, 4242 };
    private bool _poolCheck;
    private int _poolPicks;

    private void StartPoolCheck()
    {
        _poolCheck = true;
        _game.CardPickBegan += () =>
        {
            if (!_poolCheck) return;
            _poolPicks++;
            var offered = _game.PickOptions.Values.SelectMany(ids => ids)
                .Concat(_game.PickDeals.Values.SelectMany(d => new[] { d.Rare, d.Curse })).ToList();
            var bad = offered.Where(AutoDisabled.Contains).ToList();
            if (bad.Count > 0) { Finish(1, $"poolcheck: disabled cards offered: {string.Join(",", bad)}"); return; }
            if (_game.PickOptions.Values.Any(ids => ids.Distinct().Count() != ids.Count)) { Finish(1, "poolcheck: an offer repeats a card"); return; }
            Log.Info($"POOLCHECK pick {_poolPicks}: {offered.Count} cards offered, none disabled");
        };
    }

    /// <summary>The client's first lobby must carry the host's disabled list exactly.</summary>
    private void AutoJoinCheckPool()
    {
        if (_net.Lobby.Players.Count == 0) return;
        _net.LobbyChanged -= AutoJoinCheckPool;
        var got = _net.Lobby.Settings.DisabledCards;
        if (!got.SetEquals(AutoDisabled)) { Finish(1, $"poolcheck: client got disabled [{string.Join(",", got.OrderBy(id => id))}]"); return; }
        Log.Info($"POOLCHECK client has the host's disabled list: {string.Join(",", got.OrderBy(id => id))}");
    }

    // ---------- --overlaycheck: the Esc options overlay over a running match ----------

    private bool _overlayCheck, _ocSawPick;
    private int _ocStage;
    private long _ocFrames;
    private float _ocMoved;
    private readonly System.Collections.Generic.Dictionary<int, Vector2> _ocLast = new();
    private const int OcDone = 7;

    private static void PressEsc()
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
    }

    /// <summary>What the local player would send right now with move-right and shoot held down.</summary>
    private InputFrame ProbeHeldInput()
    {
        Input.ActionPress("move_right");
        Input.ActionPress("shoot");
        var f = new LocalInput(_game).Next(null, 1f / 60);
        Input.ActionRelease("move_right");
        Input.ActionRelease("shoot");
        return f;
    }

    private static Button FindButton(Node n, string text)
    {
        foreach (var c in n.GetChildren())
            if (c is Button b && b.Text == text) return b;
            else if (FindButton(c, text) is { } found) return found;
        return null;
    }

    private static CardView FirstCard(Node n)
    {
        foreach (var c in n.GetChildren())
            if (c is CardView cv && cv.IsVisibleInTree()) return cv;
            else if (FirstCard(c) is { } found) return found;
        return null;
    }

    private void OverlayCheckStep()
    {
        void Fail(string why) { _overlayCheck = false; Finish(1, $"overlaycheck: {why}"); }
        bool overlayUp = _overlay && _options.Visible && LocalInput.Suppressed;
        _ocFrames++;
        switch (_ocStage)
        {
            case 0: // a battle well under way: press Esc
                if (_game.Phase != Phase.Fighting) { _ocFrames = 0; return; }
                if (_ocFrames < 60) return;
                var free = ProbeHeldInput();
                if (free.MoveX <= 0 || !free.Shoot) { Fail("held keys don't register even without the overlay"); return; }
                if (!_crosshair.Active) { Fail("no crosshair in a battle"); return; }
                PressEsc();
                (_ocStage, _ocFrames) = (1, 0);
                return;
            case 1: // the overlay is up and your held keys do nothing
                if (!_overlay) { if (_ocFrames > 10) Fail("Esc did not open the overlay"); return; }
                if (!overlayUp) { Fail("overlay flagged open but not shown, or input not suppressed"); return; }
                var muted = ProbeHeldInput();
                if (muted.MoveX != 0 || muted.Shoot) { Fail("held keys still move or shoot under the overlay"); return; }
                Log.Info("OVERLAYCHECK Esc opened the overlay mid-battle; held move+shoot give no input");
                (_ocStage, _ocFrames, _ocMoved) = (2, 0, 0);
                _ocLast.Clear();
                return;
            case 2: // three seconds under the overlay: nothing paused, the bots keep fighting
                if (GetTree().Paused || !Mathf.IsEqualApprox(Engine.TimeScale, 1)) { Fail("the game paused"); return; }
                if (!overlayUp) { Fail("overlay closed by itself during the battle"); return; }
                if (_ocFrames > 2 && _crosshair.Active) { Fail("the crosshair still replaces the pointer under the overlay"); return; }
                foreach (var a in _game.LiveAvatars())
                {
                    if (a.Player == _game.LocalMatchPlayer) continue;
                    if (_ocLast.TryGetValue(a.Player.Slot, out var p)) _ocMoved += a.GlobalPosition.DistanceTo(p);
                    _ocLast[a.Player.Slot] = a.GlobalPosition;
                }
                if (_ocFrames == 60 && _shotDir != null) SaveShot();
                if (_ocFrames < 180) return;
                if (_ocMoved < 100) { Fail($"bots moved only {_ocMoved:0}px in 3s under the overlay"); return; }
                Log.Info($"OVERLAYCHECK game kept running: bots moved {_ocMoved:0}px in 3s, now battle {_game.BattleNumber} {_game.Phase}");
                (_ocStage, _ocFrames, _ocSawPick) = (3, 0, false);
                return;
            case 3: // stays open through battle ends and new battles until a card pick comes up under it
                if (!overlayUp) { Fail($"overlay closed by itself ({_game.Phase})"); return; }
                if (_ocFrames > 60 * 180) { Fail("no card pick within 3 minutes"); return; }
                if (!_ocSawPick || _game.Phase != Phase.CardPick || FirstCard(_pick) is not { } card) return;
                // Hover a card: the overlay, not the card, must be what the mouse is over.
                var at = GetViewport().GetFinalTransform() * card.GetGlobalRect().GetCenter();
                Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
                (_ocStage, _ocFrames) = (4, 0);
                return;
            case 4: // card pick under the overlay: both on screen, overlay on top and catching the mouse
                if (_ocFrames < 6) return;
                if (_game.Phase != Phase.CardPick) { (_ocStage, _ocFrames, _ocSawPick) = (3, 0, false); return; }
                if (!overlayUp || !_pick.Visible || !_hud.Visible) { Fail($"card pick screens wrong: options {_options.Visible} pick {_pick.Visible} hud {_hud.Visible}"); return; }
                var hovered = GetViewport().GuiGetHoveredControl();
                if (hovered == null || !(hovered == _options || _options.IsAncestorOf(hovered))) { Fail($"mouse over a card reaches {hovered?.GetPath().ToString() ?? "nothing"}, not the overlay"); return; }
                Log.Info($"OVERLAYCHECK card pick came up under the overlay; the mouse hits {hovered.Name}, not the cards");
                if (_shotDir != null) SaveShot();
                FindButton(_options, "LEAVE MATCH")?.EmitSignal(BaseButton.SignalName.Pressed);
                (_ocStage, _ocFrames) = (5, 0);
                return;
            case 5: // Leave asks first; No backs out without leaving
                if (_ocFrames < 6) return;
                if (FindButton(_options, "YES") is not { } yes || !yes.IsVisibleInTree() || FindButton(_options, "LEAVE MATCH").IsVisibleInTree())
                { Fail("Leave match did not ask for confirmation"); return; }
                if (_shotDir != null) SaveShot();
                FindButton(_options, "NO").EmitSignal(BaseButton.SignalName.Pressed);
                if (!FindButton(_options, "LEAVE MATCH").IsVisibleInTree() || _net.Mode == NetMode.None) { Fail("No did not back out of leaving"); return; }
                Log.Info("OVERLAYCHECK Leave match asks first; No backs out");
                PressEsc();
                (_ocStage, _ocFrames) = (6, 0);
                return;
            case 6: // Esc closed it; the card pick is still there and input is back
                if (_ocFrames < 6) return;
                if (_overlay || _options.Visible || LocalInput.Suppressed) { Fail("Esc did not close the overlay"); return; }
                if (!_hud.Visible || _pick.Visible != (_game.Phase == Phase.CardPick)) { Fail($"after closing: hud {_hud.Visible} pick {_pick.Visible} in {_game.Phase}"); return; }
                if (_crosshair.Active == _pick.Visible) { Fail($"crosshair {(_crosshair.Active ? "shown" : "hidden")} with the card pick {(_pick.Visible ? "up" : "down")}"); return; }
                Log.Info($"OVERLAYCHECK Esc closed it; {_game.Phase} screens intact, input restored");
                _ocStage = OcDone;
                return;
        }
    }

    // --shots=<dir>: save a screenshot every 4 simulated seconds during an autotest (needs a window, not --headless).
    private string _shotDir;
    private int _shotCount;

    private Control _gallery;

    /// <summary>--menushots: every option of one look part side by side, for checking the art.</summary>
    private void ShowGallery(LookPart part)
    {
        Show();
        _gallery?.QueueFree();
        _gallery = new PanelContainer();
        _gallery.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _menu.GetParent().AddChild(_gallery);
        var v = UiKit.VBox(4);
        _gallery.AddChild(v);
        v.AddChild(UiKit.Label(Appearance.PartLabel(part), 22, Palette.Accent, font: Brawler.Fx.Fonts.Heavy));
        var grid = new GridContainer { Columns = 6 };
        v.AddChild(grid);
        for (int o = 0; o < Appearance.Count(part); o++)
        {
            var look = Appearance.Default();
            look[LookPart.Hat] = 0;
            if (part is LookPart.HairColor) look[LookPart.Hair] = 7;
            if (part is LookPart.HatColor) look[LookPart.Hat] = 1;
            look[part] = o;
            var cell = UiKit.VBox(0);
            cell.AddChild(new AvatarPreview(look, o % Palette.Players.Length, 2.6f) { ShowGun = false });
            var tmp = new Appearance();
            tmp[part] = o;
            cell.AddChild(UiKit.Label(tmp.OptionName(part), UiKit.Small, Palette.Text, HorizontalAlignment.Center));
            grid.AddChild(cell);
        }
    }

    private void SaveShot()
    {
        var img = GetViewport().GetTexture().GetImage();
        string path = $"{_shotDir}/shot_{_shotCount++:D2}_{_game.Phase}.png";
        img.SavePng(path);
    }

    private void SaveShot(string name) => GetViewport().GetTexture().GetImage().SavePng($"{_shotDir}/{name}.png");

    // --readmeshots clips: every 3rd rendered frame (20 fps under --fixed-fps 60), scaled to 640 wide.
    private const int ClipW = 640, ClipH = 360, ClipEvery = 3;

    /// <summary>Records <paramref name="seconds"/> of the screen into DIR/name.gif.</summary>
    private void RecordClip(string name, double seconds)
    {
        var frames = new System.Collections.Generic.List<byte[]>();
        int frame = 0, total = (int)(seconds * 60);
        void Grab()
        {
            if (frame++ % ClipEvery == 0)
            {
                var img = GetViewport().GetTexture().GetImage();
                img.Convert(Image.Format.Rgb8);
                img.Resize(ClipW, ClipH, Image.Interpolation.Lanczos);
                frames.Add(img.GetData());
            }
            if (frame < total) return;
            RenderingServer.FramePostDraw -= Grab;
            System.IO.File.WriteAllBytes($"{_shotDir}/{name}.gif", Gif.Encode(frames, ClipW, ClipH, ClipEvery * 100 / 60));
            Log.Info($"README clip {name}: {frames.Count} frames");
        }
        RenderingServer.FramePostDraw += Grab;
    }

    private void Finish(int code, string why)
    {
        Log.Info($"AUTOTEST {(code == 0 ? "PASS" : "FAIL")}: {why}");
        _autoMode = null;
        GetTree().Quit(code);
    }
}
