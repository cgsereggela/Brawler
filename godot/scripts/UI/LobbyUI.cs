using System;
using System.Linq;
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
    private Button _lava, _rare, _deals, _killCam, _mode, _friendly;
    private Label _problem, _friendlyLabel;
    private ScrollContainer _scroll;
    private VBoxContainer _middle;

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

        // Players and settings scroll when the window is too short for them all (see FitScroll), so the
        // title and the Start row always stay on screen.
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(_scroll);
        _middle = UiKit.VBox(8);
        _middle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.AddChild(_middle);

        _players = new GridContainer { Columns = 4 };
        _players.AddThemeConstantOverride("h_separation", 8);
        _players.AddThemeConstantOverride("v_separation", 8);
        _players.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _middle.AddChild(_players);

        _middle.AddChild(UiKit.Label("MATCH SETTINGS", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        _battles = AddSpin(grid, "Battle wins per round", 1, 5);
        _rounds = AddSpin(grid, "Round wins to win", 1, 10);
        _loserCards = AddSpin(grid, "Card choices (losers)", 1, CardPool.MaxChoices);
        _winnerCards = AddSpin(grid, "Card choices (winner)", 0, CardPool.MaxChoices);
        _lava = AddToggle(grid, "Lava rises after", "Sudden death: after this long in a battle, lava wells up from below and keeps climbing.", CycleLava, out _);
        _rare = AddToggle(grid, "Rare cards", "How often each card in a pick is a rare one: strong, build-defining cards with a gold frame.", CycleRare, out _);
        _killCam = AddToggle(grid, "Kill cam", "After each battle, replay the final kill in slow motion. Anyone can skip it; it ends once everyone has.", ToggleKillCam, out _);
        _mode = AddToggle(grid, "Mode", "Free-for-all, or two teams (2v2, 3v3, 4v4): the last team standing wins the battle, and the whole team scores. Click a player's team tag to move them.", CycleMode, out _);
        // Only in team modes, so the free-for-all lobby stays a row shorter.
        _friendly = AddToggle(grid, "Friendly fire", "Whether your shots and blasts hurt your teammates. Off, they pass straight through.", ToggleFriendlyFire, out _friendlyLabel);
        _middle.AddChild(grid);

        // The card pool: the host picks which cards can be offered; everyone else can look.
        var poolRow = UiKit.HBox(8);
        var poolLabel = UiKit.Label("Cards that can come up in picks", UiKit.Small, Palette.Text);
        poolLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        poolLabel.VerticalAlignment = VerticalAlignment.Center;
        poolRow.AddChild(poolLabel);
        _deals = UiKit.Button("", ToggleDeals, 150, "Small");
        _deals.TooltipText = "Each pick also offers a cursed deal: a rare card, but you take a curse card with it.";
        poolRow.AddChild(_deals);
        _cards = UiKit.Button("Cards", () => _pool.Open(), 120, "Small");
        poolRow.AddChild(_cards);
        _middle.AddChild(poolRow);

        _problem = UiKit.Label("", UiKit.Small, Palette.Hot.Lightened(0.35f), HorizontalAlignment.Center);
        body.AddChild(_problem);

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

    // Every frame while shown: cheap, and catches every change (new cards, window size, the invite line wrapping).
    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) FitScroll();
    }

    /// <summary>
    /// Sizes the scrolling middle: all of it when the panel fits on screen, else just what's left once the title, info
    /// and Start row have their room.
    /// </summary>
    private void FitScroll()
    {
        if (!IsInsideTree()) return;
        var panel = (Control)_scroll.GetParent().GetParent();
        float want = _middle.GetCombinedMinimumSize().Y;
        float rest = panel.GetCombinedMinimumSize().Y - _scroll.CustomMinimumSize.Y; // everything but the middle
        float room = GetViewportRect().Size.Y - 4 - rest;
        var size = new Vector2(0, Mathf.Clamp(want, 80, Mathf.Max(80, room)));
        if (size != _scroll.CustomMinimumSize) _scroll.CustomMinimumSize = size;
        _scroll.VerticalScrollMode = want > room ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled;
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

    /// <summary>A setting the host steps through by clicking: its label (hover for the tip) and button.</summary>
    private static Button AddToggle(GridContainer grid, string label, string tip, Action pressed, out Label l)
    {
        l = UiKit.Label(label, UiKit.Small, Palette.Text);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.TooltipText = tip;
        l.MouseFilter = MouseFilterEnum.Pass;
        grid.AddChild(l);
        var b = UiKit.Button("", pressed, 70, "Small");
        grid.AddChild(b);
        return b;
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
            LavaAfter = _net.Lobby.Settings.LavaAfter,
            RareChance = _net.Lobby.Settings.RareChance,
            CursedDeals = _net.Lobby.Settings.CursedDeals,
            KillCam = _net.Lobby.Settings.KillCam,
            TeamSize = _net.Lobby.Settings.TeamSize,
            FriendlyFire = _net.Lobby.Settings.FriendlyFire,
            DisabledCards = _net.Lobby.Settings.DisabledCards,
        });
    }

    private static string LavaText(int after) => after <= 0 ? "Never" : $"{after} s";

    /// <summary>Host: steps the sudden-death lava setting to its next choice (wrapping round to Never).</summary>
    private void CycleLava()
    {
        if (!_net.IsHost) return;
        var choices = RisingLava.Choices;
        int i = Array.IndexOf(choices, RisingLava.Snap(_net.Lobby.Settings.LavaAfter));
        _net.Lobby.Settings.LavaAfter = choices[(i + 1) % choices.Length];
        PushSettings();
    }

    /// <summary>Choices for the rare-card chance, in percent per offered card.</summary>
    public static readonly int[] RareChoices = { 0, 5, 10, 20, 35 };

    private static string RareText(int pct) => pct <= 0 ? "Off" : $"{pct}%";

    /// <summary>Host: steps the rare-card chance to its next choice (wrapping round to Off).</summary>
    private void CycleRare()
    {
        if (!_net.IsHost) return;
        int i = Array.IndexOf(RareChoices, _net.Lobby.Settings.RareChance);
        _net.Lobby.Settings.RareChance = RareChoices[(i + 1) % RareChoices.Length];
        PushSettings();
    }

    private void ToggleDeals()
    {
        if (!_net.IsHost) return;
        _net.Lobby.Settings.CursedDeals = !_net.Lobby.Settings.CursedDeals;
        PushSettings();
    }

    private void ToggleKillCam()
    {
        if (!_net.IsHost) return;
        _net.Lobby.Settings.KillCam = !_net.Lobby.Settings.KillCam;
        PushSettings();
    }

    /// <summary>Host: step through free-for-all, 2v2, 3v3 and 4v4 (everyone is dealt onto fresh teams).</summary>
    private void CycleMode()
    {
        if (!_net.IsHost) return;
        _net.SetTeamSize(Teams.NextSize(_net.Lobby.Settings.TeamSize));
    }

    private void ToggleFriendlyFire()
    {
        if (!_net.IsHost) return;
        _net.Lobby.Settings.FriendlyFire = !_net.Lobby.Settings.FriendlyFire;
        PushSettings();
    }

    /// <summary>One player slot: a little glass card with the character on a pedestal.</summary>
    /// <param name="team">Team modes: the team this card sits in (an open slot there still shows the team's tint).</param>
    private Control SlotCard(PlayerInfo p, bool host, int team = -1)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(122, 100) };
        var edge = team >= 0 ? Palette.Team(team) : p != null ? Palette.Player(p.ColorIdx) : Colors.White;
        var box = new StyleBoxFlat
        {
            BgColor = p == null ? edge with { A = team >= 0 ? 0.05f : 0.03f } : Palette.Player(p.ColorIdx) with { A = 0.14f },
            BorderColor = p == null ? edge with { A = team >= 0 ? 0.25f : 0.08f } : edge with { A = 0.6f },
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
        var preview = new AvatarPreview(p.Look, p.ColorIdx, 1.9f, alive: p.PeerId == _net.LocalPeer && !p.IsBot) { ShowGun = false, Pedestal = true };
        var pc = new CenterContainer();
        pc.AddChild(preview);
        v.AddChild(pc);
        v.AddChild(UiKit.Label(p.Name, UiKit.Body, Colors.White, HorizontalAlignment.Center, Fonts.Bold));
        string tag = p.IsBot ? "BOT" : p.PeerId == _net.LocalPeer ? "YOU" : "PLAYER";
        // One line under the name: the team tag (team modes), then remove (the host's bots) or who this is.
        var foot = UiKit.HBox(4);
        foot.Alignment = BoxContainer.AlignmentMode.Center;
        v.AddChild(foot);
        int s = p.Slot;
        if (team >= 0)
        {
            // The host can move anyone, everyone else just themselves.
            bool canSwap = host || (!p.IsBot && p.PeerId == _net.LocalPeer);
            string name = Teams.Names[team].ToUpperInvariant();
            var t = UiKit.Button(canSwap ? $"{name} ⇄" : name, () => _net.SwapTeam(s), 0, "Small");
            t.Disabled = !canSwap;
            t.TooltipText = canSwap ? "Move to the other team" : "";
            t.AddThemeColorOverride("font_color", Palette.Team(team).Lightened(0.3f));
            t.AddThemeColorOverride("font_disabled_color", Palette.Team(team).Lightened(0.3f));
            foot.AddChild(t);
        }
        if (host && p.IsBot)
        {
            var x = UiKit.Button(team >= 0 ? "✕" : "remove", () => _net.RemoveBot(s), 0, "Small");
            x.TooltipText = "Remove this bot";
            foot.AddChild(x);
        }
        else
        {
            var l = UiKit.Label(tag, UiKit.Tiny, Palette.Player(p.ColorIdx).Lightened(0.3f), HorizontalAlignment.Center, Fonts.Bold);
            l.VerticalAlignment = VerticalAlignment.Center;
            foot.AddChild(l);
        }
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
        var st = lobby.Settings;
        if (lobby.TeamMode)
        {
            // One row per team, with its open places.
            _players.Columns = st.TeamSize;
            for (int team = 0; team < Teams.Count; team++)
            {
                var members = lobby.Players.Where(p => p.Team == team).OrderBy(p => p.Slot).ToList();
                for (int i = 0; i < Math.Max(st.TeamSize, members.Count); i++)
                    _players.AddChild(SlotCard(i < members.Count ? members[i] : null, host, team));
            }
        }
        else
        {
            _players.Columns = 4;
            for (int slot = 0; slot < LobbyState.MaxPlayers; slot++)
                _players.AddChild(SlotCard(lobby.BySlot(slot), host));
        }

        _battles.SetValueNoSignal(st.BattlesPerRound);
        _rounds.SetValueNoSignal(st.RoundsToWin);
        _loserCards.SetValueNoSignal(st.LoserChoices);
        _winnerCards.SetValueNoSignal(st.WinnerChoices);
        foreach (var s in new[] { _battles, _rounds, _loserCards, _winnerCards }) s.Editable = host;
        _lava.Text = LavaText(st.LavaAfter);
        _lava.Disabled = !host;
        _rare.Text = RareText(st.RareChance);
        _rare.Disabled = !host;
        _deals.Text = st.CursedDeals ? "Cursed deals: On" : "Cursed deals: Off";
        _deals.Disabled = !host;
        _killCam.Text = st.KillCam ? "On" : "Off";
        _killCam.Disabled = !host;
        _mode.Text = Teams.ModeName(st.TeamSize);
        _mode.Disabled = !host;
        _friendly.Text = st.FriendlyFire ? "On" : "Off";
        _friendly.Disabled = !host;
        _friendly.Visible = _friendlyLabel.Visible = lobby.TeamMode;
        _cards.Text = $"Cards {CardPool.EnabledCount(st.DisabledCards)}/{CardRegistry.All.Count}";

        _addBot.Visible = host;
        _addBot.Disabled = !lobby.HasRoom();
        _start.Visible = host;
        string problem = lobby.StartProblem();
        _start.Disabled = problem != null;
        // Two players short is the usual empty lobby; only explain the team rules.
        _problem.Text = lobby.Players.Count >= 2 ? problem ?? "" : "";
        _problem.Visible = _problem.Text != "";
    }
}
