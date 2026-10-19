using Brawler.Logic;
using Godot;

namespace Brawler.Core;

/// <summary>The local player's name, color and look, kept in user://profile.cfg between runs. Only UI actions save it.</summary>
public static class Profile
{
    private const string Path = "user://profile.cfg";

    public static (string Name, int Color, Appearance Look) Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return ("Player", 0, Appearance.Default());
        string name = (string)cfg.GetValue("player", "name", "Player");
        int color = (int)cfg.GetValue("player", "color", 0);
        var look = Appearance.Decode((string)cfg.GetValue("player", "look", ""));
        return (string.IsNullOrWhiteSpace(name) ? "Player" : name, Mathf.Clamp(color, 0, Palette.Players.Length - 1), look);
    }

    public static void Save(string name, int color, Appearance look)
    {
        var cfg = new ConfigFile();
        cfg.SetValue("player", "name", name);
        cfg.SetValue("player", "color", color);
        cfg.SetValue("player", "look", look.Encode());
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save profile: {err}");
    }
}
