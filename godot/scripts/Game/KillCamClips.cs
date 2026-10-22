using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Keeps the last kill cam as a short clip that can be saved as a GIF (Logic/Gif.cs). While a kill cam plays, every
/// third frame drawn is grabbed and shrunk to <see cref="W"/>×<see cref="H"/>; the replay is in slow motion, so the
/// GIF plays it back just as it looked. Saving encodes on a worker thread and writes to user://clips.
/// </summary>
public static class KillCamClips
{
    public const int W = 480, H = 270;
    /// <summary>Grab one frame in this many (20 fps at 60).</summary>
    private const int Every = 3;
    /// <summary>A clip shorter than this many frames isn't worth keeping (a kill cam skipped at once).</summary>
    private const int MinFrames = 8;
    /// <summary>At most this many frames (about 12 s): a kill cam never runs that long, but a stuck one can't eat memory.</summary>
    private const int MaxFrames = 240;

    private static GameManager _game;
    private static byte[][] _grabbing;
    private static int _frame, _grabbed, _pending;
    private static ulong _grabUsec;
    private static List<byte[]> _last;
    private static bool _saving;

    /// <summary>Raised on the main thread when a clip is kept, a save starts, or a save ends.</summary>
    public static event Action Changed;

    /// <summary>A kill cam has been kept and can be saved.</summary>
    public static bool HasClip => _last != null;
    /// <summary>A save is encoding right now.</summary>
    public static bool Saving => _saving;
    /// <summary>Where the last save went (absolute path), or null.</summary>
    public static string LastSaved { get; private set; }
    /// <summary>The clip was saved already (it isn't saved twice).</summary>
    public static bool Saved { get; private set; }
    /// <summary>Frames in the kept clip.</summary>
    public static int FrameCount => _last?.Count ?? 0;
    /// <summary>The kept clip's frames (W×H RGB bytes each), or null.</summary>
    public static IReadOnlyList<byte[]> Frames => _last;

    public static string Folder => ProjectSettings.GlobalizePath("user://clips");

    /// <summary>Starts keeping kill cams from this game. Off for headless runs (there is nothing to grab).</summary>
    public static void Attach(GameManager game)
    {
        if (DisplayServer.GetName() == "headless") return;
        _game = game;
        game.KillCamStarted += () =>
        {
            if (_pending > 0) return; // the last one is still coming back from the GPU
            _grabbing = new byte[MaxFrames][];
            _frame = _grabbed = 0;
            _grabUsec = 0;
            Hook(true);
        };
    }

    private static bool _hooked;

