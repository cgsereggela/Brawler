using System;
using Godot;

namespace Brawler.Core;

/// <summary>
/// Comfort options, kept in user://settings.cfg under [accessibility]. Shake scales camera shake and zoom punches
/// (1 = full, 0 = none). ReduceFlashes drops full-screen flashes, keeps hit bodies from flashing white, and dims the
/// glow bursts from hits, explosions and lightning. ColorblindColors swaps the player and team colors for a set that
/// stays apart under protanopia, deuteranopia and tritanopia (see Palette). UiScale makes every menu and the HUD
/// bigger. AimAssist bends a controller's aim toward the enemy nearest the line it points along (0 = off).
/// </summary>
public static class Accessibility
{
    private const string Path = "user://settings.cfg";
    /// <summary>The UI scales on offer: the menus are laid out to fit the window at each of them.</summary>
    public static readonly float[] UiScales = { 1f, 1.15f, 1.3f };

    private static bool _loaded;
    private static float _shake = 1f;
    private static bool _reduceFlashes;
    private static bool _colorblind;
    private static float _uiScale = 1f;
    private static float _aimAssist = 0.4f;

    /// <summary>Raised when the colors or the UI scale change, so what's on screen can follow.</summary>
    public static event Action Changed;

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

    public static bool ColorblindColors
    {
        get { Load(); return _colorblind; }
        set { Load(); _colorblind = value; Save(); Changed?.Invoke(); }
    }

    public static float UiScale
    {
        get { Load(); return _uiScale; }
        set { Load(); _uiScale = NearestScale(value); Save(); Changed?.Invoke(); }
    }

    /// <summary>Shows the UI at <paramref name="s"/> without saving it (checks lay the menus out at every scale).</summary>
    public static void PreviewUiScale(float s)
    {
        Load();
        _uiScale = NearestScale(s);
        Changed?.Invoke();
    }

    /// <summary>Shows the colorblind-safe colors (or not) without saving it, for demos.</summary>
    public static void PreviewColorblind(bool on)
    {
        Load();
        _colorblind = on;
        Changed?.Invoke();
    }

    /// <summary>How hard a controller's aim is pulled toward an enemy, 0 to 1. Saved by the caller (see Shake).</summary>
    public static float AimAssist
    {
        get { Load(); return _aimAssist; }
        set { Load(); _aimAssist = Mathf.Clamp(value, 0, 1); }
    }

    /// <summary>How bright glow bursts are drawn: full, or dimmed when flashes are reduced.</summary>
    public static float FlashScale => ReduceFlashes ? 0.35f : 1f;

    private static float NearestScale(float s)
    {
        float best = UiScales[0];
        foreach (float u in UiScales) if (Mathf.Abs(u - s) < Mathf.Abs(best - s)) best = u;
        return best;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;
        _shake = Mathf.Clamp((float)cfg.GetValue("accessibility", "shake", 1f), 0, 1);
        _reduceFlashes = (bool)cfg.GetValue("accessibility", "reduce_flashes", false);
        _colorblind = (bool)cfg.GetValue("accessibility", "colorblind_colors", false);
        _uiScale = NearestScale((float)cfg.GetValue("accessibility", "ui_scale", 1f));
        _aimAssist = Mathf.Clamp((float)cfg.GetValue("accessibility", "aim_assist", 0.4f), 0, 1);
    }

    public static void Save()
    {
        Load();
        var cfg = new ConfigFile();
        cfg.Load(Path); // keep any other sections
        cfg.SetValue("accessibility", "shake", _shake);
        cfg.SetValue("accessibility", "reduce_flashes", _reduceFlashes);
        cfg.SetValue("accessibility", "colorblind_colors", _colorblind);
        cfg.SetValue("accessibility", "ui_scale", _uiScale);
        cfg.SetValue("accessibility", "aim_assist", _aimAssist);
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }
}
