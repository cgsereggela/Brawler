using System;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>The modern UI theme (rounded glass panels, bold sans text, vivid accents) and small builders for screens.</summary>
public static class UiKit
{
    public const int Body = 15, Small = 12, Tiny = 11, HeadingSize = 30;

    public static Theme BuildTheme()
    {
        var t = new Theme { DefaultFont = Fonts.Regular, DefaultFontSize = Body };

        StyleBoxFlat Box(Color bg, Color border, int radius = 10, int padX = 14, int padY = 7, int borderW = 1) => new()
        {
            BgColor = bg, BorderColor = border,
            BorderWidthLeft = borderW, BorderWidthRight = borderW, BorderWidthTop = borderW, BorderWidthBottom = borderW,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY,
            AntiAliasing = true,
        };

        // Buttons: frosted glass that lights up on hover.
        t.SetStylebox("normal", "Button", Box(new Color(1, 1, 1, 0.08f), new Color(1, 1, 1, 0.16f)));
        t.SetStylebox("hover", "Button", Box(new Color(1, 1, 1, 0.16f), Palette.Cool with { A = 0.9f }));
        t.SetStylebox("pressed", "Button", Box(Palette.Cool with { A = 0.3f }, Palette.Cool));
        t.SetStylebox("disabled", "Button", Box(new Color(1, 1, 1, 0.03f), new Color(1, 1, 1, 0.06f)));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetFont("font", "Button", Fonts.Bold);
        t.SetColor("font_color", "Button", Palette.Text);
        t.SetColor("font_hover_color", "Button", Colors.White);
        t.SetColor("font_pressed_color", "Button", Colors.White);
        t.SetColor("font_disabled_color", "Button", Palette.TextDim with { A = 0.6f });

        // Primary: the hot call to action.
        t.SetTypeVariation("Primary", "Button");
        var hot = Palette.Hot;
        var primary = Box(hot, hot.Lightened(0.25f), 10, 18, 9);
        primary.ShadowColor = hot with { A = 0.25f };
        primary.ShadowSize = 10;
        t.SetStylebox("normal", "Primary", primary);
        var primaryHover = (StyleBoxFlat)primary.Duplicate();
        primaryHover.BgColor = hot.Lightened(0.15f);
        primaryHover.ShadowSize = 16;
        t.SetStylebox("hover", "Primary", primaryHover);
        t.SetStylebox("pressed", "Primary", Box(hot.Darkened(0.15f), hot.Lightened(0.3f), 10, 18, 9));
        t.SetStylebox("disabled", "Primary", Box(hot with { A = 0.25f }, hot with { A = 0.3f }, 10, 18, 9));
        // Dark ink on the pale button.
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            t.SetColor(state, "Primary", new Color("#1a1620"));
        t.SetFont("font", "Primary", Fonts.Heavy);
        t.SetFontSize("font_size", "Primary", 17);

        // Small: compact arrow buttons.
        t.SetTypeVariation("Small", "Button");
        t.SetStylebox("normal", "Small", Box(new Color(1, 1, 1, 0.08f), new Color(1, 1, 1, 0.14f), 8, 9, 3));
        t.SetStylebox("hover", "Small", Box(new Color(1, 1, 1, 0.18f), Palette.Cool, 8, 9, 3));
        t.SetStylebox("pressed", "Small", Box(Palette.Cool with { A = 0.35f }, Palette.Cool, 8, 9, 3));

        t.SetStylebox("normal", "LineEdit", Box(new Color(0.03f, 0.02f, 0.08f, 0.65f), new Color(1, 1, 1, 0.14f), 8, 10, 6));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0.03f, 0.02f, 0.08f, 0.8f), Palette.Cool, 8, 10, 6, 2));
        t.SetStylebox("read_only", "LineEdit", Box(new Color(0.03f, 0.02f, 0.08f, 0.4f), new Color(1, 1, 1, 0.06f), 8, 10, 6));
        t.SetColor("font_color", "LineEdit", Palette.Text);
        t.SetColor("caret_color", "LineEdit", Palette.Cool);
        t.SetColor("selection_color", "LineEdit", Palette.Cool with { A = 0.35f });

        // Panels: dark glass with a soft shadow.
        var panel = Box(new Color(0.05f, 0.05f, 0.07f, 0.86f), new Color(0.93f, 0.9f, 0.84f, 0.16f), 18, 22, 18);
        panel.ShadowColor = new Color(0, 0, 0, 0.45f);
        panel.ShadowSize = 28;
        panel.ShadowOffset = new Vector2(0, 8);
        t.SetStylebox("panel", "PanelContainer", panel);

        // Sliders: a thin glass track that fills with the cool accent, and a round knob.
        t.SetStylebox("slider", "HSlider", Box(new Color(1, 1, 1, 0.1f), new Color(1, 1, 1, 0.14f), 4, 0, 3));
        t.SetStylebox("grabber_area", "HSlider", Box(Palette.Cool with { A = 0.7f }, Palette.Cool, 4, 0, 3));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Palette.Cool, Palette.Cool.Lightened(0.3f), 4, 0, 3));
        t.SetIcon("grabber", "HSlider", Knob(14, Palette.Text));
        t.SetIcon("grabber_highlight", "HSlider", Knob(16, Colors.White));

        t.SetColor("font_color", "Label", Palette.Text);
        return t;
    }

    private static ImageTexture Knob(int size, Color color)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = new Vector2(x + 0.5f - r, y + 0.5f - r).Length();
            img.SetPixel(x, y, color with { A = Mathf.Clamp(r - d, 0, 1) });
        }
        return ImageTexture.CreateFromImage(img);
    }

    public static Label Label(string text, int size = Body, Color? color = null, HorizontalAlignment align = HorizontalAlignment.Left, Font font = null)
    {
        var l = new Label { Text = text, HorizontalAlignment = align };
        if (size != Body) l.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) l.AddThemeColorOverride("font_color", c);
        if (font != null) l.AddThemeFontOverride("font", font);
        return l;
    }

    public static Label Heading(string text, int size = HeadingSize, Color? color = null)
    {
        var l = Label(text, size, color ?? Colors.White, HorizontalAlignment.Center, Fonts.Heavy);
        l.AddThemeColorOverride("font_shadow_color", (color ?? Palette.Hot) with { A = 0.45f });
        l.AddThemeConstantOverride("shadow_offset_y", 3);
        l.AddThemeConstantOverride("shadow_offset_x", 0);
        return l;
    }

    public static Button Button(string text, Action onPress, int minWidth = 0, string variation = null)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(minWidth, 0) };
        if (variation != null) b.ThemeTypeVariation = variation;
        b.Pressed += onPress;
        Bounce(b);
        return b;
    }

    public static Button Primary(string text, Action onPress, int minWidth = 0) => Button(text, onPress, minWidth, "Primary");

    /// <summary>Grows a control slightly while hovered.</summary>
    public static void Bounce(Control c, float scale = 1.05f)
    {
        c.Resized += () => c.PivotOffset = c.Size / 2;
        Tween tw = null;
        void To(float s)
        {
            if (!c.IsInsideTree()) return;
            tw?.Kill();
            tw = c.CreateTween();
            tw.TweenProperty(c, "scale", Vector2.One * s, 0.12f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
        c.MouseEntered += () => { if (c is not BaseButton { Disabled: true }) To(scale); };
        c.MouseExited += () => To(1f);
    }

    /// <summary>A round color button; <paramref name="selected"/> gets a white ring.</summary>
    public static Button Swatch(Color color, bool selected, Action onPress, int size = 20)
    {
        StyleBoxFlat Dot(Color border, int bw) => new()
        {
            BgColor = color, BorderColor = border,
            BorderWidthLeft = bw, BorderWidthRight = bw, BorderWidthTop = bw, BorderWidthBottom = bw,
            CornerRadiusTopLeft = size, CornerRadiusTopRight = size, CornerRadiusBottomLeft = size, CornerRadiusBottomRight = size,
            AntiAliasing = true,
        };
        var b = new Button { CustomMinimumSize = new Vector2(size, size), FocusMode = Control.FocusModeEnum.None };
        var ring = selected ? Dot(Colors.White, 3) : Dot(new Color(1, 1, 1, 0.22f), 1);
        b.AddThemeStyleboxOverride("normal", ring);
        b.AddThemeStyleboxOverride("hover", Dot(Colors.White, selected ? 3 : 2));
        b.AddThemeStyleboxOverride("pressed", Dot(Colors.White, 3));
        b.Pressed += onPress;
        Bounce(b, 1.15f);
        return b;
    }

    public static VBoxContainer VBox(int sep = 6)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        return v;
    }

    public static HBoxContainer HBox(int sep = 6)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        return h;
    }

    public static Control Spacer(float h) => new() { CustomMinimumSize = new Vector2(0, h) };

    /// <summary>A full-screen root that centers one glass panel.</summary>
    public static (Control root, VBoxContainer body) CenteredPanel(Control screen, int minWidth = 320)
    {
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(minWidth, 0) };
        center.AddChild(panel);
        var body = VBox(8);
        panel.AddChild(body);
        return (screen, body);
    }

    public static Color CategoryColor(CardCategory c) => c switch
    {
        CardCategory.Gun => new Color("#ff8a3d"),
        CardCategory.Body => new Color("#4ef08a"),
        CardCategory.Movement => new Color("#3db4ff"),
        CardCategory.Block => new Color("#ffd447"),
        _ => new Color("#c46cff"),
    };

    public static void ClearChildren(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            n.RemoveChild(c);
            c.QueueFree();
        }
    }
}

