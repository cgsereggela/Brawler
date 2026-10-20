using Godot;

namespace Brawler.Core;

/// <summary>
/// Bloom amount (0..1, default 0.5), kept in user://settings.cfg under video/bloom. 1 is the full look the art was
/// tuned for; 0 turns the glow pass off. "--bloom=N" (0..100) on the command line overrides it for one run.
/// </summary>
public static class BloomSettings
{
    private const string Path = "user://settings.cfg";
    private const float FullIntensity = 0.9f, FullStrength = 1.1f;

    public static float Amount = 0.5f;
    private static Godot.Environment _env;

    /// <summary>Called once at startup with the WorldEnvironment's environment that holds the glow.</summary>
    public static void Init(Godot.Environment env)
    {
        _env = env;
        var cfg = new ConfigFile();
        if (cfg.Load(Path) == Error.Ok)
            Amount = Mathf.Clamp((float)cfg.GetValue("video", "bloom", Amount), 0, 1);
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--bloom=") && float.TryParse(a["--bloom=".Length..], System.Globalization.CultureInfo.InvariantCulture, out float pct))
                Amount = Mathf.Clamp(pct / 100f, 0, 1);
        Apply();
    }

    /// <summary>
    /// Scales intensity and spread together, linearly: at 50% hit flashes keep a hot core instead of blowing out
    /// to a white blob (scaling intensity alone barely shrinks it).
    /// </summary>
    public static void Apply()
    {
        if (_env == null) return;
        _env.GlowEnabled = Amount > 0.001f;
        _env.GlowIntensity = FullIntensity * Amount;
        _env.GlowStrength = FullStrength * Amount;
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path); // keep any other sections
        cfg.SetValue("video", "bloom", Amount);
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }
}
