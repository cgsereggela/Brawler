using Godot;

namespace Brawler.Core;

/// <summary>
/// Comfort options, kept in user://settings.cfg under [accessibility]. Shake scales camera shake and zoom punches
/// (1 = full, 0 = none). ReduceFlashes drops full-screen flashes, keeps hit bodies from flashing white, and dims the
/// glow bursts from hits, explosions and lightning.
/// </summary>
public static class Accessibility
{
    private const string Path = "user://settings.cfg";
    private static bool _loaded;
    private static float _shake = 1f;
    private static bool _reduceFlashes;

    public static float Shake
    {
        get { Load(); return _shake; }
        set { Load(); _shake = Mathf.Clamp(value, 0, 1); }
    }

    public static bool ReduceFlashes
    {
        get { Load(); return _reduceFlashes; }
        set { Load(); _reduceFlashes = value; Save(); }
    }

    /// <summary>How bright glow bursts are drawn: full, or dimmed when flashes are reduced.</summary>
    public static float FlashScale => ReduceFlashes ? 0.35f : 1f;

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;
        _shake = Mathf.Clamp((float)cfg.GetValue("accessibility", "shake", 1f), 0, 1);
        _reduceFlashes = (bool)cfg.GetValue("accessibility", "reduce_flashes", false);
    }

    public static void Save()
    {
        Load();
        var cfg = new ConfigFile();
        cfg.Load(Path); // keep any other sections
        cfg.SetValue("accessibility", "shake", _shake);
        cfg.SetValue("accessibility", "reduce_flashes", _reduceFlashes);
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }
}
