using Brawler.Logic;
using Godot;

namespace Brawler.Core;

/// <summary>The local player's name, color, look and last join address, kept in user://profile.cfg between runs. Only UI actions save it.</summary>
public static class Profile
{
    /// <summary>Where the profile lives. Demos point it elsewhere so they never touch the player's own.</summary>
    public static string Path = "user://profile.cfg";

    public static (string Name, int Color, Appearance Look) Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return ("Player", 0, Appearance.Default());
        string name = (string)cfg.GetValue("player", "name", "Player");
        int color = (int)cfg.GetValue("player", "color", 0);
        var look = Appearance.Decode((string)cfg.GetValue("player", "look", ""));
        return (string.IsNullOrWhiteSpace(name) ? "Player" : name, Mathf.Clamp(color, 0, Palette.Players.Length - 1), look);
    }

    public static void Save(string name, int color, Appearance look) => Edit(cfg =>
    {
        cfg.SetValue("player", "name", name);
        cfg.SetValue("player", "color", color);
        cfg.SetValue("player", "look", look.Encode());
    });

    /// <summary>The main player's milestones so far (see <see cref="Career"/>).</summary>
    public static Career LoadCareer()
    {
        var cfg = new ConfigFile();
        return Career.Decode(cfg.Load(Path) == Error.Ok ? (string)cfg.GetValue("player", "career", "") : "");
    }

    public static void SaveCareer(Career c) => Edit(cfg => cfg.SetValue("player", "career", c.Encode()));

    /// <summary>The address last typed in the join box, so friends don't retype it every session.</summary>
    public static string LastAddress()
    {
        var cfg = new ConfigFile();
        return cfg.Load(Path) == Error.Ok ? (string)cfg.GetValue("net", "last_address", "") : "";
    }

    public static void SaveLastAddress(string address) => Edit(cfg => cfg.SetValue("net", "last_address", address));

    private static void Edit(System.Action<ConfigFile> change)
    {
        var cfg = new ConfigFile();
        cfg.Load(Path); // a missing file just starts empty
        change(cfg);
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save profile: {err}");
    }
}
