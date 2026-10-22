using System.Threading.Tasks;
using Brawler.Core;
using Brawler.Logic;
using Brawler.UI;
using Godot;

namespace Brawler;

// --demo=NAME: a scripted walk through one feature, paced for a viewer, to record with --write-movie for a Short.
// Each demo logs "DEMO <frame> <label>" at the moments worth cutting to, so the edit can find them in the recording
// (frame / 60 is the time in the video). Demos keep their maps and settings apart from the player's.
public partial class Main
{
    private async void Demo(string name)
    {
        _autoMode = "demo";
        _game.Stats.Enabled = false;
        MapLibrary.Dir = "user://demo/maps";
        MapLibrary.SettingsPath = "user://demo/settings.cfg";
        DirAccess.MakeDirRecursiveAbsolute(MapLibrary.Dir);
        foreach (var f in DirAccess.GetFilesAt(MapLibrary.Dir)) DirAccess.RemoveAbsolute($"{MapLibrary.Dir}/{f}");
        MapLibrary.Draft = "";
        MapLibrary.LobbyMaps = new();
        await DemoWait(0.5);
        switch (name)
        {
            case "thumbs": await DemoThumbs(); break;
            default: Finish(1, $"--demo: no demo called {name}"); return;
        }
        Finish(0, $"demo {name} done");
    }

    private async Task DemoWait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private static void DemoMark(string label) => Log.Info($"DEMO {Engine.GetFramesDrawn()} {label}");

    /// <summary>Hides labels showing a folder on this computer (its user name) before they get recorded.</summary>
    private static void HidePaths(Node root)
    {
        while (FindChild<Label>(root, l => l.Visible && l.Text.Contains(":/Users/")) is { } l) l.Visible = false;
    }

    private static void Press(Node root, string text) => FindButton(root, text)?.EmitSignal(BaseButton.SignalName.Pressed);

    // Map thumbnails: a few saved arenas in My maps, then the lobby's Arenas panel picking some for a match.
    private async Task DemoThumbs()
    {
        var maps = new (string Name, MapStyle Style, ThemeId Theme, int Seed)[]
        {
            ("Sky Temple", MapStyle.Temple, ThemeId.CandyClouds, 11), ("Lava Lamp", MapStyle.Cave, ThemeId.MagmaCore, 23),
            ("Neon Rooftops", MapStyle.Towers, ThemeId.NeonNight, 5), ("Coral Steps", MapStyle.Tiers, ThemeId.DeepReef, 41),
            ("Frozen Tunnel", MapStyle.Tunnel, ThemeId.FrostBite, 8), ("Mirror Grove", MapStyle.Mirror, ThemeId.ToxicGrove, 17),
        };
        foreach (var m in maps)
        {
            var d = MapDesign.Generate(m.Style, m.Theme, m.Seed);
            d.Name = m.Name;
            MapLibrary.Save(d);
        }
        Press(_menu, "Map Editor");
        await DemoWait(1.5);
        DemoMark("library");
        Press(_editor, "My maps");
        HidePaths(_editor);
        await DemoWait(4.5);
        Press(_editor, "DONE");
        await DemoWait(0.5);
        Press(_editor, "Back");
        await DemoWait(0.4);
        Press(_menu, "PLAY VS BOTS");
        await DemoWait(1.0);
        DemoMark("arenas");
        Press(_lobby, "Random");
        await DemoWait(1.5);
        foreach (var pick in new[] { "Lava Lamp", "Sky Temple", "Neon Rooftops" })
        {
            if (FindChild<CheckBox>(_lobby, c => c.Text == pick && c.IsVisibleInTree()) is { } tick) tick.ButtonPressed = true;
            await DemoWait(0.9);
        }
        await DemoWait(2.0);
        Press(_lobby, "DONE");
        await DemoWait(0.8);
        DemoMark("match");
        _game.AutopilotLocal = true;
        _game.HostStartMatch();
        await DemoWait(9.0);
        DemoMark("end");
    }
}
