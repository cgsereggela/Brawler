using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.Core;

/// <summary>
/// The player's key and mouse bindings (a <see cref="BindTable"/>), applied to the InputMap and kept in section
/// "controls" of user://settings.cfg as readable token lists, e.g. jump=PackedStringArray("W", "Space", "Up").
/// </summary>
public static class Keybinds
{
    private const string Path = "user://settings.cfg", Section = "controls";
    private const int Version = 1;

    public static BindTable Current { get; private set; } = Defaults();

    /// <summary>Raised after any change, so hints that show a binding can refresh.</summary>
    public static event Action Changed;

    public static BindTable Defaults()
    {
        var t = new BindTable();
        void Set(string action, params Binding[] row) { for (int i = 0; i < row.Length; i++) t.Assign(action, i, row[i]); }
        Set("move_left", K(Key.A), K(Key.Left));
        Set("move_right", K(Key.D), K(Key.Right));
        Set("jump", K(Key.W), K(Key.Space), K(Key.Up));
        Set("shoot", Binding.Mouse((int)MouseButton.Left));
        Set("block", K(Key.Shift), Binding.Mouse((int)MouseButton.Right));
        Set("reload", K(Key.R));
        Set("emote_1", K(Key.Key1));
        Set("emote_2", K(Key.Key2));
        Set("emote_3", K(Key.Key3));
        Set("emote_4", K(Key.Key4));
        Set("emote_5", K(Key.Key5));
        Set("ping", K(Key.Q), Binding.Mouse((int)MouseButton.Middle));
        return t;
    }

    private static Binding K(Key k) => Binding.Key((long)k);

    /// <summary>Called once at startup, right after LocalInput.RegisterActions() sets up the defaults.</summary>
    public static void Init()
    {
        if (OS.GetCmdlineUserArgs().Contains("--rebindcheck"))
        {
            RebindCheck();
            return;
        }
        Current = Load(Path);
        Apply();
    }

    public static string Assign(string action, int slot, Binding b) => Change(t => t.Assign(action, slot, b));
    public static void Clear(string action, int slot) => Change(t => { t.Clear(action, slot); return null; });
    public static void ResetToDefaults() => Change(t => { Current = Defaults(); return null; });

    private static string Change(Func<BindTable, string> edit)
    {
        string result = edit(Current);
        Apply();
        Save(Path);
        Changed?.Invoke();
        return result;
    }

    public static void Apply()
    {
        foreach (var a in BindTable.Actions) LocalInput.Bind(a, Current.Row(a));
    }

    /// <summary>The binding a key or mouse press would make; none for anything else, and for Esc, which menus keep.</summary>
    public static Binding FromEvent(InputEvent e) => e switch
    {
        InputEventKey { Pressed: true, Echo: false } k when Physical(k) != Key.Escape && Physical(k) != Key.None => Binding.Key((long)Physical(k)),
        InputEventMouseButton { Pressed: true } m => Binding.Mouse((int)m.ButtonIndex),
        _ => default,
    };

    private static Key Physical(InputEventKey k) => k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;

    public static InputEvent ToEvent(Binding b) => b.Kind switch
    {
        BindKind.Key => new InputEventKey { PhysicalKeycode = (Key)b.Code },
        BindKind.Mouse => new InputEventMouseButton { ButtonIndex = (MouseButton)b.Code },
        _ => null,
    };

    /// <summary>What the player sees: the key printed on their keyboard at that position (so AZERTY shows Z, not W), or "Mouse N".</summary>
    public static string Label(Binding b)
    {
        if (b.Kind != BindKind.Key) return BindTable.Token(b, null);
        var phys = (Key)b.Code;
        // The headless display server has no keyboard layout to ask.
        var local = DisplayServer.GetName() == "headless" ? Key.None : DisplayServer.KeyboardGetKeycodeFromPhysical(phys);
        string s = OS.GetKeycodeString(local != Key.None ? local : phys);
        return string.IsNullOrEmpty(s) ? OS.GetKeycodeString(phys) : s;
    }