    private static void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) RenderingServer.FramePostDraw += Grab;
        else RenderingServer.FramePostDraw -= Grab;
    }

    // Each grab asks the GPU for a copy of the screen without waiting for it (a plain GetImage stalls the frame for
    // 15-30 ms and its half-float conversion takes another 12). The copy comes back a frame or two later and is
    // shrunk on a worker thread; once the replay is over and every copy is in, the clip is kept.
    private static void Grab()
    {
        if (_grabbing == null || !GodotObject.IsInstanceValid(_game)) { Hook(false); return; }
        if (!_game.KillCam.Playing || _grabbed >= MaxFrames)
        {
            if (Volatile.Read(ref _pending) > 0) return; // wait for the copies still on their way
            Hook(false);
            var frames = new List<byte[]>();
            for (int i = 0; i < _grabbed; i++) if (_grabbing[i] != null) frames.Add(_grabbing[i]);
            if (frames.Count >= MinFrames)
            {
                _last = frames;
                Saved = false;
                Log.Info($"kill cam clip kept: {frames.Count} frames, {_grabUsec / 1000.0 / Math.Max(1, _grabbed):0.00} ms a grab");
                Changed?.Invoke();
            }
            _grabbing = null;
            return;
        }
        if (_frame++ % Every != 0) return;
        ulong t0 = Time.GetTicksUsec();
        var rd = RenderingServer.GetRenderingDevice();
        var rid = RenderingServer.TextureGetRdTexture(_game.GetViewport().GetTexture().GetRid());
        var format = rd != null && rid.IsValid ? rd.TextureGetFormat(rid) : null;
        if (format == null || BytesPerPixel(format.Format) == 0) return; // compatibility renderer or an odd format: no clips
        int sw = (int)format.Width, sh = (int)format.Height, slot = _grabbed++;
        var df = format.Format;
        var into = _grabbing;
        Interlocked.Increment(ref _pending);
        rd.TextureGetDataAsync(rid, 0, Callable.From((byte[] data) =>
        {
            Task.Run(() =>
            {
                try { into[slot] = Shrink(data, sw, sh, df); }
                finally { Interlocked.Decrement(ref _pending); }
            });
        }));
        _grabUsec += Time.GetTicksUsec() - t0;
    }

    private static int BytesPerPixel(RenderingDevice.DataFormat f) => f switch
    {
        RenderingDevice.DataFormat.R16G16B16A16Sfloat => 8,
        RenderingDevice.DataFormat.R8G8B8A8Unorm or RenderingDevice.DataFormat.R8G8B8A8Srgb or RenderingDevice.DataFormat.B8G8R8A8Unorm => 4,
        _ => 0,
    };

    /// <summary>
    /// The screen copy shrunk to W×H RGB bytes: each output pixel averages a 2×2 grid of samples from its patch of the
    /// screen. Half-float screens (HDR 2D) already hold display values; the glow above 1 is clipped like the screen clips it.
    /// </summary>
    private static byte[] Shrink(byte[] src, int sw, int sh, RenderingDevice.DataFormat f)
    {
        int bpp = BytesPerPixel(f);
        if (src == null || src.Length < sw * sh * bpp) return null;
        var dst = new byte[W * H * 3];
        bool half = f == RenderingDevice.DataFormat.R16G16B16A16Sfloat, bgr = f == RenderingDevice.DataFormat.B8G8R8A8Unorm;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            float r = 0, g = 0, b = 0;
            for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int px = Math.Min(sw - 1, (int)((x + 0.25f + 0.5f * i) * sw / W)), py = Math.Min(sh - 1, (int)((y + 0.25f + 0.5f * j) * sh / H));
                int o = (py * sw + px) * bpp;
                if (half)
                {
                    r += (float)BitConverter.ToHalf(src, o);
                    g += (float)BitConverter.ToHalf(src, o + 2);
                    b += (float)BitConverter.ToHalf(src, o + 4);
                }
                else
                {
                    r += src[o + (bgr ? 2 : 0)];
                    g += src[o + 1];
                    b += src[o + (bgr ? 0 : 2)];
                }
            }
            int d = (y * W + x) * 3;
            if (half)
            {
                dst[d] = Encode(r * 0.25f);
                dst[d + 1] = Encode(g * 0.25f);
                dst[d + 2] = Encode(b * 0.25f);
            }
            else
            {
                dst[d] = (byte)(r * 0.25f + 0.5f);
                dst[d + 1] = (byte)(g * 0.25f + 0.5f);
                dst[d + 2] = (byte)(b * 0.25f + 0.5f);
            }
        }
        return dst;
    }

    private static byte Encode(float v) => (byte)(Math.Clamp(v, 0, 1) * 255 + 0.5f);

    /// <summary>
    /// Encodes the kept clip and writes it to <see cref="Folder"/>; <paramref name="done"/> gets the path (or null on
    /// failure) on the main thread. Does nothing while a save runs or when there is no clip.
    /// </summary>
    public static void Save(Action<string> done = null)
    {
        if (_last == null || _saving) return;
        _saving = true;
        Changed?.Invoke();
        var frames = _last;
        string dir = Folder, path = System.IO.Path.Combine(dir, $"brawler-killcam-{DateTime.Now:yyyyMMdd-HHmmss}.gif");
        Task.Run(() =>
        {
            string result = null;
            try
            {
                var bytes = Gif.Encode(frames, W, H, Every * 100 / 60);
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(path, bytes);
                result = path;
            }
            catch (Exception e)
            {
                Log.Info($"kill cam clip not saved: {e.Message}");
            }
            Callable.From(() =>
            {
                _saving = false;
                if (result != null)
                {
                    LastSaved = result;
                    if (frames == _last) Saved = true;
                    Log.Info($"kill cam clip saved: {frames.Count} frames");
                }
                Changed?.Invoke();
                done?.Invoke(result);
            }).CallDeferred();
        });
    }

    /// <summary>Keeps a blank clip of <paramref name="frames"/> frames, so checks can see the save button without a match.</summary>
    public static void KeepBlankForCheck(int frames)
    {
        _last = new List<byte[]>();
        for (int i = 0; i < frames; i++) _last.Add(new byte[W * H * 3]);
        Saved = false;
        Changed?.Invoke();
    }

    /// <summary>Opens the clips folder in the system file browser.</summary>
    public static void OpenFolder()
    {
        System.IO.Directory.CreateDirectory(Folder);
        OS.ShellOpen(Folder);
    }

    /// <summary>The button text for saving, given the current state.</summary>
    public static string ButtonText => _saving ? "SAVING GIF..." : Saved ? "OPEN CLIPS FOLDER" : "SAVE KILL CAM GIF";

    /// <summary>What the save button does now: save, or show the folder once saved.</summary>
    public static void Press()
    {
        if (Saved) OpenFolder();
        else Save();
    }
}
