using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Brawler.Core;
using Brawler.Logic.Synth;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// Plays the synthesized sound effects from <see cref="SfxBank"/> through a small voice pool on the Sfx bus.
/// Sounds render on a worker thread at startup; each has several takes, and repeats avoid the last take used.
/// </summary>
public partial class Sfx : Node
{
    private readonly Dictionary<string, AudioStreamWav[]> _sounds = new();
    private readonly Dictionary<string, int> _lastTake = new();
    private readonly List<AudioStreamPlayer> _voices = new();
    private readonly Dictionary<string, ulong> _lastPlayed = new();
    private readonly Random _rng = new();
    private Task<Dictionary<string, List<float[]>>> _job;
    private int _next;

    public override void _Ready()
    {
        for (int i = 0; i < 16; i++)
        {
            var p = new AudioStreamPlayer { Bus = AudioSettings.SfxBus };
            AddChild(p);
            _voices.Add(p);
        }
        if (DisplayServer.GetName() == "headless") return; // simulations: nobody is listening
        _job = Task.Run(() =>
        {
            var all = new Dictionary<string, List<float[]>>();
            foreach (var name in SfxBank.Names) all[name] = SfxBank.Render(name);
            return all;
        });
    }

    public override void _Process(double delta)
    {
        if (_job is not { IsCompleted: true }) return;
        if (_job.IsCompletedSuccessfully)
            foreach (var (name, takes) in _job.Result)
                _sounds[name] = takes.ConvertAll(Stream).ToArray();
        else Log.Info($"sound synthesis failed: {_job.Exception?.InnerException?.Message}");
        _job = null;
    }

    private static AudioStreamWav Stream(float[] mono) => new()
    {
        Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Dsp.Rate, Stereo = false, Data = Dsp.ToPcm16(mono),
    };

    /// <summary>Plays a sound with slight pitch variation. Rapid repeats of the same sound are throttled.</summary>
    public void Play(string name, float volume = 1f)
    {
        if (!_sounds.TryGetValue(name, out var takes)) return;
        ulong now = Time.GetTicksMsec();
        if (_lastPlayed.TryGetValue(name, out var last) && now - last < 30) return;
        _lastPlayed[name] = now;

        int take = _rng.Next(takes.Length);
        if (takes.Length > 1 && _lastTake.TryGetValue(name, out int prev) && take == prev) take = (take + 1) % takes.Length;
        _lastTake[name] = take;

        var v = _voices[_next];
        _next = (_next + 1) % _voices.Count;
        v.Stream = takes[take];
        v.PitchScale = 0.96f + (float)_rng.NextDouble() * 0.08f;
        v.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.01f, volume));
        v.Play();
    }

    /// <summary>The gunshot that fits a loadout: shotguns boom, heavy hitters crack and echo, fast guns snap.</summary>
    public static string ShotFor(Logic.StatBlock s)
    {
        if (s.Int(Logic.Stat.Projectiles) >= 3) return "shoot_shotgun";
        if (s[Logic.Stat.Damage] >= 36 || s[Logic.Stat.BulletRadius] >= 4.5f) return "shoot_heavy";
        if (s[Logic.Stat.FireCooldown] <= 0.17f || s[Logic.Stat.Damage] <= 14) return "shoot_light";
        return "shoot";
    }
}
