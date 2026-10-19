using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
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

    private string _autoMode;
    private bool _matchFinished;
    private long _frames;
    private const long AutotestMaxFrames = 60L * 60 * 20; // 20 simulated minutes

    public override void _Ready()
    {
        LocalInput.RegisterActions();

        _net = new NetworkManager { Name = "Net" };
        AddChild(_net);
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
        foreach (var c in new Control[] { _menu, _lobby, _hud, _pick, _over }) root.AddChild(c);

        Wire();
        Show(_menu);
        HandleCommandLine();
    }

    private void Wire()
    {
        _menu.PlayBots += () =>
        {
            _net.StartOffline();
            _net.AddBot();
            Show(_lobby);
        };
        _menu.HostLan += () =>
        {
            if (_net.Host(_menu.Port)) Show(_lobby);
        };
        _menu.JoinLan += () => _net.Join(_menu.AddressEdit.Text.Trim(), _menu.Port);

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

    private void Show(params Control[] screens)
    {
        foreach (var c in new Control[] { _menu, _lobby, _hud, _pick, _over })
            c.Visible = screens.Contains(c);
    }

    private void OnMatchEnded(int winner)
    {
        Show(_hud, _over);
        _matchFinished = true;
        if (_autoMode != null)
        {
            var t = GetTree().CreateTimer(_autoMode == "host" ? 2.0 : 0.5, true, false, true);
            t.Timeout += () => Finish(0, $"match finished, winner slot {winner} | {_game.ScoreLine()}");
        }
    }

    // ---------- automated test modes ----------

    private void HandleCommandLine()
    {
        var args = OS.GetCmdlineUserArgs();
        foreach (var a in args)
            if (a.StartsWith("--shots=")) _shotDir = a["--shots=".Length..];
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
            else if (a == "--menushots" && _shotDir != null)
            {
                // Screenshot the menu, then an offline lobby with bots, then quit.
                var tree = GetTree();
                tree.CreateTimer(0.5).Timeout += () =>
                {
                    SaveShot();
                    _net.StartOffline();
                    _net.AddBot();
                    _net.AddBot();
                    Show(_lobby);
                    tree.CreateTimer(0.5).Timeout += () => { SaveShot(); tree.Quit(0); };
                };
            }
        }
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
        if (++_frames > AutotestMaxFrames) Finish(1, "timed out before the match ended");
        if (_shotDir != null && _frames % 240 == 0 && _shotCount < 40) SaveShot();
    }

    // --shots=<dir>: save a screenshot every 4 simulated seconds during an autotest (needs a window, not --headless).
    private string _shotDir;
    private int _shotCount;

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
