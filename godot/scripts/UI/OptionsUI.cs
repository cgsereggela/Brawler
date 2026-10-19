using System;
using Brawler.Core;
using Brawler.Fx;
using Godot;

namespace Brawler.UI;

/// <summary>Volume sliders for master, music and sound effects. Changes apply live and are saved on Done.</summary>
public partial class OptionsUI : Control
{
    public event Action Closed;

    private readonly Sfx _sfx;
    private ulong _lastPreview;

    public OptionsUI(Sfx sfx) => _sfx = sfx;
    public OptionsUI() { }

    public override void _Ready()
    {
        var (_, body) = UiKit.CenteredPanel(this, 380);
        body.AddChild(new TitleText("OPTIONS", 30, 1.5f, Palette.Cool, Palette.Hot));
        body.AddChild(UiKit.Label("AUDIO", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 10);
        body.AddChild(grid);
        AddSlider(grid, "Master", AudioSettings.Master, v => AudioSettings.Master = v, "shoot");
        AddSlider(grid, "Music", AudioSettings.Music, v => AudioSettings.Music = v, null);
        AddSlider(grid, "Sound FX", AudioSettings.Effects, v => AudioSettings.Effects = v, "shoot");

        body.AddChild(UiKit.Spacer(4));
        var done = UiKit.Primary("DONE", () =>
        {
            AudioSettings.Save();
            Closed?.Invoke();
        });
        body.AddChild(done);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        AudioSettings.Save();
        Closed?.Invoke();
        GetViewport().SetInputAsHandled();
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
