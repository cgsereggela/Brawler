using System.Collections.Generic;
using Brawler.Logic;
using Godot;

namespace Brawler.Core;

/// <summary>
/// Every connected controller, read once per frame by <see cref="Poll"/> (Main calls it before anything else
/// processes). Keeps the press edges so a tap is seen by whoever asks during the frame, and the local co-op
/// <see cref="Couch"/>: which controller belongs to which guest. A controller nobody claimed drives the player at the
/// keyboard, alongside it.
/// </summary>
public static class Pads
{
    public static readonly CouchRoster Couch = new();

    private sealed class Pad
    {
        public PadState Now, Before;
        public bool LtHeld, RtHeld, LtBefore, RtBefore;
        public uint Pressed;
        public byte Jumps, Blocks; // running press counts, for the physics ticks
    }

    private static readonly Dictionary<int, Pad> _pads = new();
    private static readonly List<int> _devices = new();
    private static readonly Dictionary<int, uint> _tapped = new(); // buttons pressed since the last Poll, per device

    /// <summary>--padcheck: device ids treated as connected, fed with synthetic joypad events.</summary>
    public static readonly List<int> TestDevices = new();

    /// <summary>The devices connected right now.</summary>
    public static IReadOnlyList<int> Devices => _devices;

    /// <summary>True once a controller was the last thing used, until the mouse moves or a key is pressed.</summary>
    public static bool LastUsed { get; private set; }

    /// <summary>
    /// The engine's menu actions move focus with the d-pad and left stick but have no button to press or go back: add
    /// A to ui_accept and B to ui_cancel, for every controller.
    /// </summary>
    public static void RegisterMenuButtons()
    {
        foreach (var (action, button) in new[] { ("ui_accept", PadLayout.A), ("ui_cancel", PadLayout.B) })
            InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = (JoyButton)button, Device = -1 });
    }

    public static void Poll()
    {
        _devices.Clear();
        foreach (int d in Input.GetConnectedJoypads()) _devices.Add(d);
        foreach (int d in TestDevices) if (!_devices.Contains(d)) _devices.Add(d);
        foreach (int d in _devices)
        {
            if (!_pads.TryGetValue(d, out var p)) _pads[d] = p = new Pad();
            p.Before = p.Now;
            p.LtBefore = p.LtHeld;
            p.RtBefore = p.RtHeld;
            p.Now = Read(d);
            p.LtHeld = Gamepad.TriggerHeld(p.Now.LT, p.LtHeld);
            p.RtHeld = Gamepad.TriggerHeld(p.Now.RT, p.RtHeld);
            p.Pressed = Gamepad.Pressed(p.Before.Buttons, p.Now.Buttons) | (_tapped.TryGetValue(d, out uint tap) ? tap : 0);
            if ((p.Pressed & Mask(PadLayout.Jump)) != 0) p.Jumps++;
            if ((p.Pressed & Mask(PadLayout.Block)) != 0 || (p.LtHeld && !p.LtBefore)) p.Blocks++;
            if (p.Now.Active && (p.Pressed != 0 || p.Now.Move != System.Numerics.Vector2.Zero || p.Now.Look != System.Numerics.Vector2.Zero)) LastUsed = true;
        }
        _tapped.Clear();
        foreach (var d in new List<int>(_pads.Keys))
            if (!_devices.Contains(d)) _pads.Remove(d);
    }

    /// <summary>
    /// Main._Input: a button went down. Kept until the next <see cref="Poll"/>, so a tap released before then still counts
    /// as a press: reading the buttons once a frame lost quick taps whenever a frame ran long.
    /// </summary>
    public static void Note(InputEvent e)
    {
        if (e is not InputEventJoypadButton { Pressed: true } b || (int)b.ButtonIndex >= PadLayout.Buttons) return;
        _tapped[b.Device] = (_tapped.TryGetValue(b.Device, out uint m) ? m : 0) | 1u << (int)b.ButtonIndex;
    }

    /// <summary>Main: the mouse moved or a key went down, so prompts and focus go back to mouse-and-keyboard mode.</summary>
    public static void KeyboardUsed() => LastUsed = false;

    private static PadState Read(int d)
    {
        var s = new PadState
        {
            LX = Input.GetJoyAxis(d, JoyAxis.LeftX), LY = Input.GetJoyAxis(d, JoyAxis.LeftY),
            RX = Input.GetJoyAxis(d, JoyAxis.RightX), RY = Input.GetJoyAxis(d, JoyAxis.RightY),
            LT = Input.GetJoyAxis(d, JoyAxis.TriggerLeft), RT = Input.GetJoyAxis(d, JoyAxis.TriggerRight),
        };
        for (int b = 0; b < PadLayout.Buttons; b++)
            if (Input.IsJoyButtonPressed(d, (JoyButton)b)) s.Buttons |= 1u << b;
        return s;
    }

    private static uint Mask(int[] buttons)
    {
        uint m = 0;
        foreach (int b in buttons) m |= 1u << b;
        return m;
    }

    /// <summary>Running counts of jump and block presses on a controller (they wrap), so a tap between two physics ticks is never lost.</summary>
    public static (byte Jumps, byte Blocks) Presses(int device) => _pads.TryGetValue(device, out var p) ? (p.Jumps, p.Blocks) : default;

    public static PadState State(int device) => _pads.TryGetValue(device, out var p) ? p.Now : default;

    public static bool JustPressed(int device, int button) => _pads.TryGetValue(device, out var p) && (p.Pressed & (1u << button)) != 0;

    public static bool JustPressedAny(int device, int[] buttons)
    {
        foreach (int b in buttons) if (JustPressed(device, b)) return true;
        return false;
    }

    public static bool TriggerJustPressed(int device, bool right) =>
        _pads.TryGetValue(device, out var p) && (right ? p.RtHeld && !p.RtBefore : p.LtHeld && !p.LtBefore);

    /// <summary>Controllers that belong to the keyboard player: every one no guest has claimed.</summary>
    public static IEnumerable<int> Unclaimed()
    {
        foreach (int d in _devices)
            if (!Couch.Claimed(d)) yield return d;
    }

    /// <summary>A short name for a controller, e.g. "Xbox Controller", for the lobby.</summary>
    public static string Name(int device)
    {
        string n = Input.GetJoyName(device);
        return string.IsNullOrWhiteSpace(n) ? $"Controller {device + 1}" : n;
    }
}
