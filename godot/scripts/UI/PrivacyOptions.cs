using Brawler.Core;
using Godot;

namespace Brawler.UI;

/// <summary>The Online tab of OptionsUI: the streamer mode toggle, which applies and saves at once.</summary>
public static class PrivacyOptions
{
    public static void Build(Container parent)
    {
        var toggle = new CheckButton { Text = "Streamer mode", ButtonPressed = StreamerMode.On };
        toggle.AddThemeFontSizeOverride("font_size", UiKit.Body);
        toggle.Toggled += on => StreamerMode.On = on;
        // Kept in step if something else flips it while the tab is open.
        void Sync() => toggle.SetPressedNoSignal(StreamerMode.On);
        toggle.TreeEntered += () => { StreamerMode.Changed += Sync; Sync(); };
        toggle.TreeExiting += () => StreamerMode.Changed -= Sync;
        parent.AddChild(toggle);

        var about = UiKit.Label(
            "Hides every IP address on screen. When you host, Copy puts an invite code on the clipboard instead of your address; "
            + "friends paste it into the join box like an address. Send the code privately, since it can be turned back into your address.",
            UiKit.Small, Palette.TextDim);
        about.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        about.CustomMinimumSize = new Vector2(380, 0);
        parent.AddChild(about);
    }
}