    /// <summary>The bound inputs of an action joined with "/", e.g. "W/Space/Up".</summary>
    public static string Hint(string action) =>
        string.Join("/", Current.Row(action).Where(b => !b.IsNone).Select(Label).DefaultIfEmpty("unbound"));

    // ---- save / load ----

    // Key names are the engine's names for the physical (US-layout) position, so the file reads the same on any layout.
    private static string KeyName(long code) => OS.GetKeycodeString((Key)code);

    private static long KeyCode(string name)
    {
        var k = OS.FindKeycodeFromString(name);
        return k == Key.Escape || k == Key.Unknown ? 0 : (long)k;
    }

    private static BindTable Load(string path)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(path) != Error.Ok || !cfg.HasSection(Section)) return Defaults();
        return BindTable.Load(a =>
        {
            if (!cfg.HasSectionKey(Section, a)) return null;
            var v = cfg.GetValue(Section, a);
            return v.VariantType switch
            {
                Variant.Type.PackedStringArray => v.AsStringArray(),
                Variant.Type.Array => v.AsGodotArray().Select(x => x.VariantType == Variant.Type.String ? x.AsString() : "\u0001").ToArray(),
                _ => null,
            };
        }, KeyCode, Defaults());
    }

    private static void Save(string path)
    {
        var cfg = new ConfigFile();
        cfg.Load(path); // keep any other sections
        cfg.SetValue(Section, "version", Version);
        foreach (var a in BindTable.Actions) cfg.SetValue(Section, a, Current.Tokens(a, KeyName));
        var err = cfg.Save(path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }

    /// <summary>
    /// --rebindcheck: rebind jump's first slot to K, save to a scratch file, reload, and confirm the InputMap has K
    /// (and lost W). Also checks that a damaged file falls back to defaults. Never touches settings.cfg. Prints REBINDCHECK and quits.
    /// </summary>
    private static void RebindCheck()
    {
        const string scratch = "user://rebindcheck.cfg";
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(scratch));
        var fails = new List<string>();

        Current = Defaults();
        string from = Current.Assign("jump", 0, K(Key.K));
        Save(scratch);
        string saved = FileAccess.GetFileAsString(scratch);
        Current = Defaults();
        Apply();
        Current = Load(scratch);
        Apply();
        var jump = InputMap.ActionGetEvents("jump").OfType<InputEventKey>().Select(e => e.PhysicalKeycode).ToList();
        if (!jump.Contains(Key.K)) fails.Add("jump lacks K");
        if (jump.Contains(Key.W)) fails.Add("jump still has W");
        if (!jump.Contains(Key.Space) || !jump.Contains(Key.Up)) fails.Add("jump lost Space/Up");
        if (from != null) fails.Add($"K was taken from {from}");

        // Taking block's Shift for jump moves it.
        Current.Assign("jump", 1, K(Key.Shift));
        Apply();
        if (InputMap.ActionGetEvents("block").OfType<InputEventKey>().Any()) fails.Add("block kept Shift");

        var cfg = new ConfigFile();
        cfg.SetValue(Section, "jump", "not a list");
        cfg.SetValue(Section, "shoot", new[] { "Mouse 99" });
        cfg.SetValue(Section, "block", new[] { "Escape" });
        cfg.Save(scratch);
        var bad = Load(scratch);
        if (!bad.SameAs(Defaults())) fails.Add("bad data did not fall back to defaults");

        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(scratch));
        Current = Defaults();
        Apply();
        Log.Info($"saved as:\n{saved}");
        string after = $"jump after reload: {string.Join(", ", jump.Select(k => OS.GetKeycodeString(k)))}";
        Log.Info($"REBINDCHECK {(fails.Count == 0 ? "PASS" : "FAIL " + string.Join("; ", fails))} ({after})");
        ErrorWatch.Finish((SceneTree)Engine.GetMainLoop(), fails.Count == 0 ? 0 : 1, fails.Count == 0 ? $"rebindcheck: {after}" : $"rebindcheck: {string.Join("; ", fails)}");
    }
}
