using System;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

public partial class MenuUI : Control
{
    public event Action PlayBots, HostLan, JoinLan, Customize, Options;

    public LineEdit NameEdit, AddressEdit, PortEdit;
    private Label _status;
    private AvatarPreview _preview;
    private readonly NetworkManager _net;

    public MenuUI(NetworkManager net) => _net = net;
    public MenuUI() { }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var row = UiKit.HBox(36);
        center.AddChild(row);

        // Left: the title and your character.
        var left = UiKit.VBox(2);
        left.Alignment = BoxContainer.AlignmentMode.Center;
        left.AddChild(new TitleText("BRAWLER", 64, 3f, Palette.Players[0], Palette.Players[3], Palette.Players[2], Palette.Players[6],
            Palette.Players[1], Palette.Players[4], Palette.Players[7]));
        left.AddChild(UiKit.Label("lose a battle, pick a card, come back stronger", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center));
        _preview = new AvatarPreview(_net.LocalLook, _net.LocalColor, 5.5f, alive: true) { Pedestal = true };
        var previewBox = new CenterContainer();
        previewBox.AddChild(_preview);
        left.AddChild(previewBox);
        var customize = UiKit.Button("Customize Character", () => Customize?.Invoke());
        customize.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        left.AddChild(customize);
        row.AddChild(left);

        // Right: the play panel.
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(290, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(panel);
        var body = UiKit.VBox(9);
        panel.AddChild(body);

        body.AddChild(UiKit.Label("YOUR NAME", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        NameEdit = new LineEdit { Text = _net.LocalName, MaxLength = 12 };
        NameEdit.TextChanged += t => _net.LocalName = string.IsNullOrWhiteSpace(t) ? "Player" : t.Trim();
        body.AddChild(NameEdit);
        body.AddChild(UiKit.Spacer(2));
        body.AddChild(UiKit.Primary("PLAY VS BOTS", () => PlayBots?.Invoke()));
        body.AddChild(UiKit.Button("Host Game", () => HostLan?.Invoke()));

        body.AddChild(UiKit.Label("JOIN A FRIEND", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        var joinRow = UiKit.HBox();
        AddressEdit = new LineEdit { Text = Profile.LastAddress() is { Length: > 0 } last ? last : "127.0.0.1", PlaceholderText = "host IP or IP:port", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        PortEdit = new LineEdit { Text = NetworkManager.DefaultPort.ToString(), CustomMinimumSize = new Vector2(66, 0) };
        joinRow.AddChild(AddressEdit);
        joinRow.AddChild(PortEdit);
        joinRow.AddChild(UiKit.Button("Join", () => JoinLan?.Invoke()));
        body.AddChild(joinRow);

        _status = UiKit.Label("", UiKit.Small, Palette.Accent, HorizontalAlignment.Center);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_status);
        var bottomRow = UiKit.HBox(6);
        var options = UiKit.Button("Options", () => Options?.Invoke());
        var quit = UiKit.Button("Quit", () => GetTree().Quit());
        options.SizeFlagsHorizontal = quit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bottomRow.AddChild(options);
        bottomRow.AddChild(quit);
        body.AddChild(bottomRow);

        var hint = UiKit.Label("A/D move   W/Space jump   LMB shoot   RMB/Shift block", UiKit.Tiny, Palette.TextDim with { A = 0.8f }, HorizontalAlignment.Center);
        hint.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        hint.OffsetTop = -26;
        hint.OffsetBottom = -8;
        hint.GrowHorizontal = GrowDirection.Both;
        AddChild(hint);

        var version = UiKit.Label($"v{NetworkManager.Version}", UiKit.Tiny, Palette.TextDim with { A = 0.6f }, HorizontalAlignment.Right);
        version.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        version.OffsetLeft = -120;
        version.OffsetRight = -10;
        version.OffsetTop = -26;
        version.OffsetBottom = -8;
        AddChild(version);

        VisibilityChanged += RefreshPreview;
    }

    public int Port => int.TryParse(PortEdit.Text, out int p) && p > 0 && p < 65536 ? p : NetworkManager.DefaultPort;

    public void SetStatus(string text) => _status.Text = text ?? "";

    private void RefreshPreview()
    {
        if (!Visible || _preview == null) return;
        _preview.Look = _net.LocalLook;
        _preview.ColorIdx = _net.LocalColor;
        _preview.QueueRedraw();
    }
}
