using Brawler.Core;
using Godot;

namespace Brawler.UI;

/// <summary>The comfort rows of the Video tab in OptionsUI: a screen shake slider and a Reduce flashes toggle. Both apply live and save.</summary>
public static class AccessibilityOptions
{
    public static void Build(Container parent)
    {
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 12);
        parent.AddChild(grid);

        var l = UiKit.Label("Shake", UiKit.Body, Palette.Text);
        l.CustomMinimumSize = new Vector2(82, 0);
        grid.AddChild(l);
        var slider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = Mathf.Round(Accessibility.Shake * 100),
            CustomMinimumSize = new Vector2(190, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var pct = UiKit.Label($"{slider.Value:0}%", UiKit.Small, Palette.TextDim);
        pct.CustomMinimumSize = new Vector2(40, 0);
        // Saved once a drag ends (not on every tick of it); clicks and keys save right away.
        bool dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += _ => { dragging = false; Accessibility.Save(); };
        slider.ValueChanged += v =>
        {
            Accessibility.Shake = (float)v / 100f;
            pct.Text = $"{v:0}%";
            if (!dragging) Accessibility.Save();
        };
        grid.AddChild(slider);
        grid.AddChild(pct);

        var flashes = new CheckButton { Text = "Reduce flashes", ButtonPressed = Accessibility.ReduceFlashes };
        flashes.AddThemeFontSizeOverride("font_size", UiKit.Body);
        flashes.TooltipText = "No full-screen flashes, no white flash on hit players, and dimmer glow bursts.";
        flashes.Toggled += on => Accessibility.ReduceFlashes = on;
        parent.AddChild(flashes);
    }
}
