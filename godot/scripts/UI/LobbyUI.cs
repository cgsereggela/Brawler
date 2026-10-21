using System;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

public partial class LobbyUI : Control
{
    public event Action StartPressed, LeavePressed, CustomizePressed, OptionsPressed;

    private readonly NetworkManager _net;
    private Label _info, _internet;
    private Button _copy;
    private HBoxContainer _inviteRow;
    private GridContainer _players;
    private Button _addBot, _start, _color, _cards;
    private CardPoolUI _pool;
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

        // Host only: the internet address (from UPnP) and a copy button, so the invite can be pasted to friends.
        _inviteRow = UiKit.HBox(6);
        _inviteRow.Alignment = BoxContainer.AlignmentMode.Center;
        _internet = UiKit.Label("", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center);
        _internet.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _internet.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _copy = UiKit.Button("Copy", CopyInvite, 0, "Small");
        _copy.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _inviteRow.AddChild(_internet);
        _inviteRow.AddChild(_copy);
        body.AddChild(_inviteRow);

        _players = new GridContainer { Columns = 4 };
        _players.AddThemeConstantOverride("h_separation", 8);
        _players.AddThemeConstantOverride("v_separation", 8);
        _players.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        body.AddChild(_players);


        body.AddChild(UiKit.Label("MATCH SETTINGS", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        _battles = AddSpin(grid, "Battle wins per round", 1, 5);
        _rounds = AddSpin(grid, "Round wins to win", 1, 10);
        _loserCards = AddSpin(grid, "Card choices (losers)", 1, CardPool.MaxChoices);
        _winnerCards = AddSpin(grid, "Card choices (winner)", 0, CardPool.MaxChoices);
        body.AddChild(grid);

        // The card pool: the host picks which cards can be offered; everyone else can look.
        var poolRow = UiKit.HBox(8);
        var poolLabel = UiKit.Label("Cards that can come up in picks", UiKit.Small, Palette.Text);
        poolLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        poolLabel.VerticalAlignment = VerticalAlignment.Center;
        poolRow.AddChild(poolLabel);
        _cards = UiKit.Button("Cards", () => _pool.Open(), 120, "Small");
        poolRow.AddChild(_cards);
        body.AddChild(poolRow);

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

        _pool = new CardPoolUI(_net) { Name = "CardPool", Visible = false };
        AddChild(_pool);
        _pool.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        VisibilityChanged += () => { if (!Visible) _pool.Visible = false; };

        _net.LobbyChanged += Refresh;
        _net.PortsChanged += Refresh;
        StreamerMode.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        _net.LobbyChanged -= Refresh;
        _net.PortsChanged -= Refresh;
        StreamerMode.Changed -= Refresh;
    }

    /// <summary>Opens or closes the card pool panel (the Cards button opens it too).</summary>
    public void ShowCardPool(bool open)
    {
        if (open) _pool.Open();
        else _pool.Visible = false;
    }

    private const string Guide = "see Playing online in README.txt";

    private void RefreshInvite()
    {
        _inviteRow.Visible = _net.Mode == NetMode.Host;
        if (!_inviteRow.Visible) return;
        var ports = _net.Ports;
        int port = _net.Port;
        Color warn = Palette.Hot.Lightened(0.35f);
        (string text, Color color) = ports.State switch
        {
            PortState.Working => ("Internet: asking your router to open the port...", Palette.TextDim),
            PortState.Opened when ports.BehindCgnat =>
                ($"Internet: your ISP shares your public address (CGNAT), so friends can't reach you directly. Use Tailscale or ZeroTier ({Guide}).", warn),
            PortState.Opened when ports.PublicIp != "" && StreamerMode.On =>
                ($"Internet: port {port} opened automatically. Address hidden (streamer mode): Copy gives an invite code to send privately.", Palette.Cool.Lightened(0.2f)),
            PortState.Opened when ports.PublicIp != "" =>
                ($"Internet: {ports.PublicIp}:{port}  (port opened automatically)", Palette.Cool.Lightened(0.2f)),
            PortState.Opened => ($"Internet: port {port} opened, but the router didn't report your public IP. Search \"what is my IP\".", Palette.TextDim),
            PortState.Idle => (StreamerMode.On ? "Copy an invite code and send it privately to friends on your network or VPN." : "Copy your address and send it to friends on your network or VPN.", Palette.TextDim),
            _ => ($"Internet: the router didn't open the port automatically. Forward UDP {port} by hand or use a VPN ({Guide}).", warn),
        };
        _internet.Text = text;
        _internet.AddThemeColorOverride("font_color", color);
        _copy.Text = CopyLabel;
        _copy.Visible = InviteAddress() != "";
    }

    /// <summary>The address a friend should paste: the public one when the port is open, else this machine's LAN/VPN address. In streamer mode it comes as an invite code, so the address itself never shows.</summary>
    private string InviteAddress()
    {
        string address = RawInviteAddress();
        if (!StreamerMode.On || address == "") return address;
        var (host, port) = NetAddress.Parse(address, _net.Port);
        return InviteCode.Encode(host, port);
    }

    private string RawInviteAddress()
    {
        var ports = _net.Ports;
        string port = _net.Port == NetworkManager.DefaultPort ? "" : $":{_net.Port}";
        if (ports.State == PortState.Opened && ports.PublicIp != "" && !ports.BehindCgnat) return ports.PublicIp + port;
        var local = NetworkManager.LocalAddresses();
        return local.Length > 0 ? local[0] + port : "";
    }

    private static string CopyLabel => StreamerMode.On ? "Copy invite" : "Copy";

    private void CopyInvite()
    {
        string address = InviteAddress();
        if (address == "") return;
        DisplayServer.ClipboardSet(address);
        _copy.Text = "Copied!";
        GetTree().CreateTimer(1.5).Timeout += () => { if (IsInstanceValid(_copy)) _copy.Text = CopyLabel; };
    }

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
            DisabledCards = _net.Lobby.Settings.DisabledCards,
        });
    }

    /// <summary>One player slot: a little glass card with the character on a pedestal.</summary>
    private Control SlotCard(PlayerInfo p, bool host)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(122, 112) };
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
        var preview = new AvatarPreview(p.Look, p.ColorIdx, 2.1f, alive: p.PeerId == _net.LocalPeer && !p.IsBot) { ShowGun = false, Pedestal = true };
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
            NetMode.Host when StreamerMode.On => $"Hosting on port {_net.Port}. Streamer mode: addresses are hidden.",
            NetMode.Host => $"Hosting on port {_net.Port}. Same network or VPN: {string.Join(" / ", NetworkManager.LocalAddresses())}",
            NetMode.Client => "Connected. Waiting for the host to start.",
            _ => "",
        };
        RefreshInvite();

        UiKit.ClearChildren(_players);
        for (int slot = 0; slot < LobbyState.MaxPlayers; slot++)
            _players.AddChild(SlotCard(lobby.BySlot(slot), host));

        var st = lobby.Settings;
        _battles.SetValueNoSignal(st.BattlesPerRound);
        _rounds.SetValueNoSignal(st.RoundsToWin);
        _loserCards.SetValueNoSignal(st.LoserChoices);
        _winnerCards.SetValueNoSignal(st.WinnerChoices);
        foreach (var s in new[] { _battles, _rounds, _loserCards, _winnerCards }) s.Editable = host;
        _cards.Text = $"Cards {CardPool.EnabledCount(st.DisabledCards)}/{CardRegistry.All.Count}";

        _addBot.Visible = host;
        _addBot.Disabled = lobby.FreeSlot() < 0;
        _start.Visible = host;
        _start.Disabled = lobby.Players.Count < 2;
    }
}
