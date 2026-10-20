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
///   --autotest            offline match, local player + 3 bots all on autopilot; exits 0 when the match ends
///   --autohost[=port]     host a LAN game with 1 bot, autopilot, start once a client joins
///   --autojoin[=ip:port]  join a LAN game on autopilot
///   --simulate=N          play N bot-only matches back to back (2–4 players) to fill the card stats log; exits 0
///   --players=A[-B]       player count (or range) per simulated match, default 2-4
///   --statsdir=PATH       where card_stats.jsonl goes (default user://stats)
///   --battlecap=SECS      (with --simulate) abandon a battle after SECS simulated seconds; logs STALL lines with bot state
///   --stats               also record stats during --autotest/--autohost (off by default so tests don't pollute the log)
///   --arenashots          (with --shots=DIR) screenshot battles on seeds rich in crates, pools, saws and movers
///   --upnpcheck           look for a UPnP router and print its public address (read-only, forwards nothing), then quit
///   --cards=ID,ID,...     (offline) every player starts each battle holding these cards; --cards=random:N deals N random
///                         cards to each player every battle, to exercise the whole card pool
///   --overlaycheck        (with --autotest) presses Esc mid-battle, checks the game keeps running under the options
///                         overlay with local input muted until a card pick, tries Leave match then No, closes it with
///                         Esc, then finishes the match
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
            _net.AddBot();
            Show(_lobby);
        };
        _menu.HostLan += () =>
        {
            SaveProfile();
            if (_net.Host(_menu.Port)) Show(_lobby);
        };
        _menu.JoinLan += () =>
        {
            SaveProfile();
            var (host, port) = NetAddress.Parse(_menu.AddressEdit.Text, _menu.Port);
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
            if (_autoMode != null) Finish(_matchFinished ? 0 : 1, $"session ended: {reason}");
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
        else if (_autoMode != null)
        {
            var t = GetTree().CreateTimer(_autoMode == "host" ? 2.0 : 0.5, true, false, true);
            t.Timeout += () =>
            {
                if (_overlayCheck && _ocStage != OcDone) Finish(1, $"overlaycheck: match ended at check stage {_ocStage}");
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
            else if (a.StartsWith("--battlecap=") && float.TryParse(a["--battlecap=".Length..], System.Globalization.CultureInfo.InvariantCulture, out float cap)) _battleCap = cap;
            else if (a.StartsWith("--statsdir=")) _game.Stats.Dir = a["--statsdir=".Length..].Replace('\\', '/');
            else if (a == "--stats") forceStats = true;
            else if (a == "--overlaycheck") _overlayCheck = true;
            else if (a.StartsWith("--cards=")) ParseDebugCards(a["--cards=".Length..]);
            else if (a.StartsWith("--players="))
            {
                var r = a["--players=".Length..].Split('-');
                if (int.TryParse(r[0], out int lo)) _simMin = _simMax = lo;
                if (r.Length > 1 && int.TryParse(r[1], out int hi)) _simMax = hi;
                _simMin = Mathf.Clamp(_simMin, 2, LobbyState.MaxPlayers);
                _simMax = Mathf.Clamp(_simMax, _simMin, LobbyState.MaxPlayers);
            }
        }
        if (args.Any(a => a is "--autotest" || a.StartsWith("--autohost") || a.StartsWith("--autojoin")))
            _game.Stats.Enabled = forceStats;
        if (_overlayCheck) _game.CardPickBegan += () => _ocSawPick = true;
        if (_shotDir != null)
        {
            _game.CardPickBegan += () => GetTree().CreateTimer(0.3).Timeout += SaveShot;
            _game.MatchEnded += _ => GetTree().CreateTimer(0.3, true, false, true).Timeout += SaveShot;
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
                _net.AddBot();
                _net.AddBot();
                _net.AddBot();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 2, RoundsToWin = 3, LoserChoices = 5, WinnerChoices = 3 });
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
                _net.AddBot();
                _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 2, LoserChoices = 3, WinnerChoices = 2 });
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
                _net.AddBot();
                _net.AddBot();
                _net.AddBot();
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
                _net.AddBot();
                _net.AddBot();
                _net.AddBot();
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
            else if (a == "--upnpcheck")
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    string result = PortOpener.Probe();
                    Callable.From(() => { Log.Info($"UPNPCHECK {result}"); GetTree().Quit(0); }).CallDeferred();
                });
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
                        _net.AddBot();
                        _net.AddBot();
                        _net.AddBot();
                        Show(_lobby);
                    },
                    () =>
                    {
                        _net.UseUpnp = false; // a screenshot run must not touch the router
                        _net.Host(24611);
                        _net.AddBot();
                        Show(_lobby);
                    },
                    () => { _net.Leave(); Show(_menu); },
                };
                foreach (LookPart part in System.Enum.GetValues<LookPart>())
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
        if (_shotDir != null && _frames % 240 == 0 && _shotCount < 40) SaveShot();
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

    private void Finish(int code, string why)
    {
        Log.Info($"AUTOTEST {(code == 0 ? "PASS" : "FAIL")}: {why}");
        _autoMode = null;
        GetTree().Quit(code);
    }
}
