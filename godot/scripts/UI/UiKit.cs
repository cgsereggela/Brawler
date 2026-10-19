using System;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>Pixel-style theme and small builders so screens can be assembled in code.</summary>
public static class UiKit
{
    public static Theme BuildTheme()
    {
        var t = new Theme { DefaultFontSize = 10 };

        StyleBoxFlat Box(Color bg, Color border, int pad = 4) => new()
        {
            BgColor = bg, BorderColor = border,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = pad + 2, ContentMarginRight = pad + 2, ContentMarginTop = pad - 1, ContentMarginBottom = pad - 1,
        };

        t.SetStylebox("normal", "Button", Box(Palette.Solid, Palette.SolidLight));
        t.SetStylebox("hover", "Button", Box(Palette.SolidLight, Palette.Text));
        t.SetStylebox("pressed", "Button", Box(Palette.Accent.Darkened(0.4f), Palette.Accent));
        t.SetStylebox("disabled", "Button", Box(Palette.SolidDark, Palette.Solid));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetColor("font_color", "Button", Palette.Text);
        t.SetColor("font_hover_color", "Button", Colors.White);
        t.SetColor("font_disabled_color", "Button", Palette.TextDim);

        t.SetStylebox("normal", "LineEdit", Box(Palette.SolidDark, Palette.SolidLight));
        t.SetStylebox("focus", "LineEdit", Box(Palette.SolidDark, Palette.Accent));
        t.SetColor("font_color", "LineEdit", Palette.Text);

        t.SetStylebox("panel", "PanelContainer", Box(new Color(0.08f, 0.08f, 0.12f, 0.96f), Palette.SolidLight, 8));
        t.SetColor("font_color", "Label", Palette.Text);
        return t;
    }

    public static Label Label(string text, int size = 10, Color? color = null, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = new Label { Text = text, HorizontalAlignment = align };
        if (size != 10) l.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) l.AddThemeColorOverride("font_color", c);
        return l;
    }

    public static Button Button(string text, Action onPress, int minWidth = 0)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(minWidth, 0) };
        b.Pressed += onPress;
        return b;
    }

    public static VBoxContainer VBox(int sep = 4)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        return v;
    }

    public static HBoxContainer HBox(int sep = 4)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        return h;
    }

    public static ColorRect Swatch(Color c, int size = 8) => new() { Color = c, CustomMinimumSize = new Vector2(size, size) };

    /// <summary>A full-screen root that centers one panel.</summary>
    public static (Control root, VBoxContainer body) CenteredPanel(Control screen, int minWidth = 260)
    {
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(minWidth, 0) };
        center.AddChild(panel);
        var body = VBox(5);
        panel.AddChild(body);
        return (screen, body);
    }

    public static Color CategoryColor(CardCategory c) => c switch
    {
        CardCategory.Bullet => new Color("#ff9233"),
        CardCategory.Body => new Color("#4ee06e"),
        CardCategory.Weapon => new Color("#3d9bff"),
        _ => new Color("#b46cff"),
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
