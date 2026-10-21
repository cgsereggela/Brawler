using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Brawler.Core;
using Brawler.Logic.Synth;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// Background music on the Music bus: a lobby track in menus, a battle track during a match. Every song is synthesized
/// by <see cref="MusicGen"/> on a worker thread at startup, one at a time so the first can play while the rest render.
/// Each match, and each return to the menus, moves on to the next track of that kind; switching crossfades.
/// </summary>
public partial class Music : Node
{
    private const float FadeSeconds = 1.2f;
    private readonly Dictionary<string, AudioStreamWav> _tracks = new();
    private readonly ConcurrentQueue<(string Name, float[] Data)> _rendered = new();
    // Play order per kind ("lobby", "battle"): shuffled at startup, then taken in turn.
    private readonly Dictionary<string, List<string>> _order = new();
    private readonly Dictionary<string, int> _next = new();
    private Task _job;
    private AudioStreamPlayer _current;
    private string _want, _playing;

    public override void _Ready()
    {
        if (DisplayServer.GetName() == "headless") return; // simulations: skip a few seconds of synthesis per run
        var songs = MusicGen.All();
        foreach (var kind in songs.Select(s => s.Kind).Distinct())
        {
            _order[kind] = songs.Where(s => s.Kind == kind).Select(s => s.Name).OrderBy(_ => GD.Randi()).ToList();
            _next[kind] = 0;
        }
        // Render each kind's first track first (lobby before battle: it's what the player hears first), then the rest.
        var firsts = _order.Values.Select(o => o[0]).ToList();
        var queue = songs.OrderBy(s => firsts.Contains(s.Name) ? 0 : 1).ThenBy(s => s.Kind == "lobby" ? 0 : 1).ToList();
        _job = Task.Run(() =>
        {
            foreach (var song in queue) _rendered.Enqueue((song.Name, MusicGen.Render(song)));
        });
    }

    /// <summary>Asks for a kind of track ("lobby" or "battle"). Asking again for the kind already playing changes nothing.</summary>
    public void Play(string kind) => _want = kind;

    public override void _Process(double delta)
    {
        while (_rendered.TryDequeue(out var r)) _tracks[r.Name] = Stream(r.Data);
        if (_job is { IsCompleted: true })
        {
            if (_job.IsFaulted) Log.Info($"music synthesis failed: {_job.Exception?.InnerException?.Message}");
            _job = null;
        }
        if (_want == _playing || _want == null || !_order.TryGetValue(_want, out var order)) return;

        // The next track of this kind in turn, skipping any that haven't finished rendering yet.
        string pick = null;
        for (int k = 0; k < order.Count && pick == null; k++)
        {
            int i = (_next[_want] + k) % order.Count;
            if (_tracks.ContainsKey(order[i])) { pick = order[i]; _next[_want] = i + 1; }
        }
        if (pick == null) return;

        if (_current != null)
        {
            var old = _current;
            var tw = CreateTween();
            tw.TweenProperty(old, "volume_db", -50f, FadeSeconds).SetTrans(Tween.TransitionType.Sine);
            tw.TweenCallback(Callable.From(old.QueueFree));
        }
        _current = new AudioStreamPlayer { Stream = _tracks[pick], Bus = AudioSettings.MusicBus, VolumeDb = _playing == null ? -12f : -30f };
        AddChild(_current);
        _current.Play();
        CreateTween().TweenProperty(_current, "volume_db", 0f, FadeSeconds * 0.8f).SetTrans(Tween.TransitionType.Sine);
        _playing = _want;
        Log.Info($"music: {pick}");
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
