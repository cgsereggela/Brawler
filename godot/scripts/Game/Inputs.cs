using System.Collections.Generic;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

public interface IInputSource
{
    InputFrame Next(Avatar self, float dt);
}

/// <summary>Keyboard + mouse for the person at this computer, plus any controller no couch guest has claimed.</summary>
public sealed class LocalInput : IInputSource
{
    private byte _jumps, _blocks;
    private readonly Node2D _world;

    /// <summary>While true (for example while a menu is open over the game) the local player gets no input.</summary>
    public static bool Suppressed;

    private readonly PadReader _pad = new();
    private Vector2 _lastMouse = new(-1, -1);

    /// <summary>True while a controller is aiming for this player (until the mouse moves): the crosshair follows the aim, not the mouse.</summary>
    public bool UsingPad { get; private set; }

    public Vector2 PadAimDir => _pad.AimDir;

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
        var mouse = _world.GetGlobalMousePosition();
        var screenMouse = _world.GetViewport().GetMousePosition();
        if (screenMouse.DistanceSquaredTo(_lastMouse) > 4) UsingPad = false; // the mouse moved: aim with it again
        _lastMouse = screenMouse;
        if (Suppressed)
        {
            var idle = new InputFrame();
            _pad.Merge(ref idle, Pads.Unclaimed(), self); // read and drop, so presses made under a menu don't land after it closes
            return new InputFrame { JumpPresses = _jumps, BlockPresses = _blocks, Aim = UsingPad && self != null ? _pad.AimAt(self) : mouse };
        }
        if (Input.IsActionJustPressed("jump")) _jumps++;
        if (Input.IsActionJustPressed("block")) _blocks++;
        var f = new InputFrame
        {
            MoveX = Input.GetAxis("move_left", "move_right"),
            JumpHeld = Input.IsActionPressed("jump"),
            Shoot = Input.IsActionPressed("shoot"),
            Reload = Input.IsActionPressed("reload"),
            Aim = mouse,
        };
        // Any controller no couch guest has claimed drives this player too.
        if (_pad.Merge(ref f, Pads.Unclaimed(), self)) UsingPad = true;
        f.JumpPresses = _jumps += _pad.Jumps;
        f.BlockPresses = _blocks += _pad.Blocks;
        if (UsingPad && self != null) f.Aim = _pad.AimAt(self);
        return f;
    }
}

/// <summary>A local co-op guest: everything from their own controller.</summary>
public sealed class PadInput : IInputSource
{
    public readonly int Device;
    private readonly PadReader _pad = new();
    private byte _jumps, _blocks;

    public PadInput(int device) => Device = device;

    public InputFrame Next(Avatar self, float dt)
    {
        var f = new InputFrame { Aim = self?.GlobalPosition ?? Vector2.Zero };
        _pad.Merge(ref f, new[] { Device }, self); // the options overlay mutes only the keyboard player
        f.JumpPresses = _jumps += _pad.Jumps;
        f.BlockPresses = _blocks += _pad.Blocks;
        if (self != null) f.Aim = _pad.AimAt(self);
        return f;
    }

    /// <summary>Where this guest's crosshair goes: along their aim, at the same reach as the stick aim point.</summary>
    public Vector2 AimDir => _pad.AimDir;
}

/// <summary>Turns controller state into input frames: shared by the keyboard player (spare controllers) and couch guests.</summary>
public sealed class PadReader
{
    /// <summary>How far ahead of the player the stick aim point sits, and how far a ghost drifts per tick of stick.</summary>
    public const float Reach = 150, GhostReach = 90;

    private readonly PadAim _aim = new();
    private readonly Dictionary<int, (byte J, byte B)> _seen = new();
    private System.Numerics.Vector2 _ghost;
    /// <summary>Jump and block presses since the last <see cref="Merge"/>.</summary>
    public byte Jumps, Blocks;

    /// <summary>Where the stick aims, after aim assist (see <see cref="AimAt"/>).</summary>
    public Vector2 AimDir => _assisted ?? new Vector2(_aim.Dir.X, _aim.Dir.Y);
    private Vector2? _assisted;

    /// <summary>
    /// Adds what the given controllers hold to <paramref name="f"/> (sticks only where the keyboard is idle). Returns
    /// true when one of them was used this tick.
    /// </summary>
    public bool Merge(ref InputFrame f, IEnumerable<int> devices, Avatar self)
    {
        Jumps = Blocks = 0;
        bool used = false;
        foreach (int d in devices)
        {
            var s = Pads.State(d);
            var (j, b) = Pads.Presses(d);
            if (_seen.TryGetValue(d, out var last))
            {
                Jumps += (byte)(j - last.J);
                Blocks += (byte)(b - last.B);
            }
            _seen[d] = (j, b);
            if (!s.Active) continue;
            used = true;
            var move = s.Move;
            if (f.MoveX == 0 && move.X != 0) f.MoveX = move.X;
            f.JumpHeld |= s.JumpHeld;
            f.Shoot |= s.ShootHeld;
            f.Reload |= s.Any(PadLayout.Reload);
            _aim.Update(s);
            // A ghost floats where the sticks push it.
            var push = s.Look != System.Numerics.Vector2.Zero ? s.Look : move;
            _ghost = push;
        }
        if (!used) _ghost = System.Numerics.Vector2.Zero;
        return used;
    }

    public Vector2 AimAt(Avatar self)
    {
        _assisted = null;
        if (!self.Alive) return self.GlobalPosition + new Vector2(_ghost.X, _ghost.Y) * GhostReach;
        // Aim assist works while the right stick is pushed (a resting stick doesn't track anyone by itself), and pulls
        // toward enemies only: a teammate in the way is left alone.
        var from = ToNum(self.GlobalPosition);
        var foes = new List<System.Numerics.Vector2>();
        if (self.Game != null && _aim.Aiming && Accessibility.AimAssist > 0)
            foreach (var a in self.Game.LiveAvatars())
                if (a != self && !self.Game.Allies(a.Player.Slot, self.Player.Slot)) foes.Add(ToNum(a.GlobalPosition));
        var d = AimAssist.Bend(_aim.Dir, from, foes, Accessibility.AimAssist);
        _assisted = new Vector2(d.X, d.Y);
        return self.GlobalPosition + _assisted.Value * Reach;
    }

    private static System.Numerics.Vector2 ToNum(Vector2 v) => new(v.X, v.Y);
}

/// <summary>Host side: latest frame received from a remote client.</summary>
public sealed class RemoteInput : IInputSource
{
    public InputFrame Latest;
    public InputFrame Next(Avatar self, float dt) => Latest;
}
