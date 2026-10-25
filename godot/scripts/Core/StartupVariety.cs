using System.Linq;
using Godot;

namespace Brawler.Core;

/// <summary>
/// What the last launch opened with (the menu scenery's theme, the first menu song), kept in the [startup] section of
/// user://settings.cfg, so the next launch can open with something else. Test and capture runs (anything with
/// command-line arguments) read it but leave it alone.
/// </summary>
public static class StartupVariety
{
    private const string Path = "user://settings.cfg", Section = "startup";

    public static string Last(string key)
    {
        var cfg = new ConfigFile();
        return cfg.Load(Path) == Error.Ok ? (string)cfg.GetValue(Section, key, "") : "";
    }

    public static void Remember(string key, string value)
    {
        if (OS.GetCmdlineUserArgs().Any()) return;
        var cfg = new ConfigFile();
        cfg.Load(Path); // keep any other sections
        cfg.SetValue(Section, key, value);
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }

    /// <summary>A random index in [0, count), never the one remembered under <paramref name="key"/> (when there's a choice); remembers it.</summary>
    public static int Pick(string key, int count)
    {
        string last = Last(key);
        int i = (int)(GD.Randi() % (uint)count);
        if (count > 1 && i.ToString() == last) i = (i + 1 + (int)(GD.Randi() % (uint)(count - 1))) % count;
        Remember(key, i.ToString());
        return i;
    }
}
