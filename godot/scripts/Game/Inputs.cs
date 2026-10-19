using Brawler.Core;
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

    public LocalInput(Node2D world) => _world = world;

    public static void RegisterActions()
    {
        Bind("move_left", Key.A, Key.Left);
        Bind("move_right", Key.D, Key.Right);
        Bind("jump", Key.W, Key.Space, Key.Up);
        Bind("block", Key.Shift);
        BindMouse("shoot", MouseButton.Left);
        BindMouse("block", MouseButton.Right);
    }

    private static void Bind(string action, params Key[] keys)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
        foreach (var k in keys) InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = k });
    }

    private static void BindMouse(string action, MouseButton b)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventMouseButton { ButtonIndex = b });
    }

    public InputFrame Next(Avatar self, float dt)
    {
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
