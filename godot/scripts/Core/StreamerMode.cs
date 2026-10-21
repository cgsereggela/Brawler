using System;
using Godot;

namespace Brawler.Core;

/// <summary>
/// Streamer mode: no IP address is ever drawn on screen. The lobby hides them and its Copy button copies an invite code
/// instead, the join box masks what's typed, and connection messages leave the address out. Kept in user://settings.cfg.
/// </summary>
public static class StreamerMode
{
    private const string Path = "user://settings.cfg";
    private static bool? _on;

    /// <summary>Raised when the option is toggled, so open screens can hide or show addresses straight away.</summary>
    public static event Action Changed;

    public static bool On
    {
        get
        {
            if (_on == null)
            {
                var cfg = new ConfigFile();
                _on = cfg.Load(Path) == Error.Ok && (bool)cfg.GetValue("privacy", "streamer_mode", false);
            }
            return _on.Value;
        }
        set
        {
            if (value == On) return;
            _on = value;
            var cfg = new ConfigFile();
            cfg.Load(Path); // keep any other sections
            cfg.SetValue("privacy", "streamer_mode", value);
            var err = cfg.Save(Path);
            if (err != Error.Ok) Log.Info($"could not save settings: {err}");
            Changed?.Invoke();
        }
    }

    /// <summary>--streamer: on for this run only, without touching the saved setting.</summary>
    public static void SetForRun(bool on)
    {
        _on = on;
        Changed?.Invoke();
    }
}
