using System;
using Brawler.Core;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

public partial class MenuUI : Control
{
    public event Action PlayBots, HostLan, JoinLan;

    public LineEdit NameEdit, AddressEdit, PortEdit;
    private Label _status;
    private Button _colorBtn;
    private ColorRect _swatch;
    private readonly NetworkManager _net;

    public MenuUI(NetworkManager net) => _net = net;
    public MenuUI() { }

    public override void _Ready()
    {
        var (_, body) = UiKit.CenteredPanel(this, 240);
        body.AddChild(UiKit.Label("BRAWLER", 32, Palette.Accent, HorizontalAlignment.Center));
        body.AddChild(UiKit.Label("lose a battle, pick a card, come back stronger", 10, Palette.TextDim, HorizontalAlignment.Center));
        body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

        var nameRow = UiKit.HBox();
        nameRow.AddChild(UiKit.Label("Name", 10, Palette.TextDim));
        NameEdit = new LineEdit { Text = _net.LocalName, MaxLength = 12, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        NameEdit.TextChanged += t => _net.LocalName = string.IsNullOrWhiteSpace(t) ? "Player" : t.Trim();
        nameRow.AddChild(NameEdit);
        _swatch = UiKit.Swatch(Palette.Player(_net.LocalColor), 12);
        nameRow.AddChild(_swatch);
        _colorBtn = UiKit.Button("Color", CycleColor);
        nameRow.AddChild(_colorBtn);
        body.AddChild(nameRow);

        body.AddChild(UiKit.Button("Play vs Bots", () => PlayBots?.Invoke()));
        body.AddChild(UiKit.Button("Host LAN Game", () => HostLan?.Invoke()));

        var joinRow = UiKit.HBox();
        AddressEdit = new LineEdit { Text = "127.0.0.1", PlaceholderText = "host IP", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        PortEdit = new LineEdit { Text = NetworkManager.DefaultPort.ToString(), CustomMinimumSize = new Vector2(54, 0) };
        joinRow.AddChild(AddressEdit);
        joinRow.AddChild(PortEdit);
        joinRow.AddChild(UiKit.Button("Join", () => JoinLan?.Invoke()));
        body.AddChild(joinRow);

        _status = UiKit.Label("", 10, Palette.TextDim, HorizontalAlignment.Center);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_status);
        body.AddChild(UiKit.Button("Quit", () => GetTree().Quit()));

        body.AddChild(UiKit.Label("A/D move  W/Space jump  LMB shoot  RMB/Shift block", 8, Palette.TextDim, HorizontalAlignment.Center));
    }

    public int Port => int.TryParse(PortEdit.Text, out int p) && p > 0 && p < 65536 ? p : NetworkManager.DefaultPort;

    public void SetStatus(string text) => _status.Text = text ?? "";

    private void CycleColor()
    {
        _net.LocalColor = (_net.LocalColor + 1) % Palette.Players.Length;
        _swatch.Color = Palette.Player(_net.LocalColor);
    }
}
