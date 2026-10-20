using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Brawler.Core;

/// <summary>
/// Window mode, kept in user://settings.cfg as video/window_mode. Windowed is a framed 1600×900 window; Borderless is a
/// frameless window covering its screen ("windowed full screen"); Fullscreen is Godot's exclusive full screen. Alt+Enter flips between
/// Windowed and the last full-screen choice. Does nothing headless, and leaves the window alone at startup when Godot's
/// own window flags (--fullscreen, --windowed, --resolution, ...) were given.
/// </summary>
public static class DisplaySettings
{
    public enum WindowChoice { Windowed, Borderless, Fullscreen }

    private const string Path = "user://settings.cfg";
    private static readonly string[] Keys = { "windowed", "borderless", "fullscreen" };
    private static readonly string[] EngineWindowArgs = { "--fullscreen", "-f", "--windowed", "-w", "--maximized", "-m", "--resolution", "--position" };
    private static readonly Vector2I DefaultSize = new(1600, 900);

    public static WindowChoice Mode { get; private set; } = WindowChoice.Windowed;

    /// <summary>Fired after the mode changes (menu or Alt+Enter), so an open menu can refresh.</summary>
    public static event Action Changed;

    private static WindowChoice _lastFull = WindowChoice.Borderless;
    private static Vector2I _windowedSize = DefaultSize;
    private static bool _dryRun;

    /// <summary>False when running headless: there is no window to change.</summary>
    public static bool Available => DisplayServer.GetName() != "headless";

