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

    public static void Delete(string name)
    {
        if (Exists(name)) DirAccess.RemoveAbsolute(PathOf(name));
    }

    /// <summary>A file name for a map name: letters, digits, spaces, dashes and underscores; lowercase.</summary>
    private static string PathOf(string name)
    {
        var safe = new string(MapCode.Trim(name).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : c == ' ' ? '_' : '-').ToArray());
        return $"{Dir}/{(safe.Trim('-', '_') is { Length: > 0 } s ? s : "untitled")}{Ext}";
    }

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
