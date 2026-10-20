using System.Collections.Generic;
using System.IO;
using System.Linq;
using Brawler.Logic;
using Godot;

namespace Brawler.Core;

/// <summary>
/// Reads the player's custom card files (user://cards/*.json) at startup and puts them in play.
/// Parsing and the rules live in Logic/CustomCards; this only does the file IO and logging.
/// In someone else's lobby the host's set replaces this one (NetworkManager.SyncCards) until you leave.
/// </summary>
public static class CustomCardLoader
{
    public const string DefaultDir = "user://cards";

    /// <summary>This player's own cards, as loaded from disk.</summary>
    public static IReadOnlyList<CardDef> Local { get; private set; } = [];

    /// <summary>Loads every *.json in <paramref name="dir"/> (default user://cards) in name order and registers the cards.</summary>
    public static void Load(string dir = null)
    {
        string path = ProjectSettings.GlobalizePath(dir ?? DefaultDir);
        var files = new List<(string, string)>();
        try
        {
            // Make the default folder so players can find it; an override (--carddir) must already exist.
            if (dir == null) Directory.CreateDirectory(path);
            if (Directory.Exists(path))
                foreach (var f in Directory.GetFiles(path, "*.json").OrderBy(f => f, System.StringComparer.OrdinalIgnoreCase))
                {
                    try { files.Add((Path.GetFileName(f), File.ReadAllText(f))); }
                    catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException)
                    {
                        Log.Info($"custom cards: {Path.GetFileName(f)}: could not read ({e.Message}), file skipped");
                    }
                }
            else Log.Info($"custom cards: folder {path} not found");
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException)
        {
            Log.Info($"custom cards: could not open {path} ({e.Message})");
        }

        var log = new List<string>();
        Local = CustomCards.Collect(files, log);
        foreach (var line in log) Log.Info($"custom cards: {line}");
        if (files.Count > 0)
            Log.Info($"custom cards: loaded {Local.Count} from {files.Count} file(s) in {path} [set {CustomCards.Fingerprint(Local)}]" +
                     (Local.Count > 0 ? $": {string.Join(", ", Local.Select(c => $"{c.Name} ({c.Id})"))}" : ""));
        else Log.Info($"custom cards: none (put .json card files in {path})");
        RestoreLocal();
    }

    /// <summary>Back to this player's own set (after leaving a lobby that used the host's).</summary>
    public static void RestoreLocal()
    {
        if (_usingHost) Log.Info($"custom cards: back to your own {Local.Count} custom card(s) [set {CustomCards.Fingerprint(Local)}]");
        _usingHost = false;
        CardRegistry.SetCustom(Local);
    }

    private static bool _usingHost;

    /// <summary>Uses the host's cards for this session, in place of the local set.</summary>
    public static void UseHostCards(byte[] data)
    {
        var log = new List<string>();
        var cards = CustomCards.Deserialize(data, log);
        foreach (var line in log) Log.Info($"custom cards: {line}");
        CardRegistry.SetCustom(cards);
        _usingHost = true;
        Log.Info($"custom cards: using the host's {cards.Count} custom card(s) [set {CustomCards.Fingerprint(cards)}]" +
                 (cards.Count > 0 ? $": {string.Join(", ", cards.Select(c => $"{c.Name} ({c.Id})"))}" : ""));
    }
}
