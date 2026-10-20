using Brawler.Core;
using Godot;

namespace Brawler.UI;

/// <summary>The bloom row of the Video tab in OptionsUI: a 0..100% slider that applies live and saves on change.</summary>
public static class BloomOptions
{
    public static void Build(Container parent)
    {
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 12);
        parent.AddChild(grid);

        var l = UiKit.Label("Bloom", UiKit.Body, Palette.Text);
        l.CustomMinimumSize = new Vector2(82, 0);
        grid.AddChild(l);
        var slider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = Mathf.Round(BloomSettings.Amount * 100),
            CustomMinimumSize = new Vector2(190, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var pct = UiKit.Label($"{slider.Value:0}%", UiKit.Small, Palette.TextDim);
        pct.CustomMinimumSize = new Vector2(40, 0);
        // Saved once a drag ends (not on every tick of it); clicks and keys save right away.
        bool dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += _ => { dragging = false; BloomSettings.Save(); };
        slider.ValueChanged += v =>
        {
            BloomSettings.Amount = (float)v / 100f;
            BloomSettings.Apply();
            pct.Text = $"{v:0}%";
            if (!dragging) BloomSettings.Save();
        };
        grid.AddChild(slider);
        grid.AddChild(pct);
    }
}
