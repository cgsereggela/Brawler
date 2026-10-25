using System;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

public partial class MenuUI : Control
{
    public event Action PlayBots, HostLan, JoinLan, Customize, Options, MapEditor, Daily, Training;

    /// <summary>Where Report Bug sends people: the issues page of the GitHub repository.</summary>
    public const string IssuesUrl = "https://github.com/cgsereggela/Brawler/issues";

    public LineEdit NameEdit, AddressEdit, PortEdit;
    private Label _status;
    private Label _daily;
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
        left.AddChild(new TitleText("BRAWLER", 64, 3f, new Color("#f2ede2"), new Color("#d9d2c2"), new Color("#f2ede2"), new Color("#c9c1b0"),
            new Color("#f2ede2"), new Color("#d9d2c2"), new Color("#f2ede2")));
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
        // Two to a row, so the panel still fits on screen at the biggest UI scale.
        void Pair(Button a, Button b)
        {
            var r = UiKit.HBox(6);
            a.SizeFlagsHorizontal = b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            r.AddChild(a);
            r.AddChild(b);
            body.AddChild(r);
        }
        var editor = UiKit.Button("Map Editor", () => MapEditor?.Invoke());
        editor.TooltipText = "Build your own arenas and share them as map codes";
        Pair(UiKit.Button("Host Game", () => HostLan?.Invoke()), editor);
        var daily = UiKit.Button("Daily Challenge", () => Daily?.Invoke());
        daily.TooltipText = "Today's arena, mode and starting cards against three Hard bots: the same for everyone, so friends can compare";
        var training = UiKit.Button("Training Range", () => Training?.Invoke());
        training.TooltipText = "Try any card on target dummies and a bot that never fires";
        Pair(daily, training);
        _daily = UiKit.Label("", UiKit.Tiny, Palette.TextDim, HorizontalAlignment.Center);
        _daily.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_daily);

        body.AddChild(UiKit.Label("JOIN A FRIEND", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        var joinRow = UiKit.HBox();
        AddressEdit = new LineEdit { Text = Profile.LastAddress() is { Length: > 0 } last ? last : "127.0.0.1", PlaceholderText = "IP, IP:port or invite code", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        // Streamer mode masks the box, so a typed or remembered address never shows on stream.
        void Mask() => AddressEdit.Secret = StreamerMode.On;
        Mask();
        StreamerMode.Changed += Mask;
        TreeExiting += () => StreamerMode.Changed -= Mask;
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
        // Opens the GitHub issues page in the browser, where a bug can be reported (or an existing report found).
        var bug = UiKit.Button("Report Bug", () => OS.ShellOpen(IssuesUrl));
        bug.TooltipText = "Opens the Brawler issues page on GitHub in your browser";
        var quit = UiKit.Button("Quit", () => GetTree().Quit());
        options.SizeFlagsHorizontal = bug.SizeFlagsHorizontal = quit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bottomRow.AddChild(options);
        bottomRow.AddChild(bug);
        bottomRow.AddChild(quit);
        body.AddChild(bottomRow);

        var version = UiKit.Label($"v{NetworkManager.Version}", UiKit.Tiny, Palette.TextDim with { A = 0.6f }, HorizontalAlignment.Right);
        version.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        version.OffsetLeft = -120;
        version.OffsetRight = -10;
        version.OffsetTop = -26;
        version.OffsetBottom = -8;
        AddChild(version);

        // The tip card steps aside when the menu grows into its corner and it would cover something (a big UI scale, a
        // small window): the character or the play panel.
        var tip = TipCard();
        AddChild(tip);
        void FitTip()
        {
            tip.Visible = true;
            Callable.From(() =>
            {
                var r = Local(tip);
                tip.Visible = !r.Intersects(Local(body)) && !r.Intersects(Local(left));
            }).CallDeferred();
        }
        Resized += FitTip;
        Accessibility.Changed += FitTip;
        TreeExiting += () => Accessibility.Changed -= FitTip;

        VisibilityChanged += RefreshPreview;
    }

    /// <summary>Where <paramref name="c"/> sits in this menu's own coordinates.</summary>
    private Rect2 Local(Control c) => GetGlobalTransform().AffineInverse() * c.GetGlobalTransform() * new Rect2(Vector2.Zero, c.Size);

    /// <summary>
    /// Bottom-left, out of the way of the menu: a PayPal QR code with a short, no-pressure ask for a tip. It used to sit
    /// top-right, where it ran into the name box.
    /// </summary>
    private static Control TipCard()
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.07f, 0.7f),
            BorderColor = Palette.Accent with { A = 0.35f },
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 10, ContentMarginTop = 10, ContentMarginRight = 12, ContentMarginBottom = 10,
        });
        card.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        card.GrowVertical = GrowDirection.Begin;
        card.OffsetLeft = 14;
        card.OffsetBottom = -14;
        card.MouseFilter = MouseFilterEnum.Ignore;

        var row = UiKit.HBox(12);
        card.AddChild(row);

        // The code sits on white so phones can read it against the dark menu.
        var qrBack = new PanelContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        qrBack.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Colors.White,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 3, ContentMarginTop = 3, ContentMarginRight = 3, ContentMarginBottom = 3,
        });
        qrBack.AddChild(new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/ui/paypal_qr.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(68, 68),
            TextureFilter = TextureFilterEnum.LinearWithMipmaps,
        });
        row.AddChild(qrBack);

        var text = UiKit.VBox(4);
        text.Alignment = BoxContainer.AlignmentMode.Center;
        text.AddChild(UiKit.Label("ENJOYING BRAWLER?", UiKit.Small, Palette.Accent, font: Fonts.Bold));
        text.AddChild(UiKit.Label("Free forever. Tips welcome!", UiKit.Tiny, Palette.Text));
        text.AddChild(UiKit.Label("Scan to tip with PayPal", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        row.AddChild(text);
        return card;
    }

    public int Port => int.TryParse(PortEdit.Text, out int p) && p > 0 && p < 65536 ? p : NetworkManager.DefaultPort;

    public void SetStatus(string text) => _status.Text = text ?? "";

    /// <summary>The line under Daily Challenge: today's mode and cards, and your best so far.</summary>
    public void SetDaily(string text) { if (_daily != null) _daily.Text = text ?? ""; }

    private void RefreshPreview()
    {
        if (!Visible || _preview == null) return;
        _preview.Look = _net.LocalLook;
        _preview.ColorIdx = _net.LocalColor;
        _preview.QueueRedraw();
    }
}
