using System;
using System.Collections.Generic;
using Brawler.Core;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

/// <summary>Character editor: body color plus every <see cref="LookPart"/>, with a live preview. Saves the profile on Done.</summary>
public partial class CustomizeUI : Control
{
    public event Action Closed;

    private readonly NetworkManager _net;
    private AvatarPreview _preview;
    private Label _colorValue;
    private readonly Dictionary<LookPart, Label> _values = new();
    private readonly Rng _rng = new((ulong)DateTime.Now.Ticks);
    private Appearance _look;

    public CustomizeUI(NetworkManager net) => _net = net;
    public CustomizeUI() { }

    public override void _Ready()
    {
        var (_, body) = UiKit.CenteredPanel(this, 330);
        body.AddChild(UiKit.Label("CUSTOMIZE", 20, Palette.Accent, HorizontalAlignment.Center));

        var cols = UiKit.HBox(12);
        body.AddChild(cols);

        // Preview on a little pedestal.
        var left = UiKit.VBox(2);
        _preview = new AvatarPreview(_net.LocalLook, _net.LocalColor, 4, alive: true);
        left.AddChild(_preview);
        left.AddChild(new ColorRect { Color = Palette.SolidLight, CustomMinimumSize = new Vector2(0, 3) });
        left.AddChild(UiKit.Label("move the mouse to look around", 8, Palette.TextDim, HorizontalAlignment.Center));
        cols.AddChild(left);

        var grid = new GridContainer { Columns = 4, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 3);
        cols.AddChild(grid);

        _colorValue = AddRow(grid, "Color", d => SetColor(_net.LocalColor + d));
        foreach (LookPart part in Enum.GetValues<LookPart>())
        {
            var p = part;
            _values[p] = AddRow(grid, Appearance.PartLabel(p), d => Change(p, d));
        }

        var buttons = UiKit.HBox();
        buttons.AddChild(UiKit.Button("Randomize", Randomize));
        var done = UiKit.Button("Done", Close);
        done.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        buttons.AddChild(done);
        body.AddChild(buttons);

        VisibilityChanged += () => { if (Visible) Open(); };
        Open();
    }

    private Label AddRow(GridContainer grid, string name, Action<int> step)
    {
        grid.AddChild(UiKit.Label(name, 10, Palette.TextDim));
        grid.AddChild(UiKit.Button("<", () => step(-1)));
        var value = UiKit.Label("", 10, Palette.Text, HorizontalAlignment.Center);
        value.CustomMinimumSize = new Vector2(64, 0);
        grid.AddChild(value);
        grid.AddChild(UiKit.Button(">", () => step(1)));
        return value;
    }

    /// <summary>Start editing from the current profile.</summary>
    private void Open()
    {
        if (_preview == null) return;
        _look = _net.LocalLook.Clone();
        Refresh();
    }

    private void Change(LookPart part, int delta)
    {
        _look[part] = _look[part] + delta;
        Apply();
    }

    private void SetColor(int idx)
    {
        int n = Palette.Players.Length;
        _net.SetLocalColor(((idx % n) + n) % n);
        Refresh();
    }

    private void Randomize()
    {
        _look = Appearance.Random(_rng);
        Apply();
    }

    private void Apply()
    {
        _net.SetLocalLook(_look);
        Refresh();
    }

    private void Refresh()
    {
        _preview.Look = _look;
        _preview.ColorIdx = _net.LocalColor;
        _preview.QueueRedraw();
        _colorValue.Text = Palette.Names[_net.LocalColor];
        _colorValue.AddThemeColorOverride("font_color", Palette.Player(_net.LocalColor));
        foreach (var (part, label) in _values) label.Text = _look.OptionName(part);
    }

    private void Close()
    {
        Profile.Save(_net.LocalName, _net.LocalColor, _net.LocalLook);
        Closed?.Invoke();
    }
}
