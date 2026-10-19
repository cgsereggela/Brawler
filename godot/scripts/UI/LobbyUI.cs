using System;
using Brawler.Core;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

public partial class LobbyUI : Control
{
    public event Action StartPressed, LeavePressed;

    private readonly NetworkManager _net;
    private Label _info;
    private VBoxContainer _players;
    private Button _addBot, _start, _color;
    private SpinBox _battles, _rounds, _loserCards, _winnerCards;

    public LobbyUI(NetworkManager net) => _net = net;
    public LobbyUI() { }

    public override void _Ready()
    {
        var (_, body) = UiKit.CenteredPanel(this, 300);
        body.AddChild(UiKit.Label("LOBBY", 20, Palette.Accent, HorizontalAlignment.Center));
        _info = UiKit.Label("", 8, Palette.TextDim, HorizontalAlignment.Center);
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_info);

        _players = UiKit.VBox(3);
        body.AddChild(_players);

        var row = UiKit.HBox();
        _addBot = UiKit.Button("+ Add Bot", () => _net.AddBot());
        _color = UiKit.Button("Change Color", () => _net.SetLocalColor((_net.LocalColor + 1) % Palette.Players.Length));
        row.AddChild(_addBot);
        row.AddChild(_color);
        body.AddChild(row);

        body.AddChild(UiKit.Label("Match settings", 10, Palette.TextDim));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 8);
        _battles = AddSpin(grid, "Battle wins per round", 1, 5);
        _rounds = AddSpin(grid, "Round wins to win match", 1, 10);
        _loserCards = AddSpin(grid, "Card choices (losers)", 1, 7);
        _winnerCards = AddSpin(grid, "Card choices (winner, 0=none)", 0, 7);
        body.AddChild(grid);

        var bottom = UiKit.HBox();
        _start = UiKit.Button("Start Match", () => StartPressed?.Invoke());
        _start.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bottom.AddChild(_start);
        bottom.AddChild(UiKit.Button("Leave", () => LeavePressed?.Invoke()));
        body.AddChild(bottom);

        _net.LobbyChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree() => _net.LobbyChanged -= Refresh;

    private SpinBox AddSpin(GridContainer grid, string label, int min, int max)
    {
        grid.AddChild(UiKit.Label(label, 8, Palette.Text));
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new Vector2(56, 0) };
        s.ValueChanged += _ => PushSettings();
        grid.AddChild(s);
        return s;
    }

    private void PushSettings()
    {
        if (!_net.IsHost) return;
        _net.SetSettings(new MatchSettings
        {
            BattlesPerRound = (int)_battles.Value,
            RoundsToWin = (int)_rounds.Value,
            LoserChoices = (int)_loserCards.Value,
            WinnerChoices = (int)_winnerCards.Value,
        });
    }

    private void Refresh()
    {
        if (_players == null) return;
        var lobby = _net.Lobby;
        bool host = _net.IsHost;

        _info.Text = _net.Mode switch
        {
            NetMode.Offline => "Offline match. Add bots and start.",
            NetMode.Host => $"Hosting on port {_net.Port}. Friends join with: {string.Join(" / ", NetworkManager.LocalAddresses())}",
            NetMode.Client => "Connected. Waiting for the host to start.",
            _ => "",
        };

        UiKit.ClearChildren(_players);
        for (int slot = 0; slot < LobbyState.MaxPlayers; slot++)
        {
            var p = lobby.BySlot(slot);
            var row = UiKit.HBox(6);
            if (p == null)
            {
                row.AddChild(UiKit.Swatch(Palette.SolidDark, 10));
                row.AddChild(UiKit.Label("- empty -", 10, Palette.TextDim));
            }
            else
            {
                row.AddChild(UiKit.Swatch(Palette.Player(p.ColorIdx), 10));
                var name = UiKit.Label(p.Name + (p.IsBot ? "  [BOT]" : "") + (p.PeerId == _net.LocalPeer && !p.IsBot ? "  (you)" : ""));
                name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(name);
                if (host && p.IsBot)
                {
                    int s = p.Slot;
                    row.AddChild(UiKit.Button("x", () => _net.RemoveBot(s)));
                }
            }
            _players.AddChild(row);
        }

        var st = lobby.Settings;
        _battles.SetValueNoSignal(st.BattlesPerRound);
        _rounds.SetValueNoSignal(st.RoundsToWin);
        _loserCards.SetValueNoSignal(st.LoserChoices);
        _winnerCards.SetValueNoSignal(st.WinnerChoices);
        foreach (var s in new[] { _battles, _rounds, _loserCards, _winnerCards }) s.Editable = host;

        _addBot.Visible = host;
        _addBot.Disabled = lobby.FreeSlot() < 0;
        _start.Visible = host;
        _start.Disabled = lobby.Players.Count < 2;
    }
}
