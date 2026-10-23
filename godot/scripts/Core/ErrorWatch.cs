using System;
using System.Threading;
using Godot;

namespace Brawler.Core;

/// <summary>
/// Counts errors the game logs while it runs: engine errors, exceptions Godot catches in callbacks (it logs them and
/// carries on), printerr output and <see cref="Log.Error"/>, plus exceptions nothing caught. The check modes fail on
/// any of them, naming the first, so a check can't pass over an exception that fires every battle.
/// </summary>
public partial class ErrorWatch : Logger
{
    private static ErrorWatch _installed;
    private static int _count;
    private static string _first;

    public static int Count => _count;
    public static string First => _first;

    /// <summary>Forget what was counted (for --errorcheck, after its deliberate one).</summary>
    public static void Reset() { _count = 0; _first = null; }

    /// <summary>
    /// A check's end: logs "AUTOTEST PASS/FAIL: why" and quits with its code. A check that met its own condition still
    /// fails if the game logged an error on the way (an exception Godot caught in a callback, say): it would otherwise pass
    /// over a bug that fires every battle.
    /// </summary>
    public static void Finish(SceneTree tree, int code, string why)
    {
        if (code == 0 && Count > 0)
        {
            code = 1;
            why = $"{why}; but the game logged {Count} error(s), the first: {First}";
        }
        Log.Info($"AUTOTEST {(code == 0 ? "PASS" : "FAIL")}: {why}");
        tree.Quit(code);
    }

    /// <summary>Start counting (once per run).</summary>
    public static void Install()
    {
        if (_installed != null) return;
        _installed = new ErrorWatch();
        OS.AddLogger(_installed);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Note($"unhandled {e.ExceptionObject}");
    }

    // Loggers are called from whichever thread logged.
    private static void Note(string what)
    {
        if (Interlocked.Increment(ref _count) == 1) _first = Trim(what);
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType,
        Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        if (errorType == (int)ErrorType.Warning) return;
        string what = string.IsNullOrEmpty(rationale) ? code : $"{rationale} ({code})";
        Note($"{what} at {System.IO.Path.GetFileName(file)}:{line} in {function}");
    }

    public override void _LogMessage(string message, bool error)
    {
        if (error) Note(message);
    }
}