    /// <summary>Called once at startup, before any screen is shown.</summary>
    public static void Init()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) == Error.Ok) Mode = Parse(cfg.GetValue("video", "window_mode", Keys[0]).ToString());
        if (Mode != WindowChoice.Windowed) _lastFull = Mode;
        if (!Available) return;

        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Root.CallDeferred(Node.MethodName.AddChild, new WindowHotkey());
        if (OS.GetCmdlineUserArgs().Contains("--windowcheck")) { _ = WindowCheck(tree); return; }
        if (OS.GetCmdlineArgs().Any(a => EngineWindowArgs.Contains(a))) return; // the command line wins for this run
        if (Mode != WindowChoice.Windowed) Apply(Mode);
    }

    /// <summary>Switches the window now and saves the choice.</summary>
    public static void Set(WindowChoice choice)
    {
        Mode = choice;
        if (choice != WindowChoice.Windowed) _lastFull = choice;
        Apply(choice);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Alt+Enter: Windowed to the last full-screen choice (Borderless by default) and back.</summary>
    public static void Toggle() => Set(Mode == WindowChoice.Windowed ? _lastFull : WindowChoice.Windowed);

    private static void Save()
    {
        if (_dryRun) return;
        var cfg = new ConfigFile();
        cfg.Load(Path); // keep any other sections
        cfg.SetValue("video", "window_mode", Keys[(int)Mode]);
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }

    private static WindowChoice Parse(string s)
    {
        int i = Array.IndexOf(Keys, s.ToLowerInvariant());
        return i < 0 ? WindowChoice.Windowed : (WindowChoice)i;
    }

    /// <summary>
    /// Borderless is Godot's WindowMode.Fullscreen: on Windows that is a frameless window over the whole screen, made 2px
    /// wider off the right edge (transparent, reported size unchanged) so the driver never promotes it to exclusive. A
    /// hand-made borderless window exactly the screen's size would be, and Godot then reports it as ExclusiveFullscreen.
    /// All modes act on the window's current screen.
    /// </summary>
    private static void Apply(WindowChoice choice)
    {
        if (!Available) return;
        int screen = DisplayServer.WindowGetCurrentScreen();
        var current = DisplayServer.WindowGetMode();
        if (current == DisplayServer.WindowMode.Windowed && !DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless))
            _windowedSize = DisplayServer.WindowGetSize(); // restored when going back to Windowed

        switch (choice)
        {
            case WindowChoice.Windowed:
            {
                if (current != DisplayServer.WindowMode.Windowed) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
                var area = DisplayServer.ScreenGetUsableRect(screen);
                var size = WindowedSize(area);
                DisplayServer.WindowSetSize(size);
                DisplayServer.WindowSetPosition(area.Position + (area.Size - size) / 2);
                break;
            }
            case WindowChoice.Borderless:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                break;
            case WindowChoice.Fullscreen:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
                break;
        }
    }

    /// <summary>The last windowed size, shrunk to fit the screen's work area with room for the frame and title bar.</summary>
    private static Vector2I WindowedSize(Rect2I area) => new(
        Mathf.Clamp(_windowedSize.X, 320, Math.Max(320, area.Size.X - 16)),
        Mathf.Clamp(_windowedSize.Y, 180, Math.Max(180, area.Size.Y - 48)));

    /// <summary>
    /// --windowcheck: cycles the modes (and Alt+Enter) without saving, logs the window after each and checks Borderless
    /// and Fullscreen cover the screen and Windowed comes back at its size, centred. Quits 0 if every step passed.
    /// </summary>
    private static async Task WindowCheck(SceneTree tree)
    {
        _dryRun = true;
        int fails = 0;
        async Task Settle() => await tree.ToSignal(tree.CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        void Report(string label, WindowChoice expect)
        {
            int s = DisplayServer.WindowGetCurrentScreen();
            var mode = DisplayServer.WindowGetMode();
            bool borderless = DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless);
            var rect = new Rect2I(DisplayServer.WindowGetPosition(), DisplayServer.WindowGetSize());
            var screenRect = new Rect2I(DisplayServer.ScreenGetPosition(s), DisplayServer.ScreenGetSize(s));
            var area = DisplayServer.ScreenGetUsableRect(s);
            bool ok = expect switch
            {
                WindowChoice.Windowed => mode == DisplayServer.WindowMode.Windowed && !borderless && rect.Size == WindowedSize(area)
                    && (rect.GetCenter() - area.GetCenter()).Abs() <= Vector2I.One * 2,
                WindowChoice.Borderless => mode == DisplayServer.WindowMode.Fullscreen && rect == screenRect,
                _ => mode == DisplayServer.WindowMode.ExclusiveFullscreen && rect == screenRect,
            };
            if (!ok) fails++;
            Log.Info($"WINDOWCHECK {label,-22} setting={Mode,-10} mode={mode} borderless={borderless} pos={rect.Position} size={rect.Size} " +
                     $"screen{s}={screenRect.Position}+{screenRect.Size} usable={area.Position}+{area.Size} {(ok ? "OK" : "FAIL")}");
        }

        try
        {
            await Settle();
            Report("start", WindowChoice.Windowed);


            foreach (var c in new[] { WindowChoice.Borderless, WindowChoice.Fullscreen, WindowChoice.Borderless, WindowChoice.Windowed,
                                      WindowChoice.Fullscreen, WindowChoice.Windowed })
            {
                Set(c);
                await Settle();
                Report(c.ToString(), c);
            }
            foreach (var expect in new[] { WindowChoice.Fullscreen, WindowChoice.Windowed })
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, AltPressed = true, Pressed = true });
                await Settle();
                Report("Alt+Enter", expect);
            }
        }
        catch (Exception e)
        {
            fails++;
            Log.Info($"WINDOWCHECK error: {e}");
        }
        Log.Info($"WINDOWCHECK done, {fails} failed");
        tree.Quit(fails == 0 ? 0 : 1);
    }
}

/// <summary>Alt+Enter toggles full screen from any screen, before the UI can treat Enter as a click.</summary>
public partial class WindowHotkey : Node
{
    public WindowHotkey() => ProcessMode = ProcessModeEnum.Always;

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, AltPressed: true } k || k.Keycode is not (Key.Enter or Key.KpEnter)) return;
        DisplaySettings.Toggle();
        GetViewport().SetInputAsHandled();
    }
}
