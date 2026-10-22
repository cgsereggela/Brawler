using System;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// Settings, in tabs: Audio (volume sliders), Video (window mode, bloom, shake, flashes), Controls (key bindings) and Online (streamer mode). Each tab's
/// rows are built by their own class (DisplayOptions, BloomOptions, AccessibilityOptions, ControlsOptions, PrivacyOptions). Audio applies live and is
/// saved on Done.
/// In overlay mode (Esc during a match) it dims the still-running game behind it and adds a Leave match button
/// with a confirm step.
/// </summary>
public partial class OptionsUI : Control
{
    public event Action Closed, LeaveRequested;

    private readonly Sfx _sfx;
    private ulong _lastPreview;
    private bool _overlay;
    private ColorRect _dim;
    private Control _leaveButton, _leaveConfirm, _done, _reset;
    private TabContainer _tabs;
    private Control _controlsTab;
    private HBoxContainer _arenaRow;
    private HBoxContainer _clipRow;
    private Button _clipButton;
    private Label _arenaLabel;
    private Button _arenaSave;
    private string _arenaCode;

    public OptionsUI(Sfx sfx) => _sfx = sfx;
    public OptionsUI() { }

    /// <summary>Overlay mode: dim backdrop and the Leave match button. Off for the full-screen menu and lobby options.</summary>
    public void SetOverlay(bool overlay)
    {
        _overlay = overlay;
        if (_dim == null) return;
        _dim.Visible = overlay;
        SetArena(overlay ? _arenaCode : null);
        ShowClip();
        ShowLeaveConfirm(false);
    }

    /// <summary>The battle's custom arena (a map code) for the overlay's Save and Copy, or null to hide them.</summary>
    public void SetArena(string code)
    {
        _arenaCode = code;
        if (_arenaRow == null) return;
        var d = code != null ? MapCode.Decode(code) : null;
        _arenaRow.Visible = _overlay && d != null && !OnControls;
        FitTabs();
        if (d == null) return;
        bool saved = MapLibrary.Holds(code);
        _arenaLabel.Text = $"Arena: {(d.Name != "" ? d.Name : "Untitled")}";
        _arenaSave.Text = saved ? "SAVED" : "SAVE ARENA";
        _arenaSave.Disabled = saved;
    }

    /// <summary>The Controls tab fills the panel, so the arena and kill cam rows step aside while it's open.</summary>
    private bool OnControls => _tabs != null && _tabs.GetCurrentTabControl() == _controlsTab;

    private void ShowClip()
    {
        if (_clipRow == null || !IsInstanceValid(_clipRow)) return;
        _clipRow.Visible = _overlay && KillCamClips.HasClip && !OnControls;
        _clipButton.Text = KillCamClips.ButtonText;
        _clipButton.Disabled = KillCamClips.Saving;
        FitTabs();
    }

    public override void _ExitTree() => KillCamClips.Changed -= ShowClip;

    private void SaveArena()
    {
        var d = MapCode.Decode(_arenaCode);
        if (d == null) return;
        d.Name = MapLibrary.FreeName(d.Name != "" ? d.Name : "Shared arena");
        bool ok = MapLibrary.Save(d);
        SetArena(_arenaCode);
        _arenaSave.Text = ok ? "SAVED" : "SAVE ARENA";
        _arenaSave.Disabled = ok;
        _arenaLabel.Text = ok ? $"Saved {d.Name} to My maps." : "Couldn't save the arena.";
    }

    public override void _Ready()
    {
        // Lets the running game show through, and swallows clicks meant for the screens behind (card pick, match over).
        _dim = new ColorRect { Color = new Color(0.02f, 0.01f, 0.06f, 0.45f), MouseFilter = MouseFilterEnum.Stop };
        _dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_dim);

        var (_, body) = UiKit.CenteredPanel(this, 460);
        body.AddChild(new TitleText("OPTIONS", 30, 1.5f, Palette.Cool, Palette.Hot));

