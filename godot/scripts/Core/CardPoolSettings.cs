using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Brawler.Core;

/// <summary>
/// The host's card pool (the ids they disabled), kept in the [cards] section of user://settings.cfg and loaded as
/// the default whenever you host or play against bots. Ids of cards that aren't loaded right now are kept as is.
/// </summary>
public static class CardPoolSettings
{
    public static string Path = "user://settings.cfg";

    public static HashSet<int> Load()
    {
        var set = new HashSet<int>();
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return set;
        var v = cfg.GetValue("cards", "disabled", new int[0]);
        // Saved as a PackedInt32Array; a hand-edited [1, 2] list loads as a plain Array.
        if (v.VariantType == Variant.Type.PackedInt32Array) set.UnionWith(v.AsInt32Array());
        else if (v.VariantType == Variant.Type.Array)
            foreach (var e in v.AsGodotArray())
                if (e.VariantType is Variant.Type.Int or Variant.Type.Float) set.Add((int)e);
        set.RemoveWhere(id => id < 0);
        return set;
    }

    public static void Save(IEnumerable<int> disabled)
    {
        var cfg = new ConfigFile();
        cfg.Load(Path); // keep any other sections
        cfg.SetValue("cards", "disabled", disabled.OrderBy(id => id).ToArray());
        var err = cfg.Save(Path);
        if (err != Error.Ok) Log.Info($"could not save settings: {err}");
    }
}
