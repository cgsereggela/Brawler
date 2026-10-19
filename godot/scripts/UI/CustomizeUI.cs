using System;
using System.Collections.Generic;
using Brawler.Core;
using Brawler.Fx;
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
    private HBoxContainer _bodySwatches;
    private readonly Dictionary<LookPart, Label> _values = new();
    private readonly Dictionary<LookPart, HBoxContainer> _swatches = new();
    private readonly Rng _rng = new((ulong)DateTime.Now.Ticks);
    private Appearance _look;

    public CustomizeUI(NetworkManager net) => _net = net;
    public CustomizeUI() { }

    public override void _Ready()
    {
        var (_, body) = UiKit.CenteredPanel(this, 600);
        body.AddChild(new TitleText("CUSTOMIZE", 30, 1.5f, Palette.Hot, Palette.Accent));

        var cols = UiKit.HBox(22);
        body.AddChild(cols);

        var left = UiKit.VBox(4);
        _preview = new AvatarPreview(_net.LocalLook, _net.LocalColor, 6.5f, alive: true) { Pedestal = true };
        left.AddChild(_preview);
        left.AddChild(UiKit.Label("move the mouse to look around", UiKit.Tiny, Palette.TextDim, HorizontalAlignment.Center));
        cols.AddChild(left);

        var grid = new GridContainer { Columns = 2, SizeFlagsVertical = SizeFlags.ShrinkCenter, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 7);
        cols.AddChild(grid);

        grid.AddChild(RowLabel("Body"));
        _bodySwatches = UiKit.HBox(5);
        grid.AddChild(_bodySwatches);
        foreach (LookPart part in Enum.GetValues<LookPart>())
        {
            var p = part;
            grid.AddChild(RowLabel(Appearance.PartLabel(p)));
            if (p is LookPart.HairColor or LookPart.HatColor)
            {
                _swatches[p] = UiKit.HBox(4);
                grid.AddChild(_swatches[p]);
            }
            else grid.AddChild(Stepper(p));
        }

        var buttons = UiKit.HBox(8);
        buttons.AddChild(UiKit.Button("Randomize", Randomize));
        var done = UiKit.Primary("DONE", Close);
        done.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        buttons.AddChild(done);
        body.AddChild(buttons);

        VisibilityChanged += () => { if (Visible) Open(); };
        Open();
    }

    private static Label RowLabel(string name)
    {
        var l = UiKit.Label(name.ToUpperInvariant(), UiKit.Tiny, Palette.TextDim, font: Fonts.Bold);
        l.VerticalAlignment = VerticalAlignment.Center;
        return l;
    }

    private Control Stepper(LookPart part)
    {
        var row = UiKit.HBox(4);
        row.AddChild(UiKit.Button("<", () => Change(part, -1), 0, "Small"));
        var value = UiKit.Label("", UiKit.Body, Colors.White, HorizontalAlignment.Center, Fonts.Bold);
        value.CustomMinimumSize = new Vector2(110, 0);
        value.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(value);
        row.AddChild(UiKit.Button(">", () => Change(part, 1), 0, "Small"));
        _values[part] = value;
        return row;
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

    private void Set(LookPart part, int value)
    {
        _look[part] = value;
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
        _net.SetLocalColor(_rng.Range(0, Palette.Players.Length - 1));
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
        foreach (var (part, label) in _values) label.Text = _look.OptionName(part);

        UiKit.ClearChildren(_bodySwatches);
        for (int i = 0; i < Palette.Players.Length; i++)
        {
            int c = i;
            _bodySwatches.AddChild(UiKit.Swatch(Palette.Players[i], i == _net.LocalColor, () => SetColor(c), 22));
        }
        foreach (var (part, row) in _swatches)
        {
            UiKit.ClearChildren(row);
            for (int i = 0; i < Appearance.Dyes.Length; i++)
            {
                int d = i;
                var p = part;
                row.AddChild(UiKit.Swatch(Palette.Dye(i), i == _look[part], () => Set(p, d), 17));
            }
        }
    }

    private void Close()
    {
        Profile.Save(_net.LocalName, _net.LocalColor, _net.LocalLook);
        Closed?.Invoke();
    }
}
