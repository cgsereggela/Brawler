using Godot;

namespace Brawler.Core;

/// <summary>
/// Master, music and sound-effect volumes (0..1), kept in user://settings.cfg. Sets up the Music and Sfx buses:
/// a little room reverb on effects and a limiter on the master so stacked explosions never clip.
/// </summary>
public static class AudioSettings
{
    private const string Path = "user://settings.cfg";
    public const string MusicBus = "Music", SfxBus = "Sfx";

    public static float Master = 0.8f, Music = 0.6f, Effects = 0.8f;

    public static void Init()
    {
        int sfx = EnsureBus(SfxBus);
        if (AudioServer.GetBusEffectCount(sfx) == 0)
            AudioServer.AddBusEffect(sfx, new AudioEffectReverb { RoomSize = 0.35f, Damping = 0.6f, Spread = 0.7f, Wet = 0.09f, Dry = 1f, PredelayMsec = 12 });
        EnsureBus(MusicBus);
        if (AudioServer.GetBusEffectCount(0) == 0)
            AudioServer.AddBusEffect(0, new AudioEffectHardLimiter { CeilingDb = -0.5f });

        var cfg = new ConfigFile();
        if (cfg.Load(Path) == Error.Ok)
        {
            Master = Mathf.Clamp((float)cfg.GetValue("audio", "master", Master), 0, 1);
            Music = Mathf.Clamp((float)cfg.GetValue("audio", "music", Music), 0, 1);
            Effects = Mathf.Clamp((float)cfg.GetValue("audio", "sfx", Effects), 0, 1);
        }
        Apply();
    }

    public static void Apply()
    {
        SetBus(0, Master);
        SetBus(AudioServer.GetBusIndex(MusicBus), Music);
        SetBus(AudioServer.GetBusIndex(SfxBus), Effects);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path); // keep any other sections
        cfg.SetValue("audio", "master", Master);
        cfg.SetValue("audio", "music", Music);
        cfg.SetValue("audio", "sfx", Effects);
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }

    private static int EnsureBus(string name)
    {
        int i = AudioServer.GetBusIndex(name);
        if (i >= 0) return i;
        AudioServer.AddBus();
        i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, name);
        AudioServer.SetBusSend(i, "Master");
        return i;
    }

    /// <summary>Slider position to gain on a squared curve, so the slider feels even to the ear; 0 mutes.</summary>
    private static void SetBus(int bus, float v)
    {
        if (bus < 0) return;
        AudioServer.SetBusMute(bus, v <= 0.001f);
        AudioServer.SetBusVolumeDb(bus, Mathf.LinearToDb(Mathf.Max(v * v, 0.00001f)));
    }
}
