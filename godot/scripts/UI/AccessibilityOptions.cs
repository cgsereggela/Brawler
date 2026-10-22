using Brawler.Core;
using Godot;

namespace Brawler.UI;

/// <summary>
/// The comfort rows of the Video tab in OptionsUI: screen shake and controller aim assist sliders, the UI scale, and
/// Reduce flashes and Colorblind-safe colors toggles. All apply live and save.
/// </summary>
public static class AccessibilityOptions
{
    public static void Build(Container parent)
    {
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 12);
        parent.AddChild(grid);

        Slider(grid, "Shake", Accessibility.Shake, v => Accessibility.Shake = v, null);
        Slider(grid, "Aim assist", Accessibility.AimAssist, v => Accessibility.AimAssist = v,
            "Controllers only: while the right stick aims, it bends toward the enemy nearest that line. 0% is off.");

        var scaleLabel = UiKit.Label("UI scale", UiKit.Body, Palette.Text);
        grid.AddChild(scaleLabel);
        var scales = UiKit.HBox(6);
        foreach (float s in Accessibility.UiScales)
        {
            float pick = s;
            var b = UiKit.Button($"{s * 100:0}%", () => Accessibility.UiScale = pick, 0, "Small");
            b.ToggleMode = true;
            b.ButtonPressed = Mathf.IsEqualApprox(s, Accessibility.UiScale);
            b.TooltipText = "Bigger menus and HUD";
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scales.AddChild(b);
        }
        // Keep the buttons acting like one choice, also when the scale changes from elsewhere.
        void ShowScale()
        {
            int i = 0;
            foreach (var c in scales.GetChildren())
                if (c is Button b && IsInstanceValid(b)) b.SetPressedNoSignal(Mathf.IsEqualApprox(Accessibility.UiScales[i++], Accessibility.UiScale));
        }
        Accessibility.Changed += ShowScale;
        scales.TreeExiting += () => Accessibility.Changed -= ShowScale;
        scales.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(scales);
        grid.AddChild(new Control());

        var flashes = new CheckButton { Text = "Reduce flashes", ButtonPressed = Accessibility.ReduceFlashes };
        flashes.AddThemeFontSizeOverride("font_size", UiKit.Body);
        flashes.TooltipText = "No full-screen flashes, no white flash on hit players, and dimmer glow bursts.";
        flashes.Toggled += on => Accessibility.ReduceFlashes = on;
        var toggles = UiKit.HBox(16);
        toggles.AddChild(flashes);

        var colors = new CheckButton { Text = "Colorblind-safe colors", ButtonPressed = Accessibility.ColorblindColors };
        colors.AddThemeFontSizeOverride("font_size", UiKit.Body);
        colors.TooltipText = "Player and team colors that stay apart with red-green or blue-yellow color blindness. Only changes what you see.";
        colors.Toggled += on => Accessibility.ColorblindColors = on;
        toggles.AddChild(colors);
        parent.AddChild(toggles);
    }

    private static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);

    /// <summary>A labelled 0-100% slider. It is saved once a drag ends (not on every tick of it); clicks and keys save right away.</summary>
    private static void Slider(GridContainer grid, string name, float value, System.Action<float> set, string tip)
    {
        var l = UiKit.Label(name, UiKit.Body, Palette.Text);
        l.CustomMinimumSize = new Vector2(82, 0);
        if (tip != null) { l.TooltipText = tip; l.MouseFilter = Control.MouseFilterEnum.Pass; }
        grid.AddChild(l);
        var slider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = Mathf.Round(value * 100), TooltipText = tip ?? "",
            CustomMinimumSize = new Vector2(190, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var pct = UiKit.Label($"{slider.Value:0}%", UiKit.Small, Palette.TextDim);
        pct.CustomMinimumSize = new Vector2(40, 0);
        bool dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += _ => { dragging = false; Accessibility.Save(); };
        slider.ValueChanged += v =>
        {
            set((float)v / 100f);
            pct.Text = $"{v:0}%";
            if (!dragging) Accessibility.Save();
        };
        grid.AddChild(slider);
        grid.AddChild(pct);
    }
}
