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
    private VBoxContainer _inviteRow;
    private GridContainer _players;
    private Button _addBot, _addLocal, _start, _color, _cards;
    private CardPoolUI _pool;
    private ArenaPoolUI _arenaPool;
    private SpinBox _battles, _rounds, _loserCards, _winnerCards;
    private Button _lava, _rare, _deals, _killCam, _draft, _gameMode, _mode, _friendly, _arenas;
    private Label _problem, _friendlyLabel;

    public LobbyUI(NetworkManager net) => _net = net;
    public LobbyUI() { }

    public override void _Ready()
    {
        // Two glass panels side by side, players on the left and match settings on the right, with the title, the
        // connection info and Start between them. Everything fits on screen at once, so nothing scrolls.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var row = UiKit.HBox(14);
        center.AddChild(row);

        var left = Panel(row);
        left.AddChild(UiKit.Label("PLAYERS", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        _players = new GridContainer { Columns = 2 };
        _players.AddThemeConstantOverride("h_separation", 8);
        _players.AddThemeConstantOverride("v_separation", 8);
        _players.SizeFlagsVertical = SizeFlags.ExpandFill;
        left.AddChild(_players);

        var middle = UiKit.VBox(10);
        middle.CustomMinimumSize = new Vector2(190, 0);
        middle.Alignment = BoxContainer.AlignmentMode.Center;
        row.AddChild(middle);
        middle.AddChild(new TitleText("LOBBY", 30, 1.5f, Palette.Cool, Palette.Hot));
        _info = Outlined(UiKit.Label("", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center));
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        middle.AddChild(_info);

        // Host only: the internet address (from UPnP) and a copy button, so the invite can be pasted to friends.
        _inviteRow = UiKit.VBox(4);
        _internet = Outlined(UiKit.Label("", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center));
        _internet.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _copy = UiKit.Button("Copy", CopyInvite, 0, "Small");
        _copy.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _inviteRow.AddChild(_internet);
        _inviteRow.AddChild(_copy);
        middle.AddChild(_inviteRow);

        middle.AddChild(UiKit.Spacer(6));
        _start = UiKit.Primary("START MATCH", () => StartPressed?.Invoke());
        middle.AddChild(_start);
        _problem = Outlined(UiKit.Label("", UiKit.Small, Palette.Hot.Lightened(0.35f), HorizontalAlignment.Center));
        _problem.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        middle.AddChild(_problem);

        var right = Panel(row);
        right.AddChild(UiKit.Label("MATCH SETTINGS", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 5);
        _arenas = AddToggle(grid, "Arenas", "Random: a freshly generated arena every battle. Or pick custom arenas from the Map Editor (or a friend's map code) to take turns.", () => _arenaPool.Open(), out _);
        _gameMode = AddToggle(grid, "Game mode", "Elimination: last one standing wins the battle. King of the Hill: hold the glowing zone alone for 10 s in total. Capture the Flag (teams): bring the enemy flag to your base while yours is there. Headhunter: the leader wears a crown, and whoever takes them out wins the battle. In King of the Hill and Capture the Flag the fallen respawn until sudden death, which comes twice as late.", CycleGame, out _);
        _mode = AddToggle(grid, "Teams", "Free-for-all, or two teams (2v2, 3v3, 4v4): the last team standing wins the battle, and the whole team scores. Click a player's team tag to move them.", CycleMode, out _);
        // Only in team modes.
        _friendly = AddToggle(grid, "Friendly fire", "Whether your shots and blasts hurt your teammates. Off, they pass straight through.", ToggleFriendlyFire, out _friendlyLabel);
        _battles = AddSpin(grid, "Battle wins per round", 1, 5);
        _rounds = AddSpin(grid, "Round wins to win", 1, 10);
        _loserCards = AddSpin(grid, "Card choices (losers)", 1, CardPool.MaxChoices);
        _winnerCards = AddSpin(grid, "Card choices (winner)", 0, CardPool.MaxChoices);
        _lava = AddToggle(grid, "Lava rises after", "Sudden death: after this long in a battle, lava wells up from below and keeps climbing.", CycleLava, out _);
        _rare = AddToggle(grid, "Rare cards", "How often a pick offers a rare card: a strong, build-defining card with a gold frame. With cursed deals on, a deal comes up this often too. Off: no rares at all.", CycleRare, out _);
        _killCam = AddToggle(grid, "Kill cam", "After each battle, replay the final kill in slow motion. Anyone can skip it; it ends once everyone has.", ToggleKillCam, out _);
        _draft = AddToggle(grid, "Draft", "Before the first battle, everyone takes turns picking cards from one shared pool (two each, or one with five or more players), so the match starts with builds.", ToggleDraft, out _);
        right.AddChild(grid);

        // The card pool: the host picks which cards can be offered; everyone else can look.
        var poolRow = UiKit.HBox(8);
        _deals = UiKit.Button("", ToggleDeals, 0, "Small");
        _deals.TooltipText = "Now and then (as often as the rare-card chance) a pick also offers a cursed deal: a rare card, but you take a curse card with it.";
        _deals.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        poolRow.AddChild(_deals);
        _cards = UiKit.Button("Cards", () => _pool.Open(), 0, "Small");
        _cards.TooltipText = "Cards that can come up in picks";
        _cards.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        poolRow.AddChild(_cards);
        right.AddChild(poolRow);

        // Pushed to the bottom of the panel, under the settings.
        right.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        var buttons = new GridContainer { Columns = 2 };
        buttons.AddThemeConstantOverride("h_separation", 8);
        buttons.AddThemeConstantOverride("v_separation", 8);
        _addBot = UiKit.Button("+ Add Bot", () => _net.AddBot());
        _addLocal = UiKit.Button(AddLocalText, ToggleWaitForPad);
        _addLocal.TooltipText = "Local co-op: another person at this computer joins with their own controller";
        _color = UiKit.Button("Customize", () => CustomizePressed?.Invoke());
        foreach (var b in new[] { _addBot, _addLocal, _color, UiKit.Button("Options", () => OptionsPressed?.Invoke()), UiKit.Button("Leave", () => LeavePressed?.Invoke()) })
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            buttons.AddChild(b);
        }
        right.AddChild(buttons);

        _pool = new CardPoolUI(_net) { Name = "CardPool", Visible = false };
        AddChild(_pool);
        _pool.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _arenaPool = new ArenaPoolUI(_net) { Name = "ArenaPool", Visible = false };
        AddChild(_arenaPool);
        _arenaPool.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        VisibilityChanged += () => { if (!Visible) _pool.Visible = _arenaPool.Visible = false; };

        _net.LobbyChanged += Refresh;
        _net.PortsChanged += Refresh;
        StreamerMode.Changed += Refresh;
        Refresh();
    }

    /// <summary>One of the two glass panels; both stretch to the taller one's height.</summary>
    private static VBoxContainer Panel(HBoxContainer row)
    {
        var panel = new PanelContainer { SizeFlagsVertical = SizeFlags.Fill };
        row.AddChild(panel);
        var body = UiKit.VBox(8);
        panel.AddChild(body);
        return body;
    }

    /// <summary>Text that sits on the backdrop between the panels gets a dark outline to stay readable.</summary>
    private static Label Outlined(Label l)
    {
        l.AddThemeConstantOverride("outline_size", 4);
        l.AddThemeColorOverride("font_outline_color", new Color(0.04f, 0.03f, 0.08f, 0.9f));
        return l;
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
            Mode = _net.Lobby.Settings.Mode,
            Draft = _net.Lobby.Settings.Draft,
            DisabledCards = _net.Lobby.Settings.DisabledCards,
            Maps = _net.Lobby.Settings.Maps,
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

    private void ToggleDraft()
    {
        if (!_net.IsHost) return;
        _net.Lobby.Settings.Draft = !_net.Lobby.Settings.Draft;
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

    /// <summary>Host: step through the game modes. Capture the Flag needs teams, so picking it in free-for-all makes it 2v2.</summary>
    private void CycleGame()
    {
        if (!_net.IsHost) return;
        var s = _net.Lobby.Settings;
        s.Mode = GameModes.Next(s.Mode);
        if (GameModes.Problem(s.Mode, s.TeamSize) != null) _net.SetTeamSize(2);
        else PushSettings();
    }

    private void ToggleFriendlyFire()
    {
        if (!_net.IsHost) return;
        _net.Lobby.Settings.FriendlyFire = !_net.Lobby.Settings.FriendlyFire;
        PushSettings();
    }

    /// <summary>One player slot: a little glass card with the character on a pedestal beside their name.</summary>
    /// <param name="team">Team modes: the team this card sits in (an open slot there still shows the team's tint).</param>
    private Control SlotCard(PlayerInfo p, bool host, int team = -1)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(176, 78) };
        var edge = team >= 0 ? Palette.Team(team) : p != null ? Palette.Player(p.ColorIdx) : Colors.White;
        var box = new StyleBoxFlat
        {
            BgColor = p == null ? edge with { A = team >= 0 ? 0.05f : 0.03f } : Palette.Player(p.ColorIdx) with { A = 0.14f },
            BorderColor = p == null ? edge with { A = team >= 0 ? 0.25f : 0.08f } : edge with { A = 0.6f },
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            ContentMarginLeft = 6, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        card.AddThemeStyleboxOverride("panel", box);
        if (p == null)
        {
            var empty = UiKit.Label("open slot", UiKit.Small, Palette.TextDim with { A = 0.6f }, HorizontalAlignment.Center);
            empty.VerticalAlignment = VerticalAlignment.Center;
            card.AddChild(empty);
            return card;
        }
        var h = UiKit.HBox(6);
        card.AddChild(h);
        var preview = new AvatarPreview(p.Look, p.ColorIdx, 1.75f, alive: p.PeerId == _net.LocalPeer && !p.IsBot) { ShowGun = false, Pedestal = true };
        preview.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(preview);
        var v = UiKit.VBox(3);
        v.Alignment = BoxContainer.AlignmentMode.Center;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(v);
        var nameLabel = UiKit.Label(p.Name, UiKit.Body, Colors.White, font: Fonts.Bold);
        nameLabel.ClipText = true;
        nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(nameLabel);
        string tag = p.IsBot ? p.Skill == BotSkill.Normal ? "BOT" : $"{BotTuning.Name(p.Skill).ToUpperInvariant()} BOT" : p.PeerId == _net.LocalPeer ? "YOU" : p.Guest > 0 ? "COUCH" : "PLAYER";
        // One line under the name: the team tag (team modes), then remove (the host's bots) or who this is.
        var foot = UiKit.HBox(4);
        v.AddChild(foot);
        int s = p.Slot;
        if (team >= 0)
        {
            // The host can move anyone, everyone else just themselves.
            bool canSwap = host || (!p.IsBot && p.PeerId == _net.LocalPeer); // yourself and your couch guests
            string name = Teams.Names[team].ToUpperInvariant();
            var t = UiKit.Button(canSwap ? $"{name} ⇄" : name, () => _net.SwapTeam(s), 0, "Small");
            t.Disabled = !canSwap;
            t.TooltipText = canSwap ? "Move to the other team" : "";
            t.AddThemeColorOverride("font_color", Palette.Team(team).Lightened(0.3f));
            t.AddThemeColorOverride("font_disabled_color", Palette.Team(team).Lightened(0.3f));
            foot.AddChild(t);
        }
        bool myGuest = !p.IsBot && p.Guest > 0 && p.PeerId == _net.LocalPeer;
        if (myGuest)
        {
            int guest = p.Guest;
            var x = UiKit.Button(team >= 0 ? "✕" : "remove", () => _net.RemoveGuest(guest), 0, "Small");
            x.TooltipText = $"Remove this local player ({PadName(guest)})";
            foot.AddChild(x);
        }
        else if (host && p.IsBot)
        {
            var skill = UiKit.Button(BotTuning.Name(p.Skill), () => _net.CycleBotSkill(s), 0, "Small");
            skill.TooltipText = "How well this bot plays: Easy, Normal or Hard (click to change)";
            skill.AddThemeColorOverride("font_color", SkillColor(p.Skill));
            foot.AddChild(skill);
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

    private static Color SkillColor(BotSkill s) => s switch { BotSkill.Easy => new Color("#9cf0a0"), BotSkill.Hard => new Color("#ff8a6a"), _ => Palette.Text };

    // ---------- local co-op ----------

    private const string AddLocalText = "+ Local Player";
    private const float PadWaitTime = 10f;
    private float _padWait; // > 0 while waiting for a new controller to press A
    private float _padWaitAge;

    /// <summary>Where a controller lands in this screen: the card pool when it's open, else the lobby.</summary>
    public Control FocusRoot => _pool.Visible ? _pool : this;

    private static string PadName(int guest) => Pads.Couch.DeviceOf(guest) is >= 0 and var d ? Pads.Name(d) : "no controller";

    private ulong _joinedAt;

    private void ToggleWaitForPad()
    {
        // The A that just joined a guest also presses this button when it has the focus: don't start waiting again.
        if (Time.GetTicksMsec() - _joinedAt < 400) return;
        _padWait = _padWait > 0 ? 0 : PadWaitTime;
        _padWaitAge = 0;
        RefreshAddLocal();
    }

    private void RefreshAddLocal()
    {
        bool room = _net.Lobby.HasRoom() && Pads.Couch.Count < CouchRoster.MaxGuests;
        if (!room) _padWait = 0;
        _addLocal.Disabled = !room;
        _addLocal.Text = _padWait > 0 ? "Press A on their pad…" : AddLocalText;
    }

    public override void _Process(double delta)
    {
        if (_padWait <= 0) return;
        _padWait -= (float)delta;
        _padWaitAge += (float)delta;
        // A short grace, so the press that clicked this button (on a controller) isn't taken as the new player's.
        if (_padWaitAge > 0.25f)
            foreach (int d in Pads.Devices)
                if (!Pads.Couch.Claimed(d) && Pads.JustPressed(d, PadLayout.A))
                {
                    _padWait = 0;
                    _joinedAt = Time.GetTicksMsec();
                    _net.AddGuest(d);
                    break;
                }
        if (_padWait <= 0) RefreshAddLocal();
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
            // One column per team, with its open places.
            var members = Enumerable.Range(0, Teams.Count)
                .Select(team => lobby.Players.Where(p => p.Team == team).OrderBy(p => p.Slot).ToList()).ToList();
            int rows = Math.Max(st.TeamSize, members.Max(m => m.Count));
            for (int i = 0; i < rows; i++)
                for (int team = 0; team < Teams.Count; team++)
                    _players.AddChild(SlotCard(i < members[team].Count ? members[team][i] : null, host, team));
        }
        else
        {
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
        _draft.Text = st.Draft ? "On" : "Off";
        _draft.Disabled = !host;
        _gameMode.Text = GameModes.Name(st.Mode);
        _gameMode.Disabled = !host;
        _mode.Text = Teams.ModeName(st.TeamSize);
        _mode.Disabled = !host;
        _friendly.Text = st.FriendlyFire ? "On" : "Off";
        _friendly.Disabled = !host;
        _friendly.Visible = _friendlyLabel.Visible = lobby.TeamMode;
        _cards.Text = $"Cards {CardPool.EnabledCount(st.DisabledCards)}/{CardRegistry.All.Count}";
        _arenas.Text = ArenaPoolUI.Summary(st);

        _addBot.Visible = host;
        _addBot.Disabled = !lobby.HasRoom();
        RefreshAddLocal();
        _start.Visible = host;
        string problem = lobby.StartProblem();
        _start.Disabled = problem != null;
        // Two players short is the usual empty lobby; only explain the team rules.
        _problem.Text = lobby.Players.Count >= 2 ? problem ?? "" : "";
        _problem.Visible = _problem.Text != "";
    }
}
