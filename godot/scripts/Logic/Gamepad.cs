using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>
/// The fixed controller layout. Button and axis numbers are the engine's (SDL) numbering, so A is the bottom face
/// button on any pad. Jump and block sit on the shoulders as well as the face buttons, because aiming with the right
/// stick keeps the right thumb busy.
/// </summary>
public static class PadLayout
{
    public const int A = 0, B = 1, X = 2, Y = 3, Back = 4, Start = 6, LeftStick = 7, RightStick = 8,
        LeftShoulder = 9, RightShoulder = 10, DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14;
    public const int Buttons = 15;

    public static readonly int[] Jump = { A, LeftShoulder };
    public static readonly int[] Shoot = { RightShoulder };      // and the right trigger
    public static readonly int[] Block = { B };                  // and the left trigger
    public static readonly int[] Reload = { X };
    /// <summary>Emotes 1-5: the d-pad, then Y.</summary>
    public static readonly int[] Emotes = { DpadUp, DpadRight, DpadDown, DpadLeft, Y };
    public const int Ping = RightStick;

    /// <summary>How far a trigger has to go down to count as pressed.</summary>
    public const float TriggerDown = 0.4f;
    public const float MoveDead = 0.22f, AimDead = 0.3f;

    /// <summary>The whole layout on two lines, for the Controls tab.</summary>
    public const string Summary =
        "Controller: left stick move · right stick aim · A or LB jump · RT or RB shoot · LT or B block · X reload\n" +
        "D-pad and Y emotes · right stick click ping · Start options. Local co-op: Lobby > + Local Player.";

    /// <summary>The layout as shown in the controls menu.</summary>
    public static readonly (string Action, string Input)[] Help =
    {
        ("Move", "Left stick"),
        ("Aim", "Right stick (or the left stick while the right one rests)"),
        ("Jump", "A / LB"),
        ("Shoot", "RT / RB"),
        ("Block", "LT / B"),
        ("Reload", "X"),
        ("Emotes", "D-pad, Y"),
        ("Ping", "Right stick click"),
        ("Pause / options", "Start"),
        ("Menus", "D-pad or left stick, A to choose, B to go back"),
    };
}

/// <summary>One controller's state at an instant.</summary>
public struct PadState
{
    public float LX, LY, RX, RY, LT, RT;
    public uint Buttons; // bit i = button i held

    public readonly bool Held(int button) => (Buttons & (1u << button)) != 0;
    public readonly bool Any(int[] buttons) { foreach (int b in buttons) if (Held(b)) return true; return false; }
    public readonly bool ShootHeld => Any(PadLayout.Shoot) || RT > PadLayout.TriggerDown;
    public readonly bool BlockHeld => Any(PadLayout.Block) || LT > PadLayout.TriggerDown;
    public readonly bool JumpHeld => Any(PadLayout.Jump);
    public readonly Vector2 Move => Gamepad.Stick(LX, LY, PadLayout.MoveDead);
    public readonly Vector2 Look => Gamepad.Stick(RX, RY, PadLayout.AimDead);
    /// <summary>Anything pressed or pushed past its dead zone: the player is using this pad.</summary>
    public readonly bool Active => Buttons != 0 || Move != Vector2.Zero || Look != Vector2.Zero || LT > PadLayout.TriggerDown || RT > PadLayout.TriggerDown;
}

public static class Gamepad
{
    /// <summary>A stick with a round dead zone, rescaled so it still reaches full deflection: 0 inside, up to length 1.</summary>
    public static Vector2 Stick(float x, float y, float dead)
    {
        var v = new Vector2(x, y);
        float len = v.Length();
        if (len <= dead) return Vector2.Zero;
        float k = Math.Min(1f, (len - dead) / (1f - dead));
        return v / len * k;
    }

    /// <summary>Buttons that went down between two states (bit i = button i).</summary>
    public static uint Pressed(uint before, uint now) => now & ~before;

    /// <summary>
    /// The trigger counted as a button for edge detection: true past the press threshold, with a little hysteresis so a
    /// trigger resting near it doesn't chatter.
    /// </summary>
    public static bool TriggerHeld(float value, bool wasHeld) => wasHeld ? value > PadLayout.TriggerDown - 0.1f : value > PadLayout.TriggerDown;
}

/// <summary>
/// Where a controller aims: the right stick when it's pushed; otherwise the left stick if it's pushed well over
/// (so moving faces you that way); otherwise wherever it last pointed. Starts facing right.
/// </summary>
public sealed class PadAim
{
    public Vector2 Dir { get; private set; } = Vector2.UnitX;
    /// <summary>True while the right stick itself is pushed.</summary>
    public bool Aiming { get; private set; }

    public Vector2 Update(in PadState s)
    {
        var look = s.Look;
        Aiming = look != Vector2.Zero;
        if (Aiming) Dir = Vector2.Normalize(look);
        else if (s.Move.Length() > 0.6f) Dir = Vector2.Normalize(s.Move);
        return Dir;
    }
}

/// <summary>
/// Local co-op players on one machine: guests 1, 2, 3 (the person with the keyboard and mouse is 0), each tied to a
/// controller. Pure, so the tests cover the numbering.
/// </summary>
public sealed class CouchRoster
{
    public const int MaxGuests = 3;
    private readonly SortedDictionary<int, int> _devices = new(); // guest -> device

    public IReadOnlyDictionary<int, int> Guests => _devices;
    public int Count => _devices.Count;

    public bool Claimed(int device) => _devices.ContainsValue(device);

    public int DeviceOf(int guest) => _devices.TryGetValue(guest, out int d) ? d : -1;

    /// <summary>Gives <paramref name="device"/> to a new guest, the lowest free number; -1 if full or the device is taken.</summary>
    public int Add(int device)
    {
        if (Claimed(device) || _devices.Count >= MaxGuests) return -1;
        for (int g = 1; g <= MaxGuests; g++)
            if (!_devices.ContainsKey(g))
            {
                _devices[g] = device;
                return g;
            }
        return -1;
    }

    public bool Remove(int guest) => _devices.Remove(guest);
    public void Clear() => _devices.Clear();
}
