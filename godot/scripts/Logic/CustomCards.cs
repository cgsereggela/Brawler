using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Brawler.Logic;

/// <summary>
/// Player-made cards from JSON files (see docs/CUSTOM_CARDS.md). Parses and validates them without touching the
/// engine; Core/CustomCardLoader does the file IO and CardRegistry.SetCustom puts them in play.
///
/// A file holds one card object or an array of them:
///   { "id": 1000, "name": "Pogo", "description": "+2 mid-air jumps", "category": "Movement",
///     "mods": [ { "stat": "AirJumps", "op": "add", "value": 2 } ], "effect": "dash" }
/// Problems never throw: a bad file or card is skipped (a bad value is clamped) and a line goes to the log.
/// </summary>
public static class CustomCards
{
    public const int MinId = 1000;              // built-in ids stay below this
    public const int MaxId = short.MaxValue;    // card ids travel as 16-bit numbers in the card pick message
    public const int MaxCards = 200;            // per set; later cards are skipped
    public const int MaxMods = 12;
    public const int MaxNameLength = 24;
    public const int MaxDescriptionLength = 160;
    public const float MaxMul = 10f;            // "mul" values are clamped to -10..10
    public const float DefaultAddLimit = 1000f; // "add" values are clamped to ± this unless the stat has its own limit

    /// <summary>Effect ids a card may name. Keep in step with Game/CardEffects.Create.</summary>
    public static readonly string[] EffectIds = { "blink", "dash" };

    /// <summary>Tighter "add" limits for stats where a few thousand would break the game (counts, radii, fractions).</summary>
    private static readonly Dictionary<Stat, float> AddLimits = new()
    {
        [Stat.Size] = 2, [Stat.Lifesteal] = 2, [Stat.TeleportOnBlock] = 5,
        [Stat.Ammo] = 40, [Stat.Projectiles] = 24, [Stat.Spread] = 6.3f, [Stat.BurstCount] = 12, [Stat.BurstInterval] = 1,
        [Stat.FireCooldown] = 5, [Stat.ReloadTime] = 10, [Stat.BlockCooldown] = 999, [Stat.BlockDuration] = 3,
        [Stat.BulletRadius] = 14, [Stat.Bounces] = 20, [Stat.Pierce] = 10, [Stat.Homing] = 20, [Stat.ExplosionRadius] = 200,
        [Stat.SplitCount] = 12, [Stat.Lifetime] = 8,
        [Stat.AirJumps] = 5, [Stat.Accel] = 4, [Stat.WallJump] = 4, [Stat.Glide] = 1, [Stat.Jetpack] = 5, [Stat.Dash] = 1500,
        [Stat.Armor] = 0.75f, [Stat.Regen] = 50, [Stat.KnockbackTaken] = 4, [Stat.ExtraLives] = 3, [Stat.Thorns] = 1.5f,
        [Stat.Adrenaline] = 2,
        [Stat.Boomerang] = 1, [Stat.Phase] = 1, [Stat.Sticky] = 10, [Stat.Guided] = 30, [Stat.Chain] = 2, [Stat.Berserk] = 5,
        [Stat.Wild] = 1, [Stat.Leech] = 200, [Stat.Kamikaze] = 300, [Stat.Pinball] = 1, [Stat.Warp] = 1,
        [Stat.Reflect] = 1, [Stat.BlockHeal] = 2, [Stat.BlockReload] = 1, [Stat.BlockRefund] = 1, [Stat.Counter] = 3,
        [Stat.ShieldBash] = 200, [Stat.Shockwave] = 160, [Stat.BlockSpikes] = 3, [Stat.BounceBlast] = 1,
    };

