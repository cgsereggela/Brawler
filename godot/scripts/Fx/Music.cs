using System.Collections.Generic;
using System.Threading.Tasks;
using Brawler.Core;
using Brawler.Logic.Synth;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// Background music on the Music bus: the lobby track in menus, the battle track during a match. Both loops are
/// synthesized by <see cref="MusicGen"/> on a worker thread at startup, and switching tracks crossfades.
/// </summary>
public partial class Music : Node
{
    private const float FadeSeconds = 1.2f;
    private readonly Dictionary<string, AudioStreamWav> _tracks = new();
    private Task<List<(string Name, float[] Data)>> _job;
    private AudioStreamPlayer _current;
    private string _want, _playing;

    public override void _Ready()
    {
        if (DisplayServer.GetName() == "headless") return; // simulations: skip a few seconds of synthesis per run
        _job = Task.Run(() =>
        {
            var list = new List<(string, float[])>();
            // Lobby first: it's what the player hears first.
            foreach (var song in new[] { MusicGen.Lobby(), MusicGen.Battle() }) list.Add((song.Name, MusicGen.Render(song)));
            return list;
        });
    }

    /// <summary>Asks for a track ("lobby" or "battle"); it starts, or crossfades in, as soon as it is ready.</summary>
    public void Play(string track) => _want = track;

    public override void _Process(double delta)
    {
        if (_job is { IsCompleted: true })
        {
            if (_job.IsCompletedSuccessfully)
                foreach (var (name, data) in _job.Result) _tracks[name] = Stream(data);
            else Log.Info($"music synthesis failed: {_job.Exception?.InnerException?.Message}");
            _job = null;
        }
        if (_want == _playing || _want == null || !_tracks.TryGetValue(_want, out var stream)) return;

        if (_current != null)
        {
            var old = _current;
            var tw = CreateTween();
            tw.TweenProperty(old, "volume_db", -50f, FadeSeconds).SetTrans(Tween.TransitionType.Sine);
            tw.TweenCallback(Callable.From(old.QueueFree));
        }
        _current = new AudioStreamPlayer { Stream = stream, Bus = AudioSettings.MusicBus, VolumeDb = _playing == null ? -12f : -30f };
        AddChild(_current);
        _current.Play();
        CreateTween().TweenProperty(_current, "volume_db", 0f, FadeSeconds * 0.8f).SetTrans(Tween.TransitionType.Sine);
        _playing = _want;
    }

    private static AudioStreamWav Stream(float[] stereo)
    {
        int frames = stereo.Length / 2;
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Dsp.Rate, Stereo = true, Data = Dsp.ToPcm16(stereo),
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = frames,
        };
    }
}
