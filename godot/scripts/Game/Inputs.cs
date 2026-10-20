using System.Collections.Generic;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

public interface IInputSource
{
    InputFrame Next(Avatar self, float dt);
}

/// <summary>Keyboard + mouse for the person at this computer.</summary>
public sealed class LocalInput : IInputSource
{
    private byte _jumps, _blocks;
    private readonly Node2D _world;

    /// <summary>While true (for example while a menu is open over the game) the local player gets no input.</summary>
    public static bool Suppressed;

    public LocalInput(Node2D world) => _world = world;

    /// <summary>Creates the input actions with their default bindings; Keybinds.Init then applies the player's own.</summary>
    public static void RegisterActions()
    {
        var defaults = Keybinds.Defaults();
        foreach (var a in BindTable.Actions) Bind(a, defaults.Row(a));
    }

    /// <summary>Replaces an action's inputs (creating the action if needed); empty slots are skipped.</summary>
    public static void Bind(string action, IEnumerable<Binding> bindings)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
        InputMap.ActionEraseEvents(action);
        foreach (var b in bindings)
            if (Keybinds.ToEvent(b) is { } e) InputMap.ActionAddEvent(action, e);
    }

    public InputFrame Next(Avatar self, float dt)
    {
        if (Suppressed) return new InputFrame { JumpPresses = _jumps, BlockPresses = _blocks, Aim = _world.GetGlobalMousePosition() };
        if (Input.IsActionJustPressed("jump")) _jumps++;
        if (Input.IsActionJustPressed("block")) _blocks++;
        return new InputFrame
        {
            MoveX = Input.GetAxis("move_left", "move_right"),
            JumpHeld = Input.IsActionPressed("jump"),
            JumpPresses = _jumps,
            Shoot = Input.IsActionPressed("shoot"),
            BlockPresses = _blocks,
            Aim = _world.GetGlobalMousePosition(),
        };
    }
}

/// <summary>Host side: latest frame received from a remote client.</summary>
public sealed class RemoteInput : IInputSource
{
    public InputFrame Latest;
    public InputFrame Next(Avatar self, float dt) => Latest;
}
