using System.Collections.Generic;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Replays the final kill of a battle in slow motion. Every peer records what it shows each tick (avatars, bullets,
/// movers and props) and every effect and sound started, for the last few seconds; when the host calls for the kill
/// cam, each peer plays its own recording back around that kill (see <see cref="KillCamTiming"/>), then puts
/// everything back as it was. Nothing is simulated during a replay, so it can't change the battle.
/// </summary>
public sealed class KillCam
{
    /// <summary>Seconds of history kept: the build-up plus the time the battle runs on after the kill before the replay.</summary>
    private const float Keep = KillCamTiming.Pre + 2f;
    private const int Capacity = (int)(Keep * 60) + 8;

    private sealed class Tick
    {
        public float T, Clock;
        public readonly Avatar.Frame[] Avatars = new Avatar.Frame[LobbyState.MaxPlayers];
        public readonly bool[] Has = new bool[LobbyState.MaxPlayers];
        public readonly List<Bullet> Bullets = new(); // reused copies; the first BulletCount are this tick's
        public int BulletCount;
        public readonly List<(Vector2 Pos, bool Broken, float Wobble)> Props = new();
        public readonly List<Vector2> Drifts = new();
        public readonly List<int> Hangers = new();
    }

    private readonly GameManager _g;
    private readonly Tick[] _ticks = new Tick[Capacity];
    private int _head, _count; // ring buffer: oldest at _head
    private readonly List<FxCall> _fx = new();
    private readonly List<(float T, string Name, float Volume)> _sfx = new();
    private readonly List<(float T, float Amount)> _shakes = new();
    private readonly float[] _diedAt = new float[LobbyState.MaxPlayers];
    private bool _recording;

    // Playback.
    public bool Playing { get; private set; }
    public int Victim { get; private set; } = -1;
    public int Killer { get; private set; } = -1;
    private float _t, _from, _to, _kill;
    private int _fxNext, _sfxNext, _shakeNext;
    private readonly List<Bullet> _live = new(), _shown = new();

    public KillCam(GameManager game)
    {
        _g = game;
        for (int i = 0; i < Capacity; i++) _ticks[i] = new Tick();
    }

    /// <summary>Seconds from the kill at the current replay moment (negative = before it).</summary>
    public float FromKill => _t - _kill;
    /// <summary>0..1 through the replay.</summary>
    public float Progress => _to > _from ? Mathf.Clamp((_t - _from) / (_to - _from), 0, 1) : 1;

    // ---------- recording ----------

    /// <summary>A new battle: forget the last one and record this one.</summary>
    public void Begin()
    {
        Stop();
        _head = _count = 0;
        _fx.Clear();
        _sfx.Clear();
        _shakes.Clear();
        System.Array.Fill(_diedAt, float.NaN);
        _recording = true;
        Attach();
    }

    /// <summary>Stops recording (the battle is over, or the arena is gone).</summary>
    public void End()
    {
        Stop();
        _recording = false;
        Detach();
    }

    private void Attach()
    {
        _g.Fx.Record = _fx;
        if (_g.Sfx != null) _g.Sfx.Record = _sfx;
    }

    private void Detach()
    {
        _g.Fx.Record = null;
        if (_g.Sfx != null) _g.Sfx.Record = null;
    }

    /// <summary>Effects and sounds from here on are stamped with this battle time.</summary>
    public void Stamp(float now)
    {
        _g.Fx.RecordTime = now;
        if (_g.Sfx != null) _g.Sfx.RecordTime = now;
    }

    public void NoteShake(float now, float amount)
    {
        if (_recording && !Playing) _shakes.Add((now, amount));
    }

    public void NoteDeath(int slot, float now)
    {
        if (slot >= 0 && slot < _diedAt.Length) _diedAt[slot] = now;
    }

    /// <summary>Records how everything looks at the end of this tick.</summary>
    public void Capture(float now)
    {
        if (!_recording || Playing) return;
        Tick k;
        if (_count < Capacity) k = _ticks[(_head + _count++) % Capacity];
        else
        {
            k = _ticks[_head];
            _head = (_head + 1) % Capacity;
        }
        k.T = now;
        k.Clock = _g.Arena.Clock;
        for (int s = 0; s < k.Has.Length; s++)
        {
            var a = _g.Slots[s]?.Avatar;
            k.Has[s] = a != null && GodotObject.IsInstanceValid(a) && !_g.Slots[s].Left;
            if (k.Has[s]) k.Avatars[s] = a.Capture();
        }
        var bullets = _g.Bullets.Bullets;
        k.BulletCount = 0;
        foreach (var b in bullets)
        {
            if (b.Dead) continue;
            if (k.BulletCount == k.Bullets.Count) k.Bullets.Add(new Bullet());
            b.CopyLook(k.Bullets[k.BulletCount++]);
        }
        k.Props.Clear();
        foreach (var p in _g.Arena.Props) k.Props.Add((p.GlobalPosition, p.Broken, p.Wobble));
        _g.Arena.CaptureDrifts(k.Drifts);
        _g.Arena.CaptureHangers(k.Hangers);

        // Drop effects older than anything still kept (a few at a time, so it never stalls a frame).
        float cutoff = TickAt(0).T;
        Prune(_fx, cutoff, c => c.T);
        Prune(_sfx, cutoff, c => c.T);
        Prune(_shakes, cutoff, c => c.T);
    }

    private static void Prune<T>(List<T> list, float cutoff, System.Func<T, float> time)
    {
        int n = 0;
        while (n < list.Count && time(list[n]) < cutoff) n++;
        if (n > 0) list.RemoveRange(0, n);
    }

    private Tick TickAt(int i) => _ticks[(_head + i) % Capacity];

    // ---------- playback ----------

    /// <summary>Starts replaying <paramref name="victim"/>'s death. False if there's too little of it recorded.</summary>
    public bool Start(int victim, int killer)
    {
        if (Playing || _count < 2 || victim < 0 || victim >= _diedAt.Length || float.IsNaN(_diedAt[victim])) return false;
        _kill = _diedAt[victim];
        _from = Mathf.Max(TickAt(0).T, _kill - KillCamTiming.Pre);
        _to = Mathf.Min(TickAt(_count - 1).T, _kill + KillCamTiming.Post);
        if (_to - _from < 0.5f || _kill < _from) return false;
        Victim = victim;
        Killer = killer;
        Playing = true;
        _t = _from;
        _fxNext = First(_fx, _from, c => c.T);
        _sfxNext = First(_sfx, _from, c => c.T);
        _shakeNext = First(_shakes, _from, c => c.T);
        Detach(); // what the replay sets off isn't part of the battle
        _live.Clear();
        _live.AddRange(_g.Bullets.Bullets);
        _g.Fx.Clear();
        Show(_t);
        Engine.TimeScale = KillCamTiming.Speed(FromKill);
        return true;
    }

    private static int First<T>(List<T> list, float t, System.Func<T, float> time)
    {
        int i = 0;
        while (i < list.Count && time(list[i]) <= t) i++;
        return i;
    }

    /// <summary>One frame of the replay; <paramref name="delta"/> is in battle seconds (already slowed by the time scale).</summary>
    public void Advance(float delta)
    {
        if (!Playing) return;
        _t = Mathf.Min(_to, _t + delta);
        Show(_t);
        float speed = KillCamTiming.Speed(FromKill);
        while (_fxNext < _fx.Count && _fx[_fxNext].T <= _t) _g.Fx.Replay(_fx[_fxNext++]);
        while (_sfxNext < _sfx.Count && _sfx[_sfxNext].T <= _t)
        {
            var (_, name, volume) = _sfx[_sfxNext++];
            _g.Sfx?.Play(name, volume, Mathf.Lerp(0.72f, 1f, speed));
        }
        while (_shakeNext < _shakes.Count && _shakes[_shakeNext].T <= _t) _g.Shake(_shakes[_shakeNext++].Amount);
        if (_t >= _to) Stop();
        else Engine.TimeScale = speed;
    }

    /// <summary>Ends the replay (if one is on) and puts everything back as it is now.</summary>
    public void Stop()
    {
        if (!Playing) return;
        Playing = false;
        Engine.TimeScale = 1;
        Show(TickAt(_count - 1).T);
        _g.Bullets.Bullets.Clear();
        _g.Bullets.Bullets.AddRange(_live);
        _live.Clear();
        if (_recording) Attach();
    }

    /// <summary>Where the camera should look: the victim, pulled toward the killer if they're both in view.</summary>
    public (Vector2 Focus, Vector2 Span) Framing()
    {
        var victim = Slot(Victim);
        var killer = Killer != Victim ? Slot(Killer) : null;
        if (victim == null) return (new Vector2(MapLayout.Width, MapLayout.Height) / 2, new Vector2(MapLayout.Width, MapLayout.Height));
        var v = victim.GlobalPosition;
        if (killer == null || !killer.Alive || killer.GlobalPosition.DistanceTo(v) > 520) return (v, Vector2.Zero);
        var k = killer.GlobalPosition;
        return (v.Lerp(k, 0.4f), (k - v).Abs());
    }

    private Avatar Slot(int s) => s >= 0 && s < _g.Slots.Length && _g.Slots[s]?.Avatar is { } a && GodotObject.IsInstanceValid(a) ? a : null;

    /// <summary>Shows the recording at battle time <paramref name="t"/>, blending between the ticks either side.</summary>
    private void Show(float t)
    {
        int lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (TickAt(mid).T <= t) lo = mid;
            else hi = mid - 1;
        }
        var a = TickAt(lo);
        var b = lo + 1 < _count ? TickAt(lo + 1) : a;
        float k = b.T > a.T ? Mathf.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 0;

        for (int s = 0; s < a.Has.Length; s++)
        {
            if (!a.Has[s] || Slot(s) is not { } av) continue;
            av.Show(a.Avatars[s], b.Has[s] ? b.Avatars[s] : a.Avatars[s], k);
        }

        _g.Arena.ShowClock(Mathf.Lerp(a.Clock, b.Clock, k));
        var props = _g.Arena.Props;
        for (int i = 0; i < props.Count && i < a.Props.Count; i++)
        {
            var (pos, broken, wobble) = a.Props[i];
            var next = i < b.Props.Count ? b.Props[i].Pos : pos;
            props[i].GlobalPosition = pos.Lerp(next, k);
            props[i].Visible = !broken;
            props[i].Wobble = wobble;
        }
        _g.Arena.ShowDrifts(a.Drifts, b.Drifts, k);
        _g.Arena.ShowHangers(a.Hangers);

        // Bullets: this tick's copies, moved toward where the same bullet is on the next tick.
        while (_shown.Count < a.BulletCount) _shown.Add(new Bullet());
        var list = _g.Bullets.Bullets;
        list.Clear();
        for (int i = 0; i < a.BulletCount; i++)
        {
            var src = a.Bullets[i];
            var dst = _shown[i];
            src.CopyLook(dst);
            for (int j = 0; j < b.BulletCount; j++)
                if (b.Bullets[j].Id == src.Id)
                {
                    dst.Pos = src.Pos.Lerp(b.Bullets[j].Pos, k);
                    break;
                }
            list.Add(dst);
        }
    }
}
