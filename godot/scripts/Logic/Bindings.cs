using System;
using System.Collections.Generic;

namespace Brawler.Logic;

public enum BindKind : byte { None, Key, Mouse }

/// <summary>One input that triggers an action: a physical key code or a mouse button index (engine numbering).</summary>
public readonly record struct Binding(BindKind Kind, long Code)
{
    public bool IsNone => Kind == BindKind.None;
    public static Binding Key(long code) => code == 0 ? default : new(BindKind.Key, code);
    public static Binding Mouse(int button) => MouseNumber(button) == 0 ? default : new(BindKind.Mouse, button);

    /// <summary>Bindable mouse buttons, engine index to the number players see: left 1, right 2, middle 3, side buttons 4 and 5. The wheel is left out.</summary>
    private static readonly int[] MouseIndices = { 1, 2, 3, 8, 9 };

    public static int MouseNumber(int button) => Array.IndexOf(MouseIndices, button) + 1;
    public static int MouseButton(int number) => number >= 1 && number <= MouseIndices.Length ? MouseIndices[number - 1] : 0;
}

/// <summary>
/// The player's bindings: a few slots per action. Pure, so the tests can cover the conflict rules and the save format.
/// Saved as one list of readable tokens per action: a key name ("Space"), "Mouse N", or "" for an empty slot.
/// </summary>
public sealed class BindTable
{
    public const int Slots = 3;
    public static readonly string[] Actions = { "move_left", "move_right", "jump", "shoot", "block", "emote_1", "emote_2", "emote_3", "emote_4", "ping" };

    private readonly Dictionary<string, Binding[]> _map = new();

    public BindTable()
    {
        foreach (var a in Actions) _map[a] = new Binding[Slots];
    }

    public Binding Get(string action, int slot) => _map[action][slot];
    public IReadOnlyList<Binding> Row(string action) => _map[action];

    public BindTable Clone()
    {
        var t = new BindTable();
        foreach (var a in Actions) Array.Copy(_map[a], t._map[a], Slots);
        return t;
    }

    /// <summary>The action and slot holding <paramref name="b"/>, or (null, -1).</summary>
    public (string Action, int Slot) Find(Binding b)
    {
        if (b.IsNone) return (null, -1);
        foreach (var a in Actions)
        {
            int i = Array.IndexOf(_map[a], b);
            if (i >= 0) return (a, i);
        }
        return (null, -1);
    }

    /// <summary>
    /// Puts <paramref name="b"/> in a slot. An input drives one action only, so it is taken out of wherever it was;
    /// returns that other action (null if none, or if it only moved within this action).
    /// </summary>
    public string Assign(string action, int slot, Binding b)
    {
        string from = null;
        var (a, i) = Find(b);
        if (a != null)
        {
            _map[a][i] = default;
            if (a != action) from = a;
        }
        _map[action][slot] = b;
        return from;
    }

    public void Clear(string action, int slot) => _map[action][slot] = default;

    public bool IsUnbound(string action) => Array.TrueForAll(_map[action], b => b.IsNone);

    public bool SameAs(BindTable o)
    {
        foreach (var a in Actions)
            for (int i = 0; i < Slots; i++)
                if (_map[a][i] != o._map[a][i]) return false;
        return true;
    }

    public string[] Tokens(string action, Func<long, string> keyName)
    {
        var row = _map[action];
        var t = new string[Slots];
        for (int i = 0; i < Slots; i++) t[i] = Token(row[i], keyName);
        return t;
    }

    public static string Token(Binding b, Func<long, string> keyName) => b.Kind switch
    {
        BindKind.Key => keyName(b.Code),
        BindKind.Mouse => $"Mouse {Binding.MouseNumber((int)b.Code)}",
        _ => "",
    };

    /// <summary>A token back to a binding; false if it can't be read. <paramref name="keyCode"/> returns 0 for unknown or reserved keys.</summary>
    public static bool TryParse(string token, Func<string, long> keyCode, out Binding b)
    {
        b = default;
        token = (token ?? "").Trim();
        if (token.Length == 0) return true;
        if (token.StartsWith("Mouse ", StringComparison.OrdinalIgnoreCase))
        {
            b = int.TryParse(token[6..], out int n) ? Binding.Mouse(Binding.MouseButton(n)) : default;
            return !b.IsNone;
        }
        b = Binding.Key(keyCode(token));
        return !b.IsNone;
    }

    /// <summary>
    /// Builds a table from saved token lists (<paramref name="read"/> returns null when an action is missing).
    /// An action that is missing or holds anything unreadable gets its defaults. An input listed twice stays with the first action.
    /// </summary>
    public static BindTable Load(Func<string, string[]> read, Func<string, long> keyCode, BindTable defaults)
    {
        var t = new BindTable();
        foreach (var a in Actions)
        {
            var row = Parse(read(a), keyCode) ?? (Binding[])defaults._map[a].Clone();
            for (int i = 0; i < Slots; i++)
                if (!row[i].IsNone && t.Find(row[i]).Action == null) t._map[a][i] = row[i];
        }
        return t;
    }

    private static Binding[] Parse(string[] tokens, Func<string, long> keyCode)
    {
        if (tokens == null || tokens.Length > Slots) return null;
        var row = new Binding[Slots];
        for (int i = 0; i < tokens.Length; i++)
            if (!TryParse(tokens[i], keyCode, out row[i])) return null;
        return row;
    }
}
