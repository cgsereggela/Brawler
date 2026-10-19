using System;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

public partial class LobbyUI : Control
{
    public event Action StartPressed, LeavePressed, CustomizePressed, OptionsPressed;

    private readonly NetworkManager _net;
    private Label _info;
    private HBoxContainer _players;
    private Button _addBot, _start, _color;
    private SpinBox _battles, _rounds, _loserCards, _winnerCards;

    public LobbyUI(NetworkManager net) => _net = net;
    public LobbyUI() { }

    public override void _Ready()
    {
        var (_, body) = UiKit.CenteredPanel(this, 560);
        body.AddChild(new TitleText("LOBBY", 30, 1.5f, Palette.Cool, Palette.Hot));
        _info = UiKit.Label("", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center);
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_info);

        _players = UiKit.HBox(8);
        _players.Alignment = BoxContainer.AlignmentMode.Center;
        body.AddChild(_players);


        body.AddChild(UiKit.Label("MATCH SETTINGS", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        _battles = AddSpin(grid, "Battle wins per round", 1, 5);
        _rounds = AddSpin(grid, "Round wins to win", 1, 10);
        _loserCards = AddSpin(grid, "Card choices (losers)", 1, 7);
        _winnerCards = AddSpin(grid, "Card choices (winner)", 0, 7);
        body.AddChild(grid);

        var bottom = UiKit.HBox(8);
        _start = UiKit.Primary("START MATCH", () => StartPressed?.Invoke());
        _start.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bottom.AddChild(_start);
        _addBot = UiKit.Button("+ Add Bot", () => _net.AddBot());
        _color = UiKit.Button("Customize", () => CustomizePressed?.Invoke());
        bottom.AddChild(_addBot);
        bottom.AddChild(_color);
        bottom.AddChild(UiKit.Button("Options", () => OptionsPressed?.Invoke()));
        bottom.AddChild(UiKit.Button("Leave", () => LeavePressed?.Invoke()));
        body.AddChild(bottom);

        _net.LobbyChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree() => _net.LobbyChanged -= Refresh;

    private SpinBox AddSpin(GridContainer grid, string label, int min, int max)
    {
        var l = UiKit.Label(label, UiKit.Small, Palette.Text);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(l);
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new Vector2(70, 0) };
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

    /// <summary>One player slot: a little glass card with the character on a pedestal.</summary>
    private Control SlotCard(PlayerInfo p, bool host)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(122, 132) };
        var box = new StyleBoxFlat
        {
            BgColor = p == null ? new Color(1, 1, 1, 0.03f) : Palette.Player(p.ColorIdx) with { A = 0.14f },
            BorderColor = p == null ? new Color(1, 1, 1, 0.08f) : Palette.Player(p.ColorIdx) with { A = 0.6f },
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 6, ContentMarginBottom = 8,
        };
        card.AddThemeStyleboxOverride("panel", box);
        var v = UiKit.VBox(2);
        card.AddChild(v);
        if (p == null)
        {
            var empty = UiKit.Label("open slot", UiKit.Small, Palette.TextDim with { A = 0.6f }, HorizontalAlignment.Center);
            empty.SizeFlagsVertical = SizeFlags.ExpandFill;
            empty.VerticalAlignment = VerticalAlignment.Center;
            v.AddChild(empty);
            return card;
        }
        var preview = new AvatarPreview(p.Look, p.ColorIdx, 2.6f, alive: p.PeerId == _net.LocalPeer && !p.IsBot) { ShowGun = false, Pedestal = true };
        var pc = new CenterContainer();
        pc.AddChild(preview);
        v.AddChild(pc);
        v.AddChild(UiKit.Label(p.Name, UiKit.Body, Colors.White, HorizontalAlignment.Center, Fonts.Bold));
        string tag = p.IsBot ? "BOT" : p.PeerId == _net.LocalPeer ? "YOU" : "PLAYER";
        if (host && p.IsBot)
        {
            int s = p.Slot;
            var x = UiKit.Button("remove", () => _net.RemoveBot(s), 0, "Small");
            x.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            v.AddChild(x);
        }
        else v.AddChild(UiKit.Label(tag, UiKit.Tiny, Palette.Player(p.ColorIdx).Lightened(0.3f), HorizontalAlignment.Center, Fonts.Bold));
        return card;
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
            _players.AddChild(SlotCard(lobby.BySlot(slot), host));

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