        var tabs = _tabs = new TabContainer(); // as tall as its tallest tab (see FitTabs)
        body.AddChild(tabs);
        var audio = Tab(tabs, "Audio");
        var video = Tab(tabs, "Video");
        DisplayOptions.Build(video);
        BloomOptions.Build(video);
        AccessibilityOptions.Build(video);
        var controls = ControlsOptions.Build(Tab(tabs, "Controls"));
        _controlsTab = (Control)controls.GetParent().GetParent();
        PrivacyOptions.Build(Tab(tabs, "Online"));

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 10);
        audio.AddChild(grid);
        AddSlider(grid, "Master", AudioSettings.Master, v => AudioSettings.Master = v, "shoot");
        AddSlider(grid, "Music", AudioSettings.Music, v => AudioSettings.Music = v, null);
        AddSlider(grid, "Sound FX", AudioSettings.Effects, v => AudioSettings.Effects = v, "shoot");

        body.AddChild(UiKit.Spacer(4));
        // In a match on a custom arena: keep it, or copy its code for a friend.
        _arenaRow = UiKit.HBox(8);
        body.AddChild(_arenaRow);
        _arenaLabel = UiKit.Label("", UiKit.Small, Palette.TextDim);
        _arenaLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _arenaLabel.VerticalAlignment = VerticalAlignment.Center;
        _arenaLabel.ClipText = true;
        _arenaRow.AddChild(_arenaLabel);
        _arenaSave = NoFocus(UiKit.Button("SAVE ARENA", SaveArena, 0, "Small"));
        _arenaRow.AddChild(_arenaSave);
        _arenaRow.AddChild(NoFocus(UiKit.Button("COPY CODE", () =>
        {
            DisplayServer.ClipboardSet(_arenaCode);
            _arenaLabel.Text = "Code copied: friends paste it in their map editor or lobby.";
        }, 0, "Small")));

        // In a match, once a kill cam has played: save it as a GIF.
        _clipRow = UiKit.HBox(8);
        body.AddChild(_clipRow);
        var clipLabel = UiKit.Label("Last kill cam", UiKit.Small, Palette.TextDim);
        clipLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        clipLabel.VerticalAlignment = VerticalAlignment.Center;
        _clipRow.AddChild(clipLabel);
        _clipButton = NoFocus(UiKit.Button("", KillCamClips.Press, 0, "Small"));
        _clipRow.AddChild(_clipButton);
        KillCamClips.Changed += ShowClip;

        // Footer: Done fills it, sharing it with Reset to defaults on the Controls tab (the tab is too tall for a row of its own);
        // in a match Leave sits to its left and swaps the row for a confirm prompt.
        var footer = UiKit.HBox(10);
        body.AddChild(footer);
        _leaveButton = Danger(UiKit.Button("LEAVE MATCH", () => ShowLeaveConfirm(true), 150));
        footer.AddChild(_leaveButton);
        _reset = UiKit.Button("RESET TO DEFAULTS", controls.ResetToDefaults);
        _reset.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(_reset);
        tabs.TabChanged += _ =>
        {
            ShowLeaveConfirm(false);
            SetArena(_arenaCode);
            ShowClip();
            FitTabs();
        };
        Resized += FitTabs;
        _done = UiKit.Primary("DONE", Close);
        _done.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(_done);
        var confirm = UiKit.HBox(8);
        confirm.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _leaveConfirm = confirm;
        footer.AddChild(confirm);
        var ask = UiKit.Label("Leave match?", UiKit.Body, Palette.Text, HorizontalAlignment.Center, Fonts.Bold);
        ask.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ask.VerticalAlignment = VerticalAlignment.Center;
        confirm.AddChild(ask);
        confirm.AddChild(Danger(UiKit.Button("YES", () => { AudioSettings.Save(); LeaveRequested?.Invoke(); }, 80)));
        confirm.AddChild(NoFocus(UiKit.Button("NO", () => ShowLeaveConfirm(false), 80)));
        SetOverlay(_overlay);
    }

    private void Close()
    {
        AudioSettings.Save();
        Closed?.Invoke();
    }

    private void ShowLeaveConfirm(bool ask)
    {
        _leaveButton.Visible = _overlay && !ask;
        _done.Visible = !ask;
        _reset.Visible = !ask && _tabs.GetCurrentTabControl() == _controlsTab;
        // Same size as Reset beside it, even when Leave match takes a share of the row.
        _done.CustomMinimumSize = new Vector2(_reset.Visible ? _reset.GetCombinedMinimumSize().X : 0, 0);
        _leaveConfirm.Visible = ask;
    }

    // A focused button fires on Space or Enter, which also jump while the match runs behind the overlay.
    private static Button NoFocus(Button b)
    {
        b.FocusMode = FocusModeEnum.None;
        return b;
    }

    /// <summary>The frosted button tinted hot, for leaving.</summary>
    private static Button Danger(Button b)
    {
        StyleBoxFlat Box(float bg, float border) => new()
        {
            BgColor = Palette.Hot with { A = bg }, BorderColor = Palette.Hot with { A = border },
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 7, ContentMarginBottom = 7,
            AntiAliasing = true,
        };
        b.AddThemeStyleboxOverride("normal", Box(0.12f, 0.55f));
        b.AddThemeStyleboxOverride("hover", Box(0.28f, 1f));
        b.AddThemeStyleboxOverride("pressed", Box(0.45f, 1f));
        b.AddThemeColorOverride("font_color", Palette.Hot.Lightened(0.35f));
        return NoFocus(b);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        Close();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>A tab's rows, in a scroll box that only scrolls when the tab is taller than the window allows (see FitTabs).</summary>
    private VBoxContainer Tab(TabContainer tabs, string title)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(scroll);
        var box = UiKit.VBox(10);
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(box);
        _tabScrolls.Add(scroll);
        return box;
    }

    private readonly System.Collections.Generic.List<ScrollContainer> _tabScrolls = new();
    private bool _fitQueued;
    private int _fitPasses;

    /// <summary>
    /// Gives each tab its full height when the panel fits on screen, and otherwise only the height left over, so at a
    /// big UI scale a tall tab (Controls) scrolls instead of pushing the footer off the bottom.
    /// </summary>
    private void FitTabs()
    {
        if (_fitQueued) return;
        _fitQueued = true;
        Callable.From(() =>
        {
            _fitQueued = false;
            if (_tabs == null || !IsInstanceValid(_tabs)) return;
            var panel = (Control)_tabs.GetParent().GetParent();
            // Everything but the tab pages: the rest of the panel, the tab bar and the page frame.
            float tabBar = _tabs.GetTabBar().GetCombinedMinimumSize().Y + _tabs.GetThemeStylebox("panel").GetMinimumSize().Y;
            float chrome = panel.GetCombinedMinimumSize().Y - _tabs.GetCombinedMinimumSize().Y + tabBar, room = Size.Y - chrome;
            bool moved = false;
            foreach (var sc in _tabScrolls)
            {
                float want = ((Control)sc.GetChild(0)).GetCombinedMinimumSize().Y, h = Mathf.Clamp(room, 80, want);
                moved |= Mathf.Abs(sc.CustomMinimumSize.Y - h) > 0.5f;
                sc.CustomMinimumSize = new Vector2(0, h);
            }
            // A second pass settles it once the new heights are laid out.
            if (moved && ++_fitPasses < 4) FitTabs();
            else _fitPasses = 0;
        }).CallDeferred();
    }

    private void AddSlider(GridContainer grid, string label, float value, Action<float> set, string preview)
    {
        var l = UiKit.Label(label, UiKit.Body, Palette.Text);
        l.CustomMinimumSize = new Vector2(82, 0);
        grid.AddChild(l);
        var slider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = Mathf.Round(value * 100),
            CustomMinimumSize = new Vector2(190, 22), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        var pct = UiKit.Label($"{slider.Value:0}%", UiKit.Small, Palette.TextDim);
        pct.CustomMinimumSize = new Vector2(40, 0);
        slider.ValueChanged += v =>
        {
            set((float)v / 100f);
            AudioSettings.Apply();
            pct.Text = $"{v:0}%";
            // A short sample so you hear the level you're setting, at most a few times a second.
            ulong now = Time.GetTicksMsec();
            if (preview != null && now - _lastPreview > 250) { _lastPreview = now; _sfx?.Play(preview, 0.8f); }
        };
        grid.AddChild(slider);
        grid.AddChild(pct);
    }
}
