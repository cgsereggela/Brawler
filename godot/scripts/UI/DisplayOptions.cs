using Brawler.Core;
using Godot;
using WindowChoice = Brawler.Core.DisplaySettings.WindowChoice;

namespace Brawler.UI;

/// <summary>The window rows of the Video tab in OptionsUI: three window-mode buttons and the mouse lock, applied and saved at once.</summary>
public static class DisplayOptions
{
    public static void Build(Container parent)
    {
        var row = UiKit.HBox(6);
        var label = UiKit.Label("Window", UiKit.Body, Palette.Text);
        label.CustomMinimumSize = new Vector2(82, 0); // lines up with the Audio tab's labels
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(label);

        var group = new ButtonGroup();
        var buttons = new Button[3];
        string[] titles = { "Windowed", "Borderless", "Fullscreen" };
        string[] tips =
        {
            "A normal window with a frame",
            "A frameless window covering the whole screen; alt-tabs instantly",
            "Exclusive full screen; can be a little smoother, slower to alt-tab",
        };
        for (int i = 0; i < 3; i++)
        {
            var choice = (WindowChoice)i;
            Button b = null;
            b = UiKit.Button(titles[i], () => { if (b.ButtonPressed && DisplaySettings.Mode != choice) DisplaySettings.Set(choice); });
            b.ToggleMode = true;
            b.ButtonGroup = group;
            b.TooltipText = tips[i];
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.AddThemeFontSizeOverride("font_size", 14);
            // The theme has no hover_pressed box, so the selected button would fall back to the default grey on hover.
            b.TreeEntered += () => b.AddThemeStyleboxOverride("hover_pressed", b.GetThemeStylebox("pressed"));
            buttons[i] = b;
            row.AddChild(b);
        }

        void Refresh()
        {
            for (int i = 0; i < 3; i++) buttons[i].SetPressedNoSignal((int)DisplaySettings.Mode == i);
        }
        Refresh();
        // Alt+Enter can change the mode while the menu is open.
        row.TreeEntered += () => { DisplaySettings.Changed += Refresh; Refresh(); };
        row.TreeExiting += () => DisplaySettings.Changed -= Refresh;
        parent.AddChild(row);

        parent.AddChild(UiKit.Label("Alt+Enter switches between windowed and full screen", UiKit.Small, Palette.TextDim));

        var lockMouse = new CheckButton { Text = "Lock mouse in matches", ButtonPressed = DisplaySettings.LockMouse };
        lockMouse.AddThemeFontSizeOverride("font_size", UiKit.Body);
        lockMouse.TooltipText = "Keeps the pointer inside the game window while a match is on, so a click can't land on another window. Menus leave it free; Alt+Tab still works.";
        lockMouse.Toggled += DisplaySettings.SetLockMouse;
        var lockRow = UiKit.HBox(16); // natural width, like the Accessibility toggles
        lockRow.AddChild(lockMouse);
        parent.AddChild(lockRow);
    }
}
