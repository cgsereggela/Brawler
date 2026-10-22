using System.Collections.Generic;
using System.Linq;
using Brawler.Logic;
using Godot;

namespace Brawler.Core;

/// <summary>
/// Saved arenas: one text file per map in user://maps (the map code, so a file can be shared as is), the level
/// editor's unsaved work, and which arenas the host last played with (both in user://settings.cfg).
/// </summary>
public static class MapLibrary
{
    public static string Dir = "user://maps";
    public static string SettingsPath = "user://settings.cfg";
    private const string Ext = ".map";

    public readonly record struct Entry(string Name, string Code, MapDesign Design);

    /// <summary>Every readable saved map, by name.</summary>
    public static List<Entry> All()
    {
        var list = new List<Entry>();
        using var dir = DirAccess.Open(Dir);
        if (dir == null) return list;
        foreach (var file in dir.GetFiles())
        {
            if (!file.EndsWith(Ext)) continue;
            var code = FileAccess.GetFileAsString($"{Dir}/{file}").Trim();
            if (MapCode.Decode(code) is { } d)
                list.Add(new Entry(d.Name != "" ? d.Name : file[..^Ext.Length], code, d));
        }
        return list.OrderBy(e => e.Name, System.StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Saves under the design's name, replacing a map of the same name. False if it couldn't be written.</summary>
    public static bool Save(MapDesign d)
    {
        DirAccess.MakeDirRecursiveAbsolute(Dir);
        using var f = FileAccess.Open(PathOf(d.Name), FileAccess.ModeFlags.Write);
        if (f == null)
        {
            Log.Info($"could not save map {d.Name}: {FileAccess.GetOpenError()}");
            return false;
        }
        f.StoreString(MapCode.Encode(d) + "\n");
        return true;
    }

    public static bool Exists(string name) => FileAccess.FileExists(PathOf(name));

    /// <summary>True if a saved map is this arena, whatever it was named.</summary>
    public static bool Holds(string code)
    {
        if (MapCode.Decode(code) is not { } d) return false;
        string Unnamed(MapDesign m) { m.Name = ""; return MapCode.Encode(m); }
        var want = Unnamed(d);
        return All().Exists(e => Unnamed(e.Design) == want);
    }

    /// <summary>
    /// The name itself if no saved map has it, otherwise the first free "Name 2", "Name 3"... A count already on the
    /// end is replaced rather than added to, so a copy of "Map 2" is "Map 3", not "Map 2 2".
    /// </summary>
    public static string FreeName(string name)
    {
        name = MapCode.Trim(name);
        if (!Exists(name)) return name;
        var counted = System.Text.RegularExpressions.Regex.Match(name, @"^(.*\S) \d+$");
        var stem = counted.Success ? counted.Groups[1].Value : name;
        for (int n = 2; ; n++)
        {
            var suffix = $" {n}";
            var head = stem;
            while (head.Length > 0 && MapCode.Trim(head + suffix) != head + suffix) head = head[..^1].TrimEnd(); // room for the count
            var next = (head + suffix).Trim();
            if (!Exists(next)) return next;
        }
    }


    public static void Delete(string name)
    {
        if (Exists(name)) DirAccess.RemoveAbsolute(PathOf(name));
    }

    /// <summary>
    /// The file for a map name. Names are cut down to letters, digits, dashes and underscores, so two names can share
    /// a base (my_map for "My Map!" and "My Map?"): the file already holding this name wins, otherwise the first free
    /// one of my_map.map, my_map_2.map, my_map_3.map...
    /// </summary>
    private static string PathOf(string name)
    {
        name = MapCode.Trim(name);
        var safe = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : c == ' ' ? '_' : '-').ToArray());
        var stem = safe.Trim('-', '_') is { Length: > 0 } s ? s : "untitled";
        string free = null;
        for (int n = 1, misses = 0; misses < 10; n++) // keep looking past a few free ones: deleting a map leaves a gap
        {
            var path = $"{Dir}/{stem}{(n == 1 ? "" : $"_{n}")}{Ext}";
            if (!FileAccess.FileExists(path)) { free ??= path; misses++; }
            else if (NameIn(path) == name) return path;
        }
        return free;
    }

    /// <summary>The map name stored in a saved file, or null if it doesn't hold a map.</summary>
    private static string NameIn(string path) => MapCode.Decode(FileAccess.GetFileAsString(path).Trim()) is { } d ? MapCode.Trim(d.Name) : null;

    // ---------- settings.cfg ----------

    /// <summary>The editor's work in progress (a map code), or "" for none.</summary>
    public static string Draft
    {
        get => Get("editor", "draft", "");
        set => Set("editor", "draft", value ?? "");
    }

    /// <summary>The arenas the host last chose for a lobby (map codes), so the next lobby starts with them.</summary>
    public static List<string> LobbyMaps
    {
        get => Get("arenas", "codes", "").Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Where(c => MapCode.Decode(c) != null).ToList();
        set => Set("arenas", "codes", string.Join(" ", value ?? new List<string>()));
    }

    private static string Get(string section, string key, string fallback)
    {
        var cfg = new ConfigFile();
        return cfg.Load(SettingsPath) == Error.Ok ? cfg.GetValue(section, key, fallback).AsString() : fallback;
    }

    private static void Set(string section, string key, string value)
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath); // keep the other sections
        cfg.SetValue(section, key, value);
        if (cfg.Save(SettingsPath) is var err and not Error.Ok) Log.Info($"could not save settings: {err}");
    }
}
