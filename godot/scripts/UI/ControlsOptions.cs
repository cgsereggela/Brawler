using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// The Controls tab of OptionsUI: three slots per action. Click a slot, then press a key or mouse button to bind it;
/// right-click clears it and Esc cancels. Every change is saved at once.
/// </summary>
public static class ControlsOptions
{
    /// <summary>True while a slot waits for a press; other Esc handlers (like the in-game overlay) should stand aside.</summary>
    public static bool Capturing => ControlsPanel.Active != null;

    public static void Build(Container parent) => parent.AddChild(new ControlsPanel());
}

public partial class ControlsPanel : VBoxContainer
{
    internal static ControlsPanel Active;

    private static readonly (string Action, string Name)[] Rows = new[]
    {
        ("move_left", "Move left"), ("move_right", "Move right"), ("jump", "Jump"), ("shoot", "Shoot"), ("block", "Block"), ("reload", "Reload"),
    }.Concat(Enumerable.Range(0, Emotes.All.Length).Select(i => (Emotes.Action(i), $"Say {Emotes.All[i].Text}")))
     .Append(("ping", "Ping")).ToArray();

    private readonly Dictionary<(string, int), Button> _slots = new();
    private readonly Dictionary<string, Label> _names = new();
    private Label _note;
    private (string Action, int Slot)? _capture;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        AddChild(UiKit.Label("Click a slot, then press a key or mouse button.\nRight-click a slot to clear it. Esc cancels.",
            UiKit.Small, Palette.TextDim, HorizontalAlignment.Center));

        var grid = new GridContainer { Columns = 1 + BindTable.Slots };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 3); // tight, so all ten rows fit the panel
        AddChild(grid);
        foreach (var (action, name) in Rows)
        {
            var l = UiKit.Label(name, UiKit.Body, Palette.Text);
            l.CustomMinimumSize = new Vector2(84, 0);
            _names[action] = l;
            grid.AddChild(l);
            for (int i = 0; i < BindTable.Slots; i++)
            {
                var key = (action, i);
                var b = UiKit.Button("", () => StartCapture(key), 0, "Small");
                b.CustomMinimumSize = new Vector2(0, 23);
                b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                b.ClipText = true;
                b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                b.FocusMode = FocusModeEnum.None;
                b.AddThemeFontSizeOverride("font_size", 13);
                b.TooltipText = "Click to rebind, right-click to clear";
                b.GuiInput += e =>
                {
                    if (_capture != null || e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
                    Keybinds.Clear(key.action, key.i);
                    _note.Text = "";
                    Refresh();
                    b.AcceptEvent();
                };
                _slots[key] = b;
                grid.AddChild(b);
            }
        }

        // Controllers have a fixed layout (not rebindable): just show it.
        AddChild(UiKit.Label(PadLayout.Summary, UiKit.Tiny, Palette.TextDim, HorizontalAlignment.Center));

        _note = UiKit.Label("", UiKit.Small, Palette.Accent, HorizontalAlignment.Center);
        _note.CustomMinimumSize = new Vector2(0, 16);
        AddChild(_note);

        var reset = UiKit.Button("Reset to defaults", () =>
        {
            EndCapture();
            Keybinds.ResetToDefaults();
            _note.Text = "Default controls restored";
            Refresh();
        }, 0, "Small");
        reset.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        AddChild(reset);
        Refresh();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && !IsVisibleInTree()) EndCapture();
        else if (what == NotificationExitTree && Active == this) Active = null;
    }

    private void StartCapture((string, int) slot)
    {
        _capture = slot;
        Active = this;
        _note.Text = "";
        Refresh();
    }

    private void EndCapture()
    {
        if (_capture == null) return;
        _capture = null;
        if (Active == this) Active = null;
        Refresh();
    }

    /// <summary>While capturing, takes the next key or mouse press before the buttons and the Esc-to-close handlers see it.</summary>
    public override void _Input(InputEvent e)
    {
        if (_capture is not { } c || !IsVisibleInTree()) return;
        bool press = e is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true };
        if (!press || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight })
            return;
        GetViewport().SetInputAsHandled();
        if (e is InputEventKey k && (k.Keycode == Key.Escape || k.PhysicalKeycode == Key.Escape))
        {
            EndCapture();
            return;
        }
        var b = Keybinds.FromEvent(e);
        if (b.IsNone) return; // echo or a key with no code: keep waiting
        string from = Keybinds.Assign(c.Action, c.Slot, b);
        _note.Text = from != null ? $"{Keybinds.Label(b)} moved from {NameOf(from)}" : "";
        EndCapture();
    }

    private static string NameOf(string action) => Rows.First(r => r.Action == action).Name;

    private void Refresh()
    {
        var t = Keybinds.Current;
        foreach (var ((action, i), b) in _slots)
        {
            var bind = t.Get(action, i);
            bool capturing = _capture == (action, i);
            b.Text = capturing ? "press a key…" : bind.IsNone ? "—" : Keybinds.Label(bind);
            var color = capturing ? Palette.Cool : bind.IsNone ? Palette.TextDim with { A = 0.6f } : Palette.Text;
            b.AddThemeColorOverride("font_color", color);
            b.AddThemeColorOverride("font_hover_color", capturing ? color : Colors.White);
            if (capturing)
            {
                var lit = b.GetThemeStylebox("pressed");
                b.AddThemeStyleboxOverride("normal", lit);
                b.AddThemeStyleboxOverride("hover", lit);
            }
            else
            {
                b.RemoveThemeStyleboxOverride("normal");
                b.RemoveThemeStyleboxOverride("hover");
            }
        }
        // An action with nothing bound shows in the hot color, so a moved key can't leave it unnoticed.
        foreach (var (action, l) in _names)
            l.AddThemeColorOverride("font_color", t.IsUnbound(action) ? Palette.Hot : Palette.Text);
        if (_capture == null && _note.Text.Length == 0)
        {
            var unbound = Rows.Where(r => t.IsUnbound(r.Action)).Select(r => r.Name).ToList();
            if (unbound.Count > 0) _note.Text = $"{string.Join(", ", unbound)}: nothing bound";
        }
    }
}
