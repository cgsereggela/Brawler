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
/// Each match, and each return to the menus, moves on to the next track of that kind; switching crossfades. The
/// first menu song is never the one the last launch opened with, and M skips ahead.
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
        // The menus are what you hear first: open on a different song from the one the last launch opened with.
        if (_order.TryGetValue("lobby", out var lobby) && lobby.Count > 1)
        {
            if (lobby[0] == StartupVariety.Last("menu_song")) { lobby.Add(lobby[0]); lobby.RemoveAt(0); }
            StartupVariety.Remember("menu_song", lobby[0]);
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

    /// <summary>Crossfades to the next track of the kind playing now (the next-song hotkey).</summary>
    public void Skip()
    {
        if (_playing != null) _skip = true;
    }

    private bool _skip;

    /// <summary>The track playing now (null before the first one starts).</summary>
    public string Track { get; private set; }

    /// <summary>
    /// M skips to the next song, anywhere. Unhandled only, so typing an M into a text box doesn't, nor does the Controls
    /// tab capturing a key; and not if the player has bound M to something themselves.
    /// </summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || k.PhysicalKeycode != Key.M || k.GetModifiersMask() != 0) return;
        foreach (var action in InputMap.GetActions())
            if (!action.ToString().StartsWith("ui_") && InputMap.EventIsAction(e, action, true)) return;
        Skip();
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        while (_rendered.TryDequeue(out var r)) _tracks[r.Name] = Stream(r.Data);
        if (_job is { IsCompleted: true })
        {
            if (_job.IsFaulted) Log.Info($"music synthesis failed: {_job.Exception?.InnerException?.Message}");
            _job = null;
        }
        if (_skip) (_playing, _skip) = (null, false); // ask for the same kind again: the next one in turn comes up
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
        Track = pick;
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