    public static float AddLimit(Stat s) => AddLimits.TryGetValue(s, out var v) ? v : DefaultAddLimit;

    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase) { "id", "name", "description", "category", "mods", "effect" };

    /// <summary>
    /// Every card from several files, in order. The first card with an id wins; later ones are skipped with a warning.
    /// </summary>
    public static List<CardDef> Collect(IEnumerable<(string Source, string Json)> files, List<string> log)
    {
        var result = new List<CardDef>();
        var from = new Dictionary<int, string>();
        foreach (var (source, json) in files)
            foreach (var c in Parse(json, source, log))
            {
                if (from.TryGetValue(c.Id, out var first))
                {
                    log.Add($"{source}: card '{c.Name}' (id {c.Id}): id already used by '{result.Find(r => r.Id == c.Id).Name}' in {first}, skipped");
                    continue;
                }
                if (result.Count >= MaxCards)
                {
                    log.Add($"{source}: card '{c.Name}' (id {c.Id}): more than {MaxCards} custom cards, skipped");
                    continue;
                }
                from[c.Id] = source;
                result.Add(c);
            }
        return result;
    }

    /// <summary>The valid cards in one file's text. <paramref name="source"/> names the file in log lines.</summary>
    public static List<CardDef> Parse(string json, string source, List<string> log)
    {
        var cards = new List<CardDef>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json ?? "", JsonOptions); }
        catch (JsonException e)
        {
            log.Add($"{source}: not valid JSON ({e.Message}), file skipped");
            return cards;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object) Add(root, 1);
            else if (root.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var e in root.EnumerateArray()) Add(e, ++i);
            }
            else log.Add($"{source}: expected a card {{...}} or a list of cards [...], file skipped");
        }
        return cards;

        void Add(JsonElement e, int index)
        {
            var c = ParseCard(e, $"{source}: card #{index}", log);
            if (c != null) cards.Add(c);
        }
    }

    private static CardDef ParseCard(JsonElement e, string where, List<string> log)
    {
        if (e.ValueKind != JsonValueKind.Object) { log.Add($"{where}: not a card object {{...}}, skipped"); return null; }

        string name = Prop(e, "name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        int id = 0;
        bool hasId = Prop(e, "id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out id);
        string label = where + (name != null ? $" '{name}'" : "") + (hasId ? $" (id {id})" : "");
        CardDef Skip(string why) { log.Add($"{label}: {why}, skipped"); return null; }

        if (!hasId) return Skip("needs a whole-number \"id\"");
        if (name == null) return Skip("needs a \"name\" text");

        foreach (var p in e.EnumerateObject())
            if (!KnownFields.Contains(p.Name)) log.Add($"{label}: unknown field \"{p.Name}\" ignored");

        string desc = "";
        if (Prop(e, "description", out var d))
        {
            if (d.ValueKind == JsonValueKind.String) desc = d.GetString();
            else if (d.ValueKind == JsonValueKind.Array)
            {
                // A list of lines reads better than "\n" escapes.
                var lines = new List<string>();
                foreach (var l in d.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.String) lines.Add(l.GetString());
                    else return Skip("\"description\" list must hold only text");
                desc = string.Join("\n", lines);
            }
            else if (d.ValueKind != JsonValueKind.Null) return Skip("\"description\" must be text or a list of text lines");
        }

        var cat = CardCategory.Chaos;
        if (Prop(e, "category", out var catEl) && catEl.ValueKind != JsonValueKind.Null)
        {
            if (catEl.ValueKind != JsonValueKind.String || !TryName(catEl.GetString(), out cat))
                return Skip($"\"category\" must be one of {string.Join(", ", Enum.GetNames<CardCategory>())}");
        }

        string effect = null;
        if (Prop(e, "effect", out var effEl) && effEl.ValueKind != JsonValueKind.Null)
        {
            if (effEl.ValueKind != JsonValueKind.String) return Skip("\"effect\" must be text");
            effect = effEl.GetString();
        }

        var mods = new List<StatMod>();
        if (Prop(e, "mods", out var modsEl) && modsEl.ValueKind != JsonValueKind.Null)
        {
            if (modsEl.ValueKind != JsonValueKind.Array) return Skip("\"mods\" must be a list [...]");
            int k = 0;
            foreach (var m in modsEl.EnumerateArray())
            {
                k++;
                if (m.ValueKind != JsonValueKind.Object) return Skip($"mod #{k} is not an object {{...}}");
                if (!Prop(m, "stat", out var st) || st.ValueKind != JsonValueKind.String)
                    return Skip($"mod #{k} needs a \"stat\" name");
                if (!TryName(st.GetString(), out Stat stat)) return Skip($"mod #{k}: unknown stat \"{st.GetString()}\"");
                if (!Prop(m, "op", out var opEl) || opEl.ValueKind != JsonValueKind.String || !TryName(opEl.GetString(), out ModOp op))
                    return Skip($"mod #{k} ({stat}): \"op\" must be \"add\" or \"mul\"");
                if (!Prop(m, "value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double value))
                    return Skip($"mod #{k} ({stat}): \"value\" must be a number");
                mods.Add(new StatMod(stat, op, (float)Math.Clamp(value, -1e9, 1e9))); // 1e300 would be float infinity; Validate clamps properly

            }
        }

        return Validate(new CardDef { Id = id, Name = name, Description = desc, Category = cat, Mods = mods.ToArray(), EffectId = effect }, label, log);
    }

    /// <summary>
    /// The rules every custom card follows, whether it came from a file or from the host: a copy with long text cut
    /// and out-of-range values clamped (each logged), or null with the reason logged.
    /// </summary>
    public static CardDef Validate(CardDef c, string label, List<string> log)
    {
        CardDef Skip(string why) { log.Add($"{label}: {why}, skipped"); return null; }

        if (c.Id < MinId || c.Id > MaxId) return Skip($"id must be from {MinId} to {MaxId} (below {MinId} is reserved for built-in cards)");
        string name = (c.Name ?? "").Trim();
        if (name.Length == 0) return Skip("name is empty");
        if (name.Length > MaxNameLength)
        {
            log.Add($"{label}: name longer than {MaxNameLength} letters, cut short");
            name = name[..MaxNameLength].TrimEnd();
        }
        string desc = (c.Description ?? "").Replace("\r\n", "\n").Trim();
        if (desc.Length > MaxDescriptionLength)
        {
            log.Add($"{label}: description longer than {MaxDescriptionLength} letters, cut short");
            desc = desc[..MaxDescriptionLength].TrimEnd();
        }
        if (!Enum.IsDefined(c.Category)) return Skip("unknown category");

        string effect = string.IsNullOrWhiteSpace(c.EffectId) ? null : c.EffectId.Trim().ToLowerInvariant();
        if (effect != null && Array.IndexOf(EffectIds, effect) < 0)
            return Skip($"unknown effect \"{c.EffectId}\" (known: {string.Join(", ", EffectIds)})");

        var mods = c.Mods ?? [];
        if (mods.Length == 0 && effect == null) return Skip("has no mods and no effect, so it would do nothing");
        if (mods.Length > MaxMods) return Skip($"more than {MaxMods} mods");
        var clean = new StatMod[mods.Length];
        for (int i = 0; i < mods.Length; i++)
        {
            var m = mods[i];
            if (!Enum.IsDefined(m.Stat) || !Enum.IsDefined(m.Op)) return Skip($"mod #{i + 1} has an unknown stat or op");
            if (!float.IsFinite(m.Value)) return Skip($"mod #{i + 1} ({m.Stat}) value is not a finite number");
            float limit = m.Op == ModOp.Mul ? MaxMul : AddLimit(m.Stat);
            float v = Math.Clamp(m.Value, -limit, limit);
            if (v != m.Value) log.Add($"{label}: {m.Stat} {m.Op.ToString().ToLowerInvariant()} {m.Value} is out of range, clamped to {v}");
            clean[i] = m with { Value = v };
        }
        return new CardDef { Id = c.Id, Name = name, Description = desc, Category = c.Category, Mods = clean, EffectId = effect };
    }

    /// <summary>Case-insensitive field lookup, so "Name" works as well as "name".</summary>
    private static bool Prop(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    /// <summary>Case-insensitive enum lookup by name only (Enum.TryParse would also take "3" or "1,2").</summary>
    private static bool TryName<T>(string s, out T value) where T : struct, Enum
    {
        s = s?.Trim();
        foreach (var n in Enum.GetNames<T>())
            if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) { value = Enum.Parse<T>(n); return true; }
        value = default;
        return false;
    }

    // ---------- network form: the host sends its set to each client that joins ----------

    private const byte WireVersion = 1;

    public static byte[] Serialize(IReadOnlyList<CardDef> cards)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(WireVersion);
            w.Write((ushort)cards.Count);
            foreach (var c in cards)
            {
                w.Write(c.Id);
                w.Write(c.Name ?? "");
                w.Write(c.Description ?? "");
                w.Write((byte)c.Category);
                w.Write(c.EffectId ?? "");
                w.Write((byte)c.Mods.Length);
                foreach (var m in c.Mods)
                {
                    w.Write((byte)m.Stat);
                    w.Write((byte)m.Op);
                    w.Write(m.Value); // exact float bits, so every peer computes identical stats
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>A short code for a set, logged by host and client so a mismatch is easy to spot.</summary>
    public static string Fingerprint(IReadOnlyList<CardDef> cards)
    {
        uint h = 2166136261; // FNV-1a over the network form
        foreach (byte b in Serialize(cards)) { h ^= b; h *= 16777619; }
        return h.ToString("x8");
    }

    /// <summary>
    /// The host's set. Each card goes through <see cref="Validate"/> again, so a broken or hostile message can only
    /// lose cards, never crash the client or slip past the limits.
    /// </summary>
    public static List<CardDef> Deserialize(byte[] data, List<string> log)
    {
        var raw = new List<CardDef>();
        try
        {
            using var r = new BinaryReader(new MemoryStream(data ?? []), Encoding.UTF8);
            if (r.ReadByte() != WireVersion) { log.Add("host cards: unknown format, none used"); return raw; }
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                string name = r.ReadString(), desc = r.ReadString();
                var cat = (CardCategory)r.ReadByte();
                string effect = r.ReadString();
                var mods = new StatMod[r.ReadByte()];
                for (int k = 0; k < mods.Length; k++) mods[k] = new StatMod((Stat)r.ReadByte(), (ModOp)r.ReadByte(), r.ReadSingle());
                raw.Add(new CardDef { Id = id, Name = name, Description = desc, Category = cat, Mods = mods, EffectId = effect.Length > 0 ? effect : null });
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException)
        {
            log.Add($"host cards: message cut short ({e.Message}), none used");
            return new List<CardDef>();
        }

        var result = new List<CardDef>();
        var seen = new HashSet<int>();
        foreach (var c in raw)
        {
            var ok = Validate(c, $"host card '{c.Name}' (id {c.Id})", log);
            if (ok == null) continue;
            if (!seen.Add(ok.Id)) { log.Add($"host card '{c.Name}' (id {c.Id}): duplicate id, skipped"); continue; }
            if (result.Count >= MaxCards) { log.Add($"host card '{c.Name}' (id {c.Id}): more than {MaxCards} custom cards, skipped"); continue; }
            result.Add(ok);
        }
        return result;
    }
}
