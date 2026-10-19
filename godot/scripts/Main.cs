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
///   --stats               also record stats during --autotest/--autohost (off by default so tests don't pollute the log)
///   --cards=ID,ID,...     (offline) every player starts each battle holding these cards; --cards=random:N deals N random
///                         cards to each player every battle, to exercise the whole card pool
/// </summary>
public partial class Main : Node
{
    private NetworkManager _net;
    private GameManager _game;
    private Sfx _sfx;
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
            _net.Join(_menu.AddressEdit.Text.Trim(), _menu.Port);
        };
        _menu.Customize += () => OpenCustomize(_menu);
        _lobby.CustomizePressed += () => OpenCustomize(_lobby);
        _custom.Closed += () => Show(_customReturn == _lobby && _net.Mode != NetMode.None && !_net.InMatch ? _lobby : _menu);

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

        _hud.LeaveRequested += () => _net.Leave();
        _over.BackToLobby += () => _game.HostReturnToLobby();
        _over.LeavePressed += () => _net.Leave();

        _game.MatchBegan += () => Show(_hud);
        _game.BattleStarted += () => Show(_hud);
        _game.CardPickBegan += () => Show(_hud, _pick);
        _game.MatchEnded += OnMatchEnded;
        _game.ReturnedToLobby += () => Show(_lobby);
    }

    private Control[] Screens => new Control[] { _menu, _lobby, _hud, _pick, _over, _custom };

    private void OpenCustomize(Control from)
    {
        _customReturn = from;
        Show(_custom);
    }

    private void SaveProfile() => Profile.Save(_net.LocalName, _net.LocalColor, _net.LocalLook);

    private void Show(params Control[] screens)
    {
        foreach (var c in Screens)
            c.Visible = screens.Contains(c);
        // The menu scenery shows whenever no arena is on screen, with a new theme each time you come back.
        bool inArena = screens.Contains(_hud);
        if (!inArena && !_menuBackdrop.Visible)
            _menuBackdrop.Set(ArenaTheme.All[(int)(GD.Randi() % (uint)ArenaTheme.All.Length)], (int)GD.Randi());
        _menuBackdrop.Visible = !inArena;
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
            t.Timeout += () => Finish(0, $"match finished, winner slot {winner} | {_game.ScoreLine()}");
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
            else if (a.StartsWith("--statsdir=")) _game.Stats.Dir = a["--statsdir=".Length..].Replace('\\', '/');
            else if (a == "--stats") forceStats = true;
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
            else if (a == "--menushots" && _shotDir != null)
            {
                // Screenshot the menu, the character editor, an offline lobby with bots, and a gallery
                // of every option of every look part, then quit.
                var tree = GetTree();
                var steps = new System.Collections.Generic.List<System.Action>
                {
                    () => OpenCustomize(_menu),
                    () =>
                    {
                        _net.StartOffline();
                        _net.AddBot();
                        _net.AddBot();
                        _net.AddBot();
                        Show(_lobby);
                    },
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
        if (++_frames > AutotestMaxFrames)
        {
            if (_autoMode != "sim") { Finish(1, "timed out before the match ended"); return; }
            Log.Info("SIMULATE match timed out, abandoning it (saved as incomplete)");
            _frames = 0;
            SimMatchEnded(-1);
        }
        if (_shotDir != null && _frames % 240 == 0 && _shotCount < 40) SaveShot();
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