/// <summary>Big display text: chunky letters with a thick outline, a colored drop shadow and an optional wave.</summary>
public partial class TitleText : Control
{
    public string Text;
    public int FontSize;
    public Color[] Colors;
    public float Wave;

    private float _t;

    public TitleText() { }

    public TitleText(string text, int size, float wave = 0, params Color[] colors)
    {
        Text = text;
        FontSize = size;
        Wave = wave;
        Colors = colors.Length > 0 ? colors : new[] { Godot.Colors.White };
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(Fonts.Heavy.GetStringSize(text, HorizontalAlignment.Left, -1, size).X + size * 0.3f, size * 1.25f);
    }

    public void SetText(string text, params Color[] colors)
    {
        Text = text;
        if (colors.Length > 0) Colors = colors;
        CustomMinimumSize = new Vector2(Fonts.Heavy.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize).X + FontSize * 0.3f, FontSize * 1.25f);
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        _t += (float)delta;
        if (Wave > 0) QueueRedraw();
    }

    public override void _Draw()
    {
        if (string.IsNullOrEmpty(Text)) return;
        var font = Fonts.Heavy;
        float total = font.GetStringSize(Text, HorizontalAlignment.Left, -1, FontSize).X;
        float x = (Size.X - total) / 2;
        float baseY = Size.Y / 2 + FontSize * 0.36f;
        int outline = Mathf.Max(4, FontSize / 7);
        for (int pass = 0; pass < 3; pass++)
        {
            float cx = x;
            for (int i = 0; i < Text.Length; i++)
            {
                string ch = Text[i].ToString();
                float w = font.GetStringSize(ch, HorizontalAlignment.Left, -1, FontSize).X;
                float y = baseY + Mathf.Sin(_t * 3f + i * 0.55f) * Wave;
                var col = Colors[i % Colors.Length];
                var at = new Vector2(cx, y);
                if (pass == 0)
                    DrawStringOutline(font, at + new Vector2(0, FontSize * 0.09f), ch, HorizontalAlignment.Left, -1, FontSize, outline, col.Darkened(0.55f));
                else if (pass == 1)
                    DrawStringOutline(font, at, ch, HorizontalAlignment.Left, -1, FontSize, outline, new Color(0.07f, 0.03f, 0.13f));
                else
                    DrawString(font, at, ch, HorizontalAlignment.Left, -1, FontSize, col.Lightened(0.08f));
                cx += w;
            }
        }
    }
}
